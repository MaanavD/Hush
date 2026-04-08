// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Threading.Channels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hush.Core.Transcription;

/// <summary>
/// Wraps Foundry Local live transcription while preserving the app-level
/// <see cref="TranscriptionResult"/> abstraction.
/// </summary>
public sealed class TranscriptionEngine : ITranscriptionEngine
{
    private readonly ILogger<TranscriptionEngine> _logger;
    private readonly ILiveAudioSessionFactory _liveSessionFactory;

    private OpenAIAudioClient? _audioClient;
    private string? _modelId;
    private Channel<TranscriptionResult>? _resultChannel;
    private ILiveAudioSession? _liveSession;
    private Task? _resultPumpTask;
    // Exact text that has been irrevocably committed (typed into the target app).
    private string _committedText = string.Empty;
    // When true, words are committed progressively as they stabilise.
    // When false, all non-final text is held until TryFlushRemaining.
    private bool _streamingCommit = true;
    // Committed text from all previously completed segments. When a final chunk
    // arrives, it marks a segment boundary; non-final chunk text in the NEXT
    // segment is composed as _segmentBase + " " + chunkText so that the
    // stability comparison works across segment boundaries.
    private string _segmentBase = string.Empty;
    // Full accumulated text for the active segment. Non-final SDK chunks are not
    // always cumulative; some behave like rolling windows. We merge them into a
    // monotonic segment hypothesis so live typing can grow forward without
    // repeatedly erasing earlier words.
    private string _lastFullText = string.Empty;
    // Word list for the previous accumulated non-final segment hypothesis. Used to
    // determine which words have stabilised between updates.
    private string[] _prevChunkWords = Array.Empty<string>();
    // Approximate audio end time for the text currently stored in _committedText.
    // Used to detect when the SDK emits a later rolling window that should be
    // appended instead of treated as a rewrite of earlier words.
    private TimeSpan? _lastCommittedEndTime;
    private bool _disposed;

    // In streaming mode, commit the full monotonic interim hypothesis. Non-final
    // rewrites are already blocked below, so this favors responsive live typing
    // while still deferring corrections to final chunks.
    private const int StreamingTrailingWordHoldback = 0;
    // Allow a small timestamp overlap when the SDK rolls windows forward so a
    // later chunk can still be treated as additive speech instead of a rewrite.
    private static readonly TimeSpan DetachedChunkOverlapTolerance = TimeSpan.FromMilliseconds(150);

