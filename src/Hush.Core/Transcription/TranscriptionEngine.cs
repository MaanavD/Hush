// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Threading.Channels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Hush.Core.Configuration;
using Hush.Core.Diagnostics;

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
    private IModel? _model;
    private string? _modelId;
    private Channel<TranscriptionResult>? _resultChannel;
    private ILiveAudioSession? _liveSession;
    private Task? _resultPumpTask;
    private bool _modelLoaded;
    private CancellationTokenSource? _unloadTimerCts;

    /// <summary>How long after the last session ends before the model is unloaded to free RAM.</summary>
    public ModelUnloadTimeout UnloadTimeout { get; set; } = ModelUnloadTimeout.Min5;
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
    private const int StreamingTrailingWordHoldback = 1;
    // Allow a small timestamp overlap when the SDK rolls windows forward so a
    // later chunk can still be treated as additive speech instead of a rewrite.
    private static readonly TimeSpan DetachedChunkOverlapTolerance = TimeSpan.FromMilliseconds(150);

    // ASR hallucination / silence tokens that should never be typed or shown.
    private static readonly HashSet<string> NoiseTokens =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "[blank_audio]", "(silence)", "[silence]", "[music]",
            "[inaudible]", "[ Silence ]", "(music)"
        };

    // Check for blatant repetition artifacts from live ASR (e.g. "kkkking", "aaaa", "ee").
    // Fires when a single character makes up ≥60% of a word-only token,
    // when 3+ consecutive identical characters appear, or when a 2-char
    // token is the same character repeated (e.g. "ee", "oo", "..").
    private static bool IsRepetitionArtifact(string text)
    {
        if (text.Length < 2) return false;
        // Only applies when the token contains no whitespace (single word).
        if (text.AsSpan().ContainsAny(' ', '\t', '\n')) return false;

        // Two identical characters — no real English word consists of a single
        // character repeated twice, but Nemotron emits these as degenerate tokens.
        if (text.Length == 2)
            return char.ToLowerInvariant(text[0]) == char.ToLowerInvariant(text[1]);

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
    /// Removes repetition-artifact words from anywhere in a word array.
    /// Nemotron RNN-T can produce degenerate repeated characters at any position
    /// in a streaming chunk (e.g. "This issssssss" or "I feel ee oo tt").
    /// Filtering all artifacts prevents garbled text from being committed.
    /// </summary>
    private static string[] FilterArtifactWords(string[] words)
    {
        bool hasAny = false;
        for (int i = 0; i < words.Length; i++)
        {
            if (IsRepetitionArtifact(words[i]))
            {
                hasAny = true;
                break;
            }
        }
        if (!hasAny) return words;

        var filtered = new List<string>(words.Length);
        for (int i = 0; i < words.Length; i++)
        {
            if (!IsRepetitionArtifact(words[i]))
                filtered.Add(words[i]);
        }
        return filtered.ToArray();
    }

    /// <summary>
    /// Detects chunks where the raw text is entirely degenerate — long runs of
    /// repeated characters that indicate the ASR model has gone off the rails.
    /// </summary>
    private static bool IsEntirelyDegenerate(string text)
    {
        if (text.Length < 10) return false;

        int runLength = 1;
        for (int i = 1; i < text.Length; i++)
        {
            if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(text[i - 1]))
            {
                runLength++;
                if (runLength >= 10)
                    return true;
            }
            else
            {
                runLength = 1;
            }
        }

        return false;
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
        string modelAlias = "nemotron-speech-streaming-en-0.6b-generic-cpu",
        IProgress<double>? downloadProgress = null,
        bool downloadHardwareEPs = false,
        IProgress<string>? statusProgress = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initializing Foundry Local for model '{ModelAlias}'.", modelAlias);
        statusProgress?.Report("Connecting to Foundry Local…");

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
            statusProgress?.Report("Downloading hardware acceleration…");
            await manager.DownloadAndRegisterEpsAsync();
        }

        statusProgress?.Report($"Resolving model '{modelAlias}'…");
        var catalog = await manager.GetCatalogAsync(cancellationToken);
        var model = await catalog.GetModelAsync(modelAlias, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Model '{modelAlias}' was not found in the Foundry Local catalog. " +
                "Ensure Foundry Local is installed and the alias is correct.");

        _logger.LogInformation("Downloading model '{ModelAlias}' (no-op if already cached).", modelAlias);
        statusProgress?.Report($"Checking cache for '{modelAlias}'…");

        // SDK 0.9.0: DownloadAsync takes Action<float>? progress (0–100).
        // Switch status text to "Downloading" the first time a non-zero progress
        // arrives — a zero progress means we hit the cached-model fast path.
        bool announcedDownload = false;
        Action<float>? sdkProgress = p =>
        {
            if (!announcedDownload && p > 0.01f)
            {
                announcedDownload = true;
                statusProgress?.Report($"Downloading '{modelAlias}'…");
            }
            downloadProgress?.Report(p / 100.0);
        };

        await model.DownloadAsync(sdkProgress);

        _logger.LogInformation("Loading model '{ModelAlias}' into runtime.", modelAlias);
        statusProgress?.Report("Loading model into runtime…");
        await model.LoadAsync();

        _model = model;
        _audioClient = await model.GetAudioClientAsync();
        _modelId = model.Id;
        _modelLoaded = true;
        statusProgress?.Report("Ready");
        _logger.LogInformation("TranscriptionEngine ready.");
    }

    /// <inheritdoc/>
    public async Task StartSessionAsync(
        int sampleRate = 16000,
        int channels = 1,
        string language = "en",
        bool streamingCommit = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_modelId))
            throw new InvalidOperationException(
                "Call InitializeAsync before starting a session.");

        // Cancel any pending idle-unload timer so the model stays loaded.
        _unloadTimerCts?.Cancel();

        // Reload model if the idle-unload timer already fired (only when _model was set via InitializeAsync).
        if (!_modelLoaded && _model is not null)
        {
            _logger.LogInformation("Reloading model after idle timeout.");
            await _model.LoadAsync();
            _audioClient = await _model.GetAudioClientAsync();
            _modelLoaded = true;
        }

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

        await StartLiveSessionAsync(sampleRate, channels, language, cancellationToken).ConfigureAwait(false);
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

        using var _ = PerformanceProfiler.Measure("Engine.AppendAudio");
        return _liveSession.AppendAsync(pcmData, cancellationToken);
    }

    private async Task PumpResultsAsync(ILiveAudioSession liveSession, Channel<TranscriptionResult> resultChannel)
    {
        int chunkIndex = 0;
        try
        {
            await foreach (var chunk in liveSession.GetTranscriptionStreamAsync().ConfigureAwait(false))
            {
                chunkIndex++;
                _logger.LogDebug(
                    "SDK chunk #{Index}: IsFinal={IsFinal} text=\"{Text}\" start={Start} end={End}",
                    chunkIndex, chunk.IsFinal, chunk.Text, chunk.StartTime, chunk.EndTime);

                // Track the growing committed text length — key metric for O(n) growth diagnosis.
                PerformanceProfiler.Gauge("Engine.CommittedTextLen", _committedText.Length);
                PerformanceProfiler.Gauge("Engine.SegmentBaseLen", _segmentBase.Length);

                bool emitted;
                TranscriptionResult result;
                using (PerformanceProfiler.MeasureWithContext("Engine.TryNormalizeChunk", _committedText.Length))
                    emitted = TryNormalizeChunk(chunk, out result);

                if (emitted)
                {
                    _logger.LogDebug(
                        "Emitting result #{Index}: display=\"{Display}\" delta=\"{Delta}\" bs={BS} segBase=\"{SegBase}\"",
                        chunkIndex, result.DisplayText, result.CommittedDelta, result.BackspaceCount, _segmentBase);
                    resultChannel.Writer.TryWrite(result);
                }
                else
                {
                    _logger.LogDebug("Chunk #{Index} filtered out by TryNormalizeChunk.", chunkIndex);
                }
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

        // Schedule model unload after idle period if configured.
        if (UnloadTimeout != ModelUnloadTimeout.Never)
        {
            _unloadTimerCts?.Cancel();
            _unloadTimerCts?.Dispose();
            var cts = new CancellationTokenSource();
            _unloadTimerCts = cts;
            _ = Task.Run(() => UnloadAfterDelayAsync(cts.Token));
        }
    }

    private async Task UnloadAfterDelayAsync(CancellationToken cancellationToken)
    {
        var delay = UnloadTimeout switch
        {
            ModelUnloadTimeout.Min2  => TimeSpan.FromMinutes(2),
            ModelUnloadTimeout.Min5  => TimeSpan.FromMinutes(5),
            ModelUnloadTimeout.Min15 => TimeSpan.FromMinutes(15),
            _                        => Timeout.InfiniteTimeSpan
        };

        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested)
            return;

        if (_model is not null)
        {
            try
            {
                await _model.UnloadAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to call UnloadAsync on model; marking as unloaded anyway.");
            }
        }

        _modelLoaded = false;
        _logger.LogInformation("Model unloaded after idle timeout.");
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
        _unloadTimerCts?.Cancel();
        _unloadTimerCts?.Dispose();
        await StopSessionAsync();
        if (FoundryLocalManager.IsInitialized)
            FoundryLocalManager.Instance.Dispose();
    }

    private bool TryNormalizeChunk(LiveAudioSessionChunk chunk, out TranscriptionResult result)
    {
        var normalizedText = NormalizeText(chunk.Text);
        if (string.IsNullOrWhiteSpace(normalizedText) || IsNoiseToken(normalizedText) || IsEntirelyDegenerate(normalizedText))
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
            // Filter any repetition artifacts before committing.
            var finalWords = normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            finalWords = FilterArtifactWords(finalWords);
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
                RebaseSegmentToCommittedText(includeBufferedTail: true);
            }

            // Compose the complete text by prepending any prior segment base.
            displayText = BuildFullText(cleanedText);
            using (PerformanceProfiler.MeasureWithContext("Engine.ComputeCommitDelta", _committedText.Length))
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
            // Split into words and filter repetition artifacts at any position.
            var rawSegmentWords = normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            rawSegmentWords = FilterArtifactWords(rawSegmentWords);
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
                RebaseSegmentToCommittedText(includeBufferedTail: true);
            }

            // Foundry interim chunks are not always cumulative; some arrive as a
            // rolling suffix window and others are full rewrites of the active
            // segment. Merge additive windows into a monotonic hypothesis, but
            // let clear rewrites replace the interim segment so corrections can
            // wait for a final chunk instead of duplicating text.
            var segmentWords = ShouldTreatChunkAsInterimRewrite(chunk, rawSegmentText)
                ? rawSegmentWords
                : MergeStreamingSegmentWords(_prevChunkWords, rawSegmentWords);

            // Store the accumulated segment text so we can flush it on stop.
            var cleanedSegment = string.Join(' ', segmentWords);
            _lastFullText = cleanedSegment;
            using (PerformanceProfiler.MeasureWithContext("Engine.BuildFullText", _segmentBase.Length))
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
                // Always hold back the last word — it may still be a partial token
                // that the model hasn't extended yet. stableCount is used in batch
                // mode but must NOT override the holdback here: if the model repeats
                // the same partial word token across two chunks it would be counted
                // as "stable" and committed before it is complete.
                safeCount = Math.Max(0, segmentWords.Length - StreamingTrailingWordHoldback);
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
                    using (PerformanceProfiler.MeasureWithContext("Engine.ComputeCommitDelta", _committedText.Length))
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

        // If the tail of _segmentBase overlaps with the head of segmentText
        // (e.g. Nemotron's next rolling window starts from mid-committed text),
        // splice at the overlap boundary instead of naive concatenation to
        // prevent committed words from being typed twice.
        int overlap = LargestTextSuffixPrefixOverlap(_segmentBase, segmentText);
        if (overlap > 0)
            return _segmentBase + segmentText[overlap..];

        return _segmentBase + " " + segmentText;
    }

    private void RebaseSegmentToCommittedText(bool includeBufferedTail)
    {
        _segmentBase = includeBufferedTail && !string.IsNullOrWhiteSpace(_lastFullText)
            ? BuildFullText(_lastFullText)
            : _committedText;
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

    private bool ShouldTreatChunkAsInterimRewrite(LiveAudioSessionChunk chunk, string currentText)
    {
        if (!_streamingCommit ||
            string.IsNullOrEmpty(_lastFullText) ||
            string.IsNullOrEmpty(currentText) ||
            !chunk.StartTime.HasValue ||
            !_lastCommittedEndTime.HasValue)
        {
            return false;
        }

        if (chunk.StartTime.Value + DetachedChunkOverlapTolerance >= _lastCommittedEndTime.Value)
            return false;

        if (currentText.StartsWith(_lastFullText, StringComparison.OrdinalIgnoreCase) ||
            _lastFullText.StartsWith(currentText, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return LargestTextSuffixPrefixOverlap(_lastFullText, currentText) == 0;
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

        // Apply artifact filtering before flushing — the buffered hypothesis
        // may contain degenerate tokens that were held back by the stability
        // mechanism but would otherwise be committed wholesale on session end.
        var flushWords = _lastFullText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        flushWords = FilterArtifactWords(flushWords);
        if (flushWords.Length == 0)
            return false;

        var cleanedFlush = string.Join(' ', flushWords);
        var fullText = BuildFullText(cleanedFlush);
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

        // Character-level divergence point.
        int commonLen = CommonPrefixLength(_committedText, targetText);
        int charBackspaces = _committedText.Length - commonLen;
        string charDelta = targetText[commonLen..];

        // Word-level diff: treats words as identical when they differ only in
        // capitalisation or punctuation (e.g. "hello" ≈ "Hello,"). This avoids
        // erasing and retyping words that are already correct on screen when the
        // final model chunk adds punctuation or corrects capitalisation.
        var (wordBackspaces, wordDelta) = ComputeWordLevelDelta(_committedText, targetText);
        return wordBackspaces < charBackspaces
            ? (wordBackspaces, wordDelta)
            : (charBackspaces, charDelta);
    }

    /// <summary>
    /// Word-level delta that normalises away punctuation and case differences.
    /// Returns the minimum backspace count and new text needed to move from
    /// <paramref name="committedText"/> to <paramref name="targetText"/> at
    /// word granularity, so that words which are already correct are not erased.
    /// </summary>
    private static (int BackspaceCount, string Delta) ComputeWordLevelDelta(
        string committedText, string targetText)
    {
        var cWords = committedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tWords = targetText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (cWords.Length == 0 || tWords.Length == 0)
            return (committedText.Length, targetText);

        int minLen = Math.Min(cWords.Length, tWords.Length);
        int commonCount = 0;
        for (int i = 0; i < minLen; i++)
        {
            var cNorm = NormalizeWordForComparison(cWords[i]);
            var tNorm = NormalizeWordForComparison(tWords[i]);
            if (string.IsNullOrEmpty(cNorm) || cNorm != tNorm)
                break;
            commonCount++;
        }

        if (commonCount == 0)
            return (committedText.Length, targetText);

        // Text is space-normalised, so joining the first N words gives the exact
        // character offset of the end of the last common word in each string.
        int cPos = string.Join(' ', cWords[..commonCount]).Length;
        int tPos = string.Join(' ', tWords[..commonCount]).Length;

        return (committedText.Length - cPos, targetText[tPos..]);
    }

    /// <summary>
    /// Strips leading/trailing punctuation and returns the lowercase core of a
    /// word token so that "Hello," and "hello" compare as equal.
    /// </summary>
    private static string NormalizeWordForComparison(string word)
    {
        int start = 0, end = word.Length;
        while (start < end && (char.IsPunctuation(word[start]) || char.IsSymbol(word[start]))) start++;
        while (end > start && (char.IsPunctuation(word[end - 1]) || char.IsSymbol(word[end - 1]))) end--;
        return start >= end ? string.Empty : word[start..end].ToLowerInvariant();
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

        // No overlap found — chunks are sequential, non-overlapping segments
        // (typical of Nemotron RNN-T). Concatenate to build the full hypothesis.
        return previousText + " " + currentText;
    }

    private static int LargestTextSuffixPrefixOverlap(string previousText, string currentText)
    {
        using var _ = PerformanceProfiler.MeasureWithContext(
            "Engine.SuffixPrefixOverlap", previousText.Length + currentText.Length);

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
}
