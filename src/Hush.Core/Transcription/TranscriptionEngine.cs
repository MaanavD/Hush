using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hush.Core.Transcription;

/// <summary>
/// Wraps the Foundry Local <see cref="OpenAIAudioClient"/> to provide
/// live-transcription streaming.
/// <para>
/// <b>SDK note (Milestone 0):</b> The <c>LiveAudioTranscriptionSession</c> API
/// was added to the Foundry Local C# SDK after the 0.9.0 NuGet release. Until
/// an updated package is available, this engine buffers raw PCM audio in memory
/// and runs periodic batch transcription via <c>TranscribeAudioAsync</c> as an
/// interim strategy. See Milestone 1 — Proof of Life for the upgrade path.
/// </para>
/// </summary>
public sealed class TranscriptionEngine : ITranscriptionEngine
{
    private readonly ILogger<TranscriptionEngine> _logger;
    private OpenAIAudioClient? _audioClient;

    // Channel-based session state
    private Channel<TranscriptionResult>? _resultChannel;
    private readonly List<byte> _audioBuffer = new();
    private bool _sessionActive;
    private bool _disposed;

    // ── Batch-loop tuning ────────────────────────────────────────────────────
    // How often the loop wakes to transcribe accumulated audio.
    // 800 ms = ~2-3 words per burst; lower = more responsive but higher CPU.
    private const int TranscriptionIntervalMs = 800;

    // Minimum audio data before we bother calling the model (0.25 s at 16 kHz
    // 16-bit mono = 8000 bytes). Avoids wasting inference on near-silence.
    private const int MinBytesToTranscribe = 8000;

    // Maximum audio buffer size before dropping new audio (≈60 s at 16 kHz 16-bit mono).
    // Prevents unbounded memory growth if inference stalls or is slow.
    private const int MaxAudioBufferBytes = 16000 * 2 * 60; // ~1.9 MB

    // RMS silence gate: skip inference when the chunk is below this energy level.
    // 16-bit PCM normalised to [-1, 1]; 0.01 ≈ quiet room background noise.
    // Raise to ~0.02 if you still see hallucinations in a noisier environment.
    private const double SilenceRmsThreshold = 0.01;