    // Whisper hallucination tokens that should never be typed or shown.
    private static readonly HashSet<string> NoiseTokens =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "[blank_audio]", "(silence)", "[silence]", "[music]",
            "[inaudible]", "[ Silence ]", "(music)"
        };

    // Check for blatant repetition artifacts from live ASR (e.g. "kkkking", "aaaa").
    // Fires when a single character makes up ≥60% of a word-only token,
    // or when 3+ consecutive identical characters appear.
    private static bool IsRepetitionArtifact(string text)
    {
        if (text.Length < 3) return false;
        // Only applies when the token contains no whitespace (single word).
        if (text.AsSpan().ContainsAny(' ', '\t', '\n')) return false;

        // Fast check: 3+ consecutive identical characters (e.g. "issss", "helllo").
        for (int i = 2; i < text.Length; i++)
        {
            if (text[i] == text[i - 1] && text[i] == text[i - 2])
                return true;
        }

        // Dominant-character check for subtler patterns (e.g. "ababab").
        if (text.Length >= 4)
        {
            var dominant = text.GroupBy(c => char.ToLowerInvariant(c)).MaxBy(g => g.Count())!;
            if (dominant.Count() / (double)text.Length >= 0.60)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Strips trailing repetition-artifact words from a word array.
    /// Nemotron RNN-T can produce degenerate repeated characters at the end of
    /// a streaming chunk (e.g. "This issssssss"). Truncating at the first
    /// trailing artifact prevents garbled text from being committed or displayed.
    /// </summary>
    private static string[] StripTrailingArtifacts(string[] words)
    {
        int validCount = words.Length;
        for (int i = words.Length - 1; i >= 0; i--)
        {
            if (IsRepetitionArtifact(words[i]))
                validCount = i;
            else
                break;
        }
        return validCount == words.Length ? words : words[..validCount];
    }

    public TranscriptionEngine(ILogger<TranscriptionEngine>? logger = null)
        : this(logger, new LiveAudioSessionFactory())
    {
    }

    internal TranscriptionEngine(ILogger<TranscriptionEngine>? logger, ILiveAudioSessionFactory liveSessionFactory)
    {
        _logger = logger ?? NullLogger<TranscriptionEngine>.Instance;
        _liveSessionFactory = liveSessionFactory;
    }

    /// <inheritdoc/>
    public async Task InitializeAsync(
        string modelAlias = "whisper-tiny",
        IProgress<double>? downloadProgress = null,
        bool downloadHardwareEPs = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initializing Foundry Local for model '{ModelAlias}'.", modelAlias);

        try
        {
            await (FoundryLocalManager.IsInitialized
                ? Task.CompletedTask
                : FoundryLocalManager.CreateAsync(
                    FoundryRuntimeConfiguration.Create("Hush", _logger),
                    NullLogger.Instance));
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeLoadException or FileNotFoundException)
        {
            throw new InvalidOperationException(
                "Foundry Local is not installed or could not be loaded. "
                + "Install it from https://github.com/microsoft/foundry-local "
                + "and restart Hush.", ex);
        }

        var manager = FoundryLocalManager.Instance;

        if (OperatingSystem.IsWindows() && downloadHardwareEPs)
        {
            _logger.LogInformation("Downloading hardware acceleration EPs (Windows).");
            await manager.DownloadAndRegisterEpsAsync();
        }

        var catalog = await manager.GetCatalogAsync(cancellationToken);
        var model = await catalog.GetModelAsync(modelAlias, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Model '{modelAlias}' was not found in the Foundry Local catalog. " +
                "Ensure Foundry Local is installed and the alias is correct.");

        _logger.LogInformation("Downloading model '{ModelAlias}' (no-op if already cached).", modelAlias);

        // SDK 0.9.0: DownloadAsync takes Action<float>? progress (0–100).
        Action<float>? sdkProgress = downloadProgress is null
            ? null
            : p => downloadProgress.Report(p / 100.0);

        await model.DownloadAsync(sdkProgress);

        // Hush ships Nemotron CPU int4 instead of whisper-tiny for better quality.
        // The setup script (dist/setup.ps1) downloads Nemotron files from HuggingFace
        // into the SDK's model cache slot. After DownloadAsync ensures the cache dir
        // exists, we check whether Nemotron files have been installed. If so, the
        // remaining whisper-specific files need to be cleaned out so the GenAI runtime
        // loads cleanly as nemotron_speech.
        var modelPath = await model.GetPathAsync(cancellationToken);
        if (modelPath is not null)
        {
            await EnsureNemotronSwapAsync(modelPath, cancellationToken);
        }

        _logger.LogInformation("Loading model '{ModelAlias}' into runtime.", modelAlias);
        await model.LoadAsync();

        _audioClient = await model.GetAudioClientAsync();
        _modelId = model.Id;
        _logger.LogInformation("TranscriptionEngine ready.");
    }

    /// <inheritdoc/>
    public Task StartSessionAsync(
        int sampleRate = 16000,
        int channels = 1,
        string language = "en",
        bool streamingCommit = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_modelId))
            throw new InvalidOperationException(
                "Call InitializeAsync before starting a session.");

        _liveSession = _liveSessionFactory.Create(_modelId!, _logger, _audioClient);
        _resultChannel = Channel.CreateUnbounded<TranscriptionResult>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = true, AllowSynchronousContinuations = true });
        _committedText = string.Empty;
        _streamingCommit = streamingCommit;
        _segmentBase = string.Empty;
        _lastFullText = string.Empty;
        _prevChunkWords = Array.Empty<string>();
        _lastCommittedEndTime = null;

        _logger.LogInformation(
            "Transcription session started (sampleRate={SampleRate}, channels={Channels}, language={Language}).",
            sampleRate, channels, language);

        return StartLiveSessionAsync(sampleRate, channels, language, cancellationToken);
    }

    private async Task StartLiveSessionAsync(int sampleRate, int channels, string language, CancellationToken cancellationToken)
    {
        await _liveSession!.StartAsync(sampleRate, channels, language, cancellationToken).ConfigureAwait(false);
        _resultPumpTask = Task.Run(() => PumpResultsAsync(_liveSession, _resultChannel!), CancellationToken.None);
    }

    /// <inheritdoc/>
    public ValueTask AppendAudioAsync(ReadOnlyMemory<byte> pcmData, CancellationToken cancellationToken = default)
    {
        if (_liveSession is null)
            return ValueTask.CompletedTask;

        return _liveSession.AppendAsync(pcmData, cancellationToken);
    }

    private async Task PumpResultsAsync(ILiveAudioSession liveSession, Channel<TranscriptionResult> resultChannel)
    {
        try
        {
            await foreach (var chunk in liveSession.GetTranscriptionStreamAsync().ConfigureAwait(false))
            {
                if (TryNormalizeChunk(chunk, out var result))
                    resultChannel.Writer.TryWrite(result);
            }

            // Flush any remaining tail-buffer words before completing the channel.
            // This runs after the SDK stream has ended but before TryComplete,
            // so the writer is still open and TryWrite succeeds.
            if (TryFlushRemaining(out var finalResult))
                resultChannel.Writer.TryWrite(finalResult);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            resultChannel.Writer.TryComplete(ex);
            return;
        }
        finally
        {
            resultChannel.Writer.TryComplete();
        }
    }

    private static bool IsNoiseToken(string text) =>
        NoiseTokens.Contains(text) ||
        (text.StartsWith('[') && text.EndsWith(']')) ||
        (text.StartsWith('(') && text.EndsWith(')')) ||
        IsRepetitionArtifact(text);

    /// <inheritdoc/>
    public async Task StopSessionAsync(CancellationToken cancellationToken = default)
    {
        if (_liveSession is null)
            return;

        await _liveSession.StopAsync(cancellationToken).ConfigureAwait(false);

        if (_resultPumpTask is not null)
        {
            await _resultPumpTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            _resultPumpTask = null;
        }

        await _liveSession.DisposeAsync().ConfigureAwait(false);
        _liveSession = null;
        _logger.LogInformation("Transcription session stopped.");
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<TranscriptionResult> GetResultStreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_resultChannel is null)
            throw new InvalidOperationException("No active session. Call StartSessionAsync first.");

        await foreach (var result in _resultChannel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return result;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await StopSessionAsync();
        if (FoundryLocalManager.IsInitialized)
            FoundryLocalManager.Instance.Dispose();
    }

    private bool TryNormalizeChunk(LiveAudioSessionChunk chunk, out TranscriptionResult result)
    {
        var normalizedText = NormalizeText(chunk.Text);
        if (string.IsNullOrWhiteSpace(normalizedText) || IsNoiseToken(normalizedText))
        {
            result = default!;
            return false;
        }

        int backspaceCount = 0;
        string committedDelta = string.Empty;
        string displayText;

        if (chunk.IsFinal)
        {
            // Final chunk — commit the full segment and prepare for the next one.
            // Strip any trailing repetition artifacts before committing.
            var finalWords = normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            finalWords = StripTrailingArtifacts(finalWords);
            if (finalWords.Length == 0)
            {
                result = default!;
                return false;
            }
            var cleanedText = string.Join(' ', finalWords);

            if (ShouldTreatChunkAsDetachedAdditiveWindow(chunk, cleanedText))
            {
                _logger.LogDebug(
                    "Rebasing final chunk onto committed text: committed='{Committed}' chunk='{Chunk}'",
                    _committedText, cleanedText);
                RebaseSegmentToCommittedText();
            }

            // Compose the complete text by prepending any prior segment base.
            displayText = BuildFullText(cleanedText);
            (backspaceCount, committedDelta) = ComputeCommitDelta(displayText);
            _committedText = displayText;
            _lastCommittedEndTime = chunk.EndTime ?? _lastCommittedEndTime;
            _lastFullText = string.Empty;
            _prevChunkWords = Array.Empty<string>();

            // Mark segment boundary so the next chunk is treated as additive.
            _segmentBase = _committedText;

            _logger.LogDebug(
                "Final chunk committed: display='{Display}' delta='{Delta}' bs={BS}",
                displayText, committedDelta, backspaceCount);
        }
        else
        {
            // Split into words and strip trailing repetition artifacts.
            var rawSegmentWords = normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            rawSegmentWords = StripTrailingArtifacts(rawSegmentWords);
            if (rawSegmentWords.Length == 0)
            {
                result = default!;
                return false;
            }

            var rawSegmentText = string.Join(' ', rawSegmentWords);
            if (ShouldTreatChunkAsDetachedAdditiveWindow(chunk, rawSegmentText))
            {
                _logger.LogDebug(
                    "Rebasing interim chunk onto committed text: committed='{Committed}' chunk='{Chunk}' start={Start} lastCommittedEnd={End}",
                    _committedText, rawSegmentText, chunk.StartTime, _lastCommittedEndTime);
                RebaseSegmentToCommittedText();
            }

            // Foundry interim chunks are not always cumulative; some arrive as a
            // rolling suffix window. Merge each chunk into the previous segment
            // hypothesis so the live transcript grows monotonically unless a final
            // chunk later corrects it.
            var segmentWords = MergeStreamingSegmentWords(_prevChunkWords, rawSegmentWords);

            // Store the accumulated segment text so we can flush it on stop.
            var cleanedSegment = string.Join(' ', segmentWords);
            _lastFullText = cleanedSegment;
            displayText = BuildFullText(cleanedSegment);

            // ── Stability-based commit ──────────────────────────────────────
            // Compare the previous accumulated hypothesis with the new one.
            // Words that remain at the same position are considered stable.
            //
            // Streaming mode (_streamingCommit == true):
            //   Commit the full monotonic interim hypothesis. Non-final rewrites
            //   are blocked below, so live typing can continue growing even when
            //   the newest word is still being refined.
            //
            // Batch mode (_streamingCommit == false):
            //   Require a stable prefix and hold back one more word.
            int safeCount;
            int stableCount = StablePrefixLength(_prevChunkWords, segmentWords);

            if (_streamingCommit)
            {
                int speculativeCount = Math.Max(0, segmentWords.Length - StreamingTrailingWordHoldback);
                safeCount = Math.Max(stableCount, speculativeCount);
            }
            else
            {
                safeCount = Math.Max(0, stableCount - 1);
            }

            _logger.LogDebug(
                "Non-final chunk: rawWords={RawWords} mergedWords={MergedWords} prevWords={Prev} safeCount={Safe} streaming={Streaming}",
                rawSegmentWords.Length, segmentWords.Length, _prevChunkWords.Length, safeCount, _streamingCommit);

            _prevChunkWords = segmentWords;

            if (safeCount > 0)
            {
                var safeSegmentText = string.Join(' ', segmentWords[..safeCount]);
                var fullSafeText = BuildFullText(safeSegmentText);

                // Live typing should be monotonic during non-final updates. If the
                // latest interim hypothesis would revise already typed text, defer
                // that correction until a final chunk instead of erasing words in
                // the target app mid-sentence.
                if (fullSafeText.StartsWith(_committedText, StringComparison.Ordinal))
                {
                    (backspaceCount, committedDelta) = ComputeCommitDelta(fullSafeText);
                    _committedText = fullSafeText;
                    _lastCommittedEndTime = chunk.EndTime ?? _lastCommittedEndTime;

                    _logger.LogDebug(
                        "Stability commit: safe='{Safe}' delta='{Delta}' bs={BS}",
                        fullSafeText, committedDelta, backspaceCount);
                }
                else
                {
                    _logger.LogDebug(
                        "Skipping non-final rewrite that would revise committed text: committed='{Committed}' candidate='{Candidate}'",
                        _committedText, fullSafeText);
                }
            }
        }

        result = new TranscriptionResult(
            displayText, committedDelta, chunk.IsFinal, chunk.StartTime, chunk.EndTime)
        {
            BackspaceCount = backspaceCount
        };
        return true;
    }

    /// <summary>
    /// Composes the full transcript text from the segment base and the current
    /// accumulated segment hypothesis.
    /// </summary>
    private string BuildFullText(string segmentText)
    {
        if (string.IsNullOrEmpty(_segmentBase))
            return segmentText;

        if (segmentText.StartsWith(_segmentBase, StringComparison.OrdinalIgnoreCase))
            return segmentText;

        return _segmentBase + " " + segmentText;
    }

    private void RebaseSegmentToCommittedText()
    {
        _segmentBase = _committedText;
        _lastFullText = string.Empty;
        _prevChunkWords = Array.Empty<string>();
    }

    private bool ShouldTreatChunkAsDetachedAdditiveWindow(LiveAudioSessionChunk chunk, string currentText)
    {
        if (!_streamingCommit ||
            string.IsNullOrEmpty(_committedText) ||
            string.IsNullOrEmpty(_lastFullText) ||
            string.IsNullOrEmpty(currentText) ||
            !chunk.StartTime.HasValue ||
            !_lastCommittedEndTime.HasValue)
        {
            return false;
        }

        if (chunk.StartTime.Value + DetachedChunkOverlapTolerance < _lastCommittedEndTime.Value)
            return false;

        if (currentText.StartsWith(_lastFullText, StringComparison.OrdinalIgnoreCase) ||
            _lastFullText.StartsWith(currentText, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (LargestTextSuffixPrefixOverlap(_lastFullText, currentText) > 0)
            return false;

        return LargestTextSuffixPrefixOverlap(currentText, _lastFullText) == 0;
    }

    /// <summary>
    /// Flushes any text remaining in the stability buffer as a final committed result.
    /// Called after the SDK stream has ended but before the channel writer is completed.
    /// </summary>
    private bool TryFlushRemaining(out TranscriptionResult result)
    {
        result = default!;

        if (string.IsNullOrWhiteSpace(_lastFullText))
            return false;

        var fullText = BuildFullText(_lastFullText);
        if (fullText == _committedText)
            return false;

        var (backspaceCount, delta) = ComputeCommitDelta(fullText);
        if (backspaceCount == 0 && string.IsNullOrEmpty(delta))
            return false;

        _committedText = fullText;

        result = new TranscriptionResult(_committedText, delta, IsFinal: true)
        {
            BackspaceCount = backspaceCount
        };
        return true;
    }

    /// <summary>
    /// Computes the delta (and any backspace correction) needed to move from
    /// <see cref="_committedText"/> to <paramref name="targetText"/>.
    /// </summary>
    private (int BackspaceCount, string Delta) ComputeCommitDelta(string targetText)
    {
        if (targetText == _committedText)
            return (0, string.Empty);

        if (string.IsNullOrEmpty(_committedText))
            return (0, targetText);

        // Happy path: previously committed text is an exact prefix.
        if (targetText.StartsWith(_committedText, StringComparison.Ordinal))
            return (0, targetText[_committedText.Length..]);

        // Revision path: find the divergence point and backspace-correct.
        int commonLen = CommonPrefixLength(_committedText, targetText);
        int backspaceCount = _committedText.Length - commonLen;
        string delta = targetText[commonLen..];
        return (backspaceCount, delta);
    }

    private static int CommonPrefixLength(string a, string b)
    {
        int minLen = Math.Min(a.Length, b.Length);
        for (int i = 0; i < minLen; i++)
        {
            if (a[i] != b[i])
                return i;
        }
        return minLen;
    }

    /// <summary>
    /// Returns the number of leading words that are identical between two word
    /// arrays. Used to determine which words have stabilised between consecutive
    /// non-final chunks from the ASR model.
    /// </summary>
    private static int StablePrefixLength(string[] prev, string[] current)
    {
        int minLen = Math.Min(prev.Length, current.Length);
        for (int i = 0; i < minLen; i++)
        {
            if (!string.Equals(prev[i], current[i], StringComparison.Ordinal))
                return i;
        }
        return minLen;
    }

    private static string[] MergeStreamingSegmentWords(string[] previous, string[] current)
    {
        if (current.Length == 0)
            return previous;

        if (previous.Length == 0)
            return current;

        string previousText = string.Join(' ', previous);
        string currentText = string.Join(' ', current);

        var mergedText = MergeStreamingSegmentText(previousText, currentText);
        return mergedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string MergeStreamingSegmentText(string previousText, string currentText)
    {
        if (string.IsNullOrEmpty(currentText))
            return previousText;

        if (string.IsNullOrEmpty(previousText))
            return currentText;

        if (currentText.StartsWith(previousText, StringComparison.OrdinalIgnoreCase))
            return currentText;

        if (previousText.StartsWith(currentText, StringComparison.OrdinalIgnoreCase))
            return previousText;

        int overlap = LargestTextSuffixPrefixOverlap(previousText, currentText);
        if (overlap > 0)
            return previousText + currentText[overlap..];

        // No safe alignment found. Use the latest interim text as the volatile
        // hypothesis so future chunk growth can recover instead of freezing on a
        // stale prefix.
        return currentText;
    }

    private static int LargestTextSuffixPrefixOverlap(string previousText, string currentText)
    {
        int maxOverlap = Math.Min(previousText.Length, currentText.Length);
        for (int overlap = maxOverlap; overlap > 0; overlap--)
        {
            int previousStart = previousText.Length - overlap;
            if (!IsWordBoundary(previousText, previousStart) || !IsWordBoundary(currentText, overlap))
                continue;

            if (previousText.AsSpan(previousStart, overlap).Equals(currentText.AsSpan(0, overlap), StringComparison.OrdinalIgnoreCase))
                return overlap;
        }

        return 0;
    }

    private static bool IsWordBoundary(string text, int index)
    {
        return index <= 0 || index >= text.Length || char.IsWhiteSpace(text[index - 1]) || char.IsWhiteSpace(text[index]);
    }

    private static string NormalizeText(string text)
        => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ── Nemotron model swap ──────────────────────────────────────────────────
    //
    // The Foundry Local catalog ships whisper-tiny (encoder-decoder ASR) but
    // Hush uses NVIDIA's Nemotron CPU int4 (RNN-T) for better streaming quality.
    // After the SDK downloads whisper-tiny into the cache, this method replaces
    // those files with Nemotron from HuggingFace.

    private const string HuggingFaceBase = "https://huggingface.co/jiafatom/nemotron-cpu-int4/resolve/main";

    private static readonly string[] NemotronFiles =
    [
        "audio_processor_config.json",
        "decoder.onnx",
        "decoder.onnx.data",
        "encoder.onnx",
        "encoder.onnx.data",
        "genai_config.json",
        "joint.onnx",
        "joint.onnx.data",
        "tokenizer.json",
        "tokenizer_config.json",
        "vocab.txt"
    ];

    // Whisper files that must be removed so the GenAI runtime doesn't see
    // a model_type mismatch ("Got: whisper" vs nemotron_speech genai_config).
    private static readonly string[] WhisperLeftovers =
    [
        "config.json",
        "preprocessor_config.json",
        "added_tokens.json",
        "merges.txt",
        "normalizer.json",
        "special_tokens_map.json",
        "vocab.json",
        "whisper-tiny_decoder_fp32.onnx",
        "whisper-tiny_decoder_fp32.onnx.data",
        "whisper-tiny_encoder_fp32.onnx",
        "whisper-tiny_encoder_fp32.onnx.data",
        "whisper-tiny_jump_times_fp32.onnx"
    ];

    private async Task EnsureNemotronSwapAsync(string modelCacheDir, CancellationToken ct)
    {
        // Already swapped?
        var genaiPath = Path.Combine(modelCacheDir, "genai_config.json");
        if (File.Exists(genaiPath))
        {
            var content = await File.ReadAllTextAsync(genaiPath, ct);
            if (content.Contains("nemotron_speech", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Nemotron model files already present in cache.");
                CleanWhisperLeftovers(modelCacheDir);
                return;
            }
        }

        // Check the legacy ~/.aitk cache (setup.ps1 may have put files there)
        var legacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aitk", "Microsoft", "openai-whisper-tiny-generic-cpu-2", "cpu-fp32");

        if (Directory.Exists(legacyDir))
        {
            var legacyConfig = Path.Combine(legacyDir, "genai_config.json");
            if (File.Exists(legacyConfig))
            {
                var legContent = await File.ReadAllTextAsync(legacyConfig, ct);
                if (legContent.Contains("nemotron_speech", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Copying Nemotron files from legacy cache -> SDK cache.");
                    foreach (var f in NemotronFiles)
                    {
                        var src = Path.Combine(legacyDir, f);
                        if (File.Exists(src))
                            File.Copy(src, Path.Combine(modelCacheDir, f), overwrite: true);
                    }
                    CleanWhisperLeftovers(modelCacheDir);
                    return;
                }
            }
        }

        // Download from HuggingFace directly
        _logger.LogInformation("Downloading Nemotron CPU int4 model from HuggingFace (~700 MB)...");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        foreach (var file in NemotronFiles)
        {
            var destPath = Path.Combine(modelCacheDir, file);
            if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
            {
                // Could be a nemotron file from a partial previous download. Check.
                if (file == "genai_config.json")
                {
                    var c = await File.ReadAllTextAsync(destPath, ct);
                    if (c.Contains("nemotron_speech", StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                else
                {
                    // Only skip if the nemotron genai_config was already placed
                    continue;
                }
            }

            _logger.LogInformation("Downloading {File}...", file);
            var url = $"{HuggingFaceBase}/{file}";
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await stream.CopyToAsync(fs, ct);
        }

        CleanWhisperLeftovers(modelCacheDir);
        _logger.LogInformation("Nemotron model swap complete.");
    }

    private void CleanWhisperLeftovers(string modelCacheDir)
    {
        foreach (var file in WhisperLeftovers)
        {
            var path = Path.Combine(modelCacheDir, file);
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogDebug("Removed whisper leftover: {File}", file);
            }
        }
    }
}
