using Hush.Core.Audio;
using Hush.Core.Output;
using Hush.Core.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hush.Core.Session;

/// <summary>
/// Orchestrates a full push-to-talk dictation session:
/// hotkey held → mic capture → Foundry Local transcription → keystroke output.
/// </summary>
/// <remarks>
/// Interim text is dispatched to <see cref="OnInterimText"/> for the overlay.
/// Only committed (stable) deltas are forwarded to <see cref="ITextOutputService"/>
/// and typed into the focused application (see SPEC §2.2).
/// </remarks>
public sealed class DictationSession : IDictationSession
{
    private readonly ITranscriptionEngine _engine;
    private readonly IAudioCaptureService _capture;
    private readonly ITextOutputService _output;
    private readonly ILogger<DictationSession> _logger;
    private readonly Action<float> _audioLevelForwarder;

    private Task? _transcriptionLoop;
    private CancellationTokenSource? _loopCts;
    private bool _disposed;

    public DictationSession(
        ITranscriptionEngine engine,
        IAudioCaptureService capture,
        ITextOutputService output,
        ILogger<DictationSession>? logger = null)
    {
        _engine = engine;
        _capture = capture;
        _output = output;
        _logger = logger ?? NullLogger<DictationSession>.Instance;
        _audioLevelForwarder = level => OnAudioLevel?.Invoke(level);
    }

    /// <inheritdoc/>
    public event Action<string>? OnInterimText;

    /// <inheritdoc/>
    public event Action<string>? OnCommittedChunk;

    /// <inheritdoc/>
    public event Action<float>? OnAudioLevel;

    /// <inheritdoc/>
    public event Action? OnSessionStopped;

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Capture in a local so post-await code is safe even if StopAsync
        // nullifies _loopCts while we are suspended at the await below.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopCts = cts;
        await _engine.StartSessionAsync(cancellationToken: cts.Token);

        // StopAsync may have been called while StartSessionAsync was awaited.
        // If the token was cancelled, bail out — the session is already stopping.
        if (cts.IsCancellationRequested)
            return;

        // Wire audio capture to the engine's append method.
        _capture.AudioLevelChanged += _audioLevelForwarder;
        try
        {
            _capture.Start((pcm, ct) => _engine.AppendAudioAsync(pcm, ct));
        }
        catch
        {
            _capture.AudioLevelChanged -= _audioLevelForwarder;
            throw;
        }

        _logger.LogInformation("Dictation session started.");

        // Background loop: update overlay immediately from interim results;
        // type only committed deltas into the target application.
        _transcriptionLoop = Task.Run(
            () => TranscriptionLoopAsync(cts.Token),
            cts.Token);
    }

    private async Task TranscriptionLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var result in _engine.GetResultStreamAsync(cancellationToken))
            {
                if (!string.IsNullOrEmpty(result.DisplayText))
                    OnInterimText?.Invoke(result.DisplayText);

                if (!string.IsNullOrEmpty(result.CommittedDelta))
                {
                    await _output.TypeTextAsync(result.CommittedDelta, cancellationToken);
                    OnCommittedChunk?.Invoke(result.CommittedDelta);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown; nothing to log.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transcription loop encountered an unhandled error.");
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _capture.AudioLevelChanged -= _audioLevelForwarder;
        _capture.Stop();
        await _engine.StopSessionAsync(cancellationToken);

        if (_transcriptionLoop is not null)
        {
            // Wait for the loop to flush remaining committed chunks.
            await _transcriptionLoop.WaitAsync(cancellationToken);
            _transcriptionLoop = null;
        }

        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _loopCts = null;

        _logger.LogInformation("Dictation session stopped.");
        OnSessionStopped?.Invoke();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            await StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception during DictationSession disposal.");
        }

        _capture.Dispose();
        await _engine.DisposeAsync();
    }
}