    // Whisper hallucination tokens that should never be typed or shown.
    private static readonly HashSet<string> NoiseTokens =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "[blank_audio]", "(silence)", "[silence]", "[music]",
            "[inaudible]", "[ Silence ]", "(music)"
        };

    // Signals the batch loop to wake up immediately (used by StopSessionAsync).
    private TaskCompletionSource? _stopSignal;

    // Running transcript for this session — overlay always shows growing text.
    private string _sessionTranscript = string.Empty;

    public TranscriptionEngine(ILogger<TranscriptionEngine>? logger = null)
    {
        _logger = logger ?? NullLogger<TranscriptionEngine>.Instance;
    }

    /// <inheritdoc/>
    public async Task InitializeAsync(
        string modelAlias = "whisper-tiny",
        IProgress<double>? downloadProgress = null,
        bool downloadHardwareEPs = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initializing Foundry Local for model '{ModelAlias}'.", modelAlias);

        // Clean up any orphaned WAV temp files from previous crashes.
        CleanOrphanedTempFiles();

        try
        {
            await (FoundryLocalManager.IsInitialized
                ? Task.CompletedTask
                : FoundryLocalManager.CreateAsync(
                    new Microsoft.AI.Foundry.Local.Configuration { AppName = "Hush" },
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
            await manager.EnsureEpsDownloadedAsync();
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
        _logger.LogInformation("Loading model '{ModelAlias}' into runtime.", modelAlias);
        await model.LoadAsync();

        _audioClient = await model.GetAudioClientAsync();
        _audioClient.Settings.Language = "en";
        _logger.LogInformation("TranscriptionEngine ready.");
    }

    /// <inheritdoc/>
    public Task StartSessionAsync(
        int sampleRate = 16000,
        int channels = 1,
        string language = "en",
        CancellationToken cancellationToken = default)
    {
        if (_audioClient is null)
            throw new InvalidOperationException(
                "Call InitializeAsync before starting a session.");

        _audioClient.Settings.Language = language;
        _resultChannel = Channel.CreateUnbounded<TranscriptionResult>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
        _audioBuffer.Clear();
        _sessionTranscript = string.Empty;
        _stopSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sessionActive = true;

        _logger.LogInformation(
            "Transcription session started (sampleRate={SampleRate}, channels={Channels}, language={Language}).",
            sampleRate, channels, language);

        // TODO (Milestone 1): Replace this polling loop with
        // LiveAudioTranscriptionSession once SDK >0.9.0 is published to NuGet.
        _ = Task.Run(() => BatchTranscriptionLoopAsync(sampleRate, channels, cancellationToken), cancellationToken);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask AppendAudioAsync(ReadOnlyMemory<byte> pcmData, CancellationToken cancellationToken = default)
    {
        if (!_sessionActive)
            return ValueTask.CompletedTask;

        lock (_audioBuffer)
        {
            // Drop new audio if the buffer exceeds ~60 s. This prevents unbounded
            // memory growth when inference is slower than real-time.
            if (_audioBuffer.Count + pcmData.Length > MaxAudioBufferBytes)
            {
                _logger.LogWarning(
                    "Audio buffer full ({Size} bytes); dropping {Dropped} bytes of audio.",
                    _audioBuffer.Count, pcmData.Length);
                return ValueTask.CompletedTask;
            }

            _audioBuffer.AddRange(pcmData.Span);
        }

        return ValueTask.CompletedTask;
    }

    // Periodically transcribes accumulated audio as WAV and pushes results.
    private async Task BatchTranscriptionLoopAsync(int sampleRate, int channels, CancellationToken ct)
    {
        if (_audioClient is null || _resultChannel is null)
            return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Wake on the normal interval OR an immediate stop signal.
                var stopTask = _stopSignal?.Task ?? Task.CompletedTask;
                await Task.WhenAny(Task.Delay(TranscriptionIntervalMs, ct), stopTask)
                          .ConfigureAwait(false);

                bool isFinalFlush = !_sessionActive;

                byte[] chunk;
                lock (_audioBuffer)
                {
                    // Skip tiny chunks during normal operation (likely silence);
                    // but always flush whatever remains on the final pass.
                    if (_audioBuffer.Count < MinBytesToTranscribe && !isFinalFlush)
                        goto nextIteration;

                    if (_audioBuffer.Count == 0)
                    {
                        if (isFinalFlush) break;
                        goto nextIteration;
                    }

                    chunk = _audioBuffer.ToArray();
                    _audioBuffer.Clear();
                }

                await TranscribeChunkAsync(chunk, sampleRate, channels).ConfigureAwait(false);

                if (isFinalFlush) break;

                nextIteration:
                if (isFinalFlush) break;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _resultChannel?.Writer.TryComplete();
        }
    }

    private async Task TranscribeChunkAsync(byte[] chunk, int sampleRate, int channels)
    {
        if (_audioClient is null || _resultChannel is null)
            return;

        // Skip inference on silent chunks to prevent Whisper hallucinations.
        if (ComputeRms(chunk) < SilenceRmsThreshold)
        {
            _logger.LogDebug("Skipping silent chunk (RMS below threshold).");
            return;
        }

        var wavFile = Path.GetTempFileName() + ".wav";
        try
        {
            WritePcmAsWav(chunk, wavFile, sampleRate, channels);
            var result = await _audioClient.TranscribeAudioAsync(wavFile).ConfigureAwait(false);
            var rawText = result?.Text?.Trim();

            if (string.IsNullOrWhiteSpace(rawText) || IsNoiseToken(rawText))
                return;

            // Collapse runs of internal whitespace to a single space.
            rawText = Regex.Replace(rawText, @"\s+", " ");

            // Append to running session transcript (overlay always grows).
            var separator = _sessionTranscript.Length > 0 ? " " : string.Empty;
            _sessionTranscript += separator + rawText;

            _resultChannel.Writer.TryWrite(new TranscriptionResult(
                DisplayText: _sessionTranscript,              // whole session so far (overlay)
                CommittedDelta: separator + rawText,          // include leading space so consecutive pastes separate cleanly
                IsFinal: false));                             // true only when session ends
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batch transcription segment failed.");
        }
        finally
        {
            File.Delete(wavFile);
        }
    }

    private static bool IsNoiseToken(string text) =>
        NoiseTokens.Contains(text) ||
        (text.StartsWith('[') && text.EndsWith(']')) ||
        (text.StartsWith('(') && text.EndsWith(')'));

    /// <inheritdoc/>
    public Task StopSessionAsync(CancellationToken cancellationToken = default)
    {
        _sessionActive = false;
        // Wake the batch loop immediately so the last audio chunk is flushed
        // without waiting for the next timer tick.
        _stopSignal?.TrySetResult();
        _logger.LogInformation("Transcription session stopped.");
        return Task.CompletedTask;
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

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes the RMS energy of a 16-bit signed PCM byte array, normalised to [0, 1].
    /// </summary>
    private static double ComputeRms(byte[] pcm)
    {
        int samples = pcm.Length / 2;
        if (samples == 0) return 0.0;
        double sumSq = 0.0;
        for (int i = 0; i < pcm.Length - 1; i += 2)
        {
            short s = (short)(pcm[i] | (pcm[i + 1] << 8));
            double norm = s / 32768.0;
            sumSq += norm * norm;
        }
        return Math.Sqrt(sumSq / samples);
    }

    /// <summary>
    /// Writes raw 16-bit signed PCM to a minimal WAV file for batch transcription.
    /// </summary>
    private static void WritePcmAsWav(byte[] pcm, string path, int sampleRate, int channels)
    {
        const short bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        // RIFF header
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + pcm.Length);              // ChunkSize
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        // fmt sub-chunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);                            // SubChunk1Size (PCM)
        bw.Write((short)1);                      // AudioFormat (PCM)
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);

        // data sub-chunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(pcm.Length);
        bw.Write(pcm);
    }

    /// <summary>
    /// Removes orphaned <c>.tmp*.wav</c> files left behind if the process crashed
    /// mid-transcription. Only deletes files older than 5 minutes to avoid racing
    /// with a concurrent transcription.
    /// </summary>
    private void CleanOrphanedTempFiles()
    {
        try
        {
            var tempDir = Path.GetTempPath();
            var cutoff = DateTime.UtcNow.AddMinutes(-5);
            foreach (var file in Directory.EnumerateFiles(tempDir, "tmp*.wav"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch { /* best-effort */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to clean orphaned temp WAV files.");
        }
    }
}
