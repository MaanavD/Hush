// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Text;
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
/// When <c>showSpinner</c> is enabled, a rotating indicator is animated in the
/// focused application and committed text is buffered until the session ends.
/// Otherwise committed (stable) deltas are typed progressively.
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
    private bool _showSpinner;

    private static readonly char[] SpinnerFrames = { '|', '/', '\u2014', '\\' };
    private const int SpinnerIntervalMs = 120;

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
    public async Task StartAsync(string language = "en", bool streamingCommit = true, bool showSpinner = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _showSpinner = showSpinner;

        // Capture in a local so post-await code is safe even if StopAsync
        // nullifies _loopCts while we are suspended at the await below.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopCts = cts;
        await _engine.StartSessionAsync(language: language, streamingCommit: streamingCommit, cancellationToken: cts.Token);

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

        _logger.LogInformation("Dictation session started (spinner={Spinner}).", showSpinner);

        _transcriptionLoop = Task.Run(
            () => _showSpinner
                ? SpinnerTranscriptionLoopAsync(cts.Token)
                : StreamingTranscriptionLoopAsync(cts.Token),
            cts.Token);
    }

    // ── Streaming path: type committed deltas as they arrive ─────────────

    private async Task StreamingTranscriptionLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var result in _engine.GetResultStreamAsync(cancellationToken))
            {
                if (!string.IsNullOrEmpty(result.DisplayText))
                    OnInterimText?.Invoke(result.DisplayText);

                if (result.BackspaceCount > 0)
                    await _output.SendBackspacesAsync(result.BackspaceCount, cancellationToken);

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

    // ── Spinner path: animate indicator, buffer text, type on stop ───────

    private async Task SpinnerTranscriptionLoopAsync(CancellationToken cancellationToken)
    {
        var accumulatedText = new StringBuilder();
        CancellationTokenSource? spinnerCts = null;
        Task? spinnerTask = null;
        bool spinnerStarted = false;

        try
        {
            await foreach (var result in _engine.GetResultStreamAsync(cancellationToken))
            {
                if (!string.IsNullOrEmpty(result.DisplayText))
                {
                    OnInterimText?.Invoke(result.DisplayText);

                    // Start the spinner animation on the first interim text
                    // so the user sees activity in the target app.
                    if (!spinnerStarted)
                    {
                        spinnerStarted = true;
                        spinnerCts = new CancellationTokenSource();
                        spinnerTask = Task.Run(() => RunSpinnerAsync(spinnerCts.Token));
                    }
                }

                // Handle backspace corrections on accumulated text.
                if (result.BackspaceCount > 0 && accumulatedText.Length > 0)
                {
                    int toRemove = Math.Min(result.BackspaceCount, accumulatedText.Length);
                    accumulatedText.Remove(accumulatedText.Length - toRemove, toRemove);
                }

                if (!string.IsNullOrEmpty(result.CommittedDelta))
                {
                    accumulatedText.Append(result.CommittedDelta);
                    OnCommittedChunk?.Invoke(result.CommittedDelta);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transcription loop encountered an unhandled error.");
        }

        // Stop the spinner before typing accumulated text.
        if (spinnerCts is not null)
        {
            await spinnerCts.CancelAsync();
            if (spinnerTask is not null)
            {
                try { await spinnerTask; }
                catch (OperationCanceledException) { }
            }
            spinnerCts.Dispose();
        }

        // Type the full accumulated transcript in one shot.
        var finalText = accumulatedText.ToString();
        if (!string.IsNullOrEmpty(finalText))
        {
            try
            {
                await _output.TypeTextAsync(finalText, CancellationToken.None);
                _logger.LogDebug("Typed buffered text ({Length} chars) after spinner session.", finalText.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to type accumulated text after spinner session.");
            }
        }
    }

    private async Task RunSpinnerAsync(CancellationToken cancellationToken)
    {
        int frameIndex = 0;
        bool hasChar = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Remove the previous frame character.
                if (hasChar)
                {
                    await _output.SendBackspacesAsync(1, CancellationToken.None);
                    hasChar = false;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Type the next frame character.
                char frame = SpinnerFrames[frameIndex % SpinnerFrames.Length];
                await _output.TypeTextAsync(frame.ToString(), CancellationToken.None);
                hasChar = true;
                frameIndex++;

                await Task.Delay(SpinnerIntervalMs, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }

        // Clean up: erase the last spinner character so the caret is clean
        // before the accumulated text is typed.
        if (hasChar)
        {
            try
            {
                await _output.SendBackspacesAsync(1, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to erase spinner character on cleanup.");
            }
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
            // Wait for the loop to flush remaining committed chunks
            // (or type buffered text in spinner mode).
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
