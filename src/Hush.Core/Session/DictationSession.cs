// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Text;
using Hush.Core.Audio;
using Hush.Core.Configuration;
using Hush.Core.Diagnostics;
using Hush.Core.Output;
using Hush.Core.PostProcessing;
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
    private readonly IReadOnlyList<TextSubstitution>? _substitutions;
    private readonly ITranscriptBuffer? _transcriptBuffer;
    private readonly IPostProcessingService? _postProcessor;

    private Task? _transcriptionLoop;
    private CancellationTokenSource? _loopCts;
    private bool _disposed;
    private DictationOutputMode _outputMode = DictationOutputMode.Streaming;
    private string? _postProcessingPrompt;
    private int _sessionErrorRaised;
    private AutoSubmitKey _autoSubmitKey;
    private readonly StringBuilder _sessionAccumulated = new();

    private static readonly char[] SpinnerFrames = { '|', '/', '\u2014', '\\' };
    private const int SpinnerIntervalMs = 120;

    public DictationSession(
        ITranscriptionEngine engine,
        IAudioCaptureService capture,
        ITextOutputService output,
        ILogger<DictationSession>? logger = null,
        IReadOnlyList<TextSubstitution>? substitutions = null,
        ITranscriptBuffer? transcriptBuffer = null,
        IPostProcessingService? postProcessor = null)
    {
        _engine = engine;
        _capture = capture;
        _output = output;
        _logger = logger ?? NullLogger<DictationSession>.Instance;
        _substitutions = substitutions;
        _transcriptBuffer = transcriptBuffer;
        _postProcessor = postProcessor;
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
    public event Action<Exception>? OnSessionError;
    /// <inheritdoc/>
    public event Action<bool>? OnPostProcessingStateChanged;
    /// <inheritdoc/>
    public async Task StartAsync(
        string language = "en",
        bool streamingCommit = true,
        bool showSpinner = false,
        string? postProcessingPrompt = null,
        AutoSubmitKey autoSubmitKey = AutoSubmitKey.None,
        CancellationToken cancellationToken = default,
        DictationOutputMode outputMode = DictationOutputMode.Auto)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _outputMode = ResolveOutputMode(outputMode, showSpinner);
        _postProcessingPrompt = postProcessingPrompt;
        _sessionErrorRaised = 0;
        _autoSubmitKey = autoSubmitKey;
        _sessionAccumulated.Clear();

        // Capture in a local so post-await code is safe even if StopAsync
        // nullifies _loopCts while we are suspended at the await below.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopCts = cts;
        var engineStreamingCommit = _outputMode == DictationOutputMode.CleanStreamingPreview
            ? true
            : streamingCommit;
        await _engine.StartSessionAsync(language: language, streamingCommit: engineStreamingCommit, cancellationToken: cts.Token);

        // StopAsync may have been called while StartSessionAsync was awaited.
        // If the token was cancelled, bail out — the session is already stopping.
        if (cts.IsCancellationRequested)
            return;

        // Wire audio capture to the engine's append method.
        _capture.AudioLevelChanged += _audioLevelForwarder;
        try
        {
            _capture.Start(ForwardAudioToEngineAsync);
        }
        catch
        {
            _capture.AudioLevelChanged -= _audioLevelForwarder;
            throw;
        }

        _logger.LogInformation("Dictation session started (mode={Mode}).", _outputMode);

        _transcriptionLoop = Task.Run(
            () => _outputMode switch
            {
                DictationOutputMode.Spinner => SpinnerTranscriptionLoopAsync(cts.Token),
                DictationOutputMode.CleanStreamingPreview => CleanStreamingPreviewTranscriptionLoopAsync(cts.Token),
                _ => StreamingTranscriptionLoopAsync(cts.Token)
            },
            cts.Token);
    }

    private static DictationOutputMode ResolveOutputMode(DictationOutputMode outputMode, bool showSpinner)
        => outputMode == DictationOutputMode.Auto
            ? showSpinner ? DictationOutputMode.Spinner : DictationOutputMode.Streaming
            : outputMode;

    // ── Streaming path: commit only stable deltas ────────────────────────
    //
    // A brief settle delay is inserted between backspace events and the
    // immediately-following text characters. Apps that route keyboard input
    // through an async pipeline (Notepad/WinUI3 via TSF/XAML, some UWP apps)
    // may not have finished processing VK_BACK messages by the time SendInput
    // returns, causing the replacement text to land in the wrong cursor
    // position or, worse, late-arriving backspaces deleting the replacement.
    private const int BackspaceSettleMs = 15;
    // TSF-aware apps (Notepad, WinUI3) process each VK_BACK through an async
    // pipeline: WM_KEYDOWN → TSF → document update → XAML layout → render.
    // Large batches (e.g. 80 backspaces on a final correction) need several
    // hundred milliseconds. We scale linearly and cap at a reasonable maximum.
    private const int TsfBackspacePerCharMs = 5;
    private const int TsfBackspaceMinMs = 60;
    private const int TsfBackspaceMaxMs = 600;

    private async Task StreamingTranscriptionLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var result in _engine.GetResultStreamAsync(cancellationToken))
            {
                var targetText = result.DisplayText ?? string.Empty;

                if (!string.IsNullOrEmpty(targetText))
                    OnInterimText?.Invoke(targetText);

                _logger.LogDebug(
                    "Output: bs={BS} delta='{Delta}' isFinal={Final} display='{Display}'",
                    result.BackspaceCount, result.CommittedDelta, result.IsFinal, targetText);

                if (result.BackspaceCount > 0)
                {
                    await _output.SendBackspacesAsync(result.BackspaceCount, cancellationToken, skipModifierRestore: true);
                    int settleMs = CalculateBackspaceSettleMs(result.BackspaceCount);
                    _logger.LogDebug("Backspace settle: {SettleMs}ms for {Count} backspaces", settleMs, result.BackspaceCount);
                    await Task.Delay(settleMs, cancellationToken);
                }

                if (!string.IsNullOrEmpty(result.CommittedDelta))
                {
                    string delta;
                    using (PerformanceProfiler.Measure("Session.Substitution"))
                    {
                        delta = _substitutions?.Count > 0
                            ? SubstitutionProcessor.Apply(result.CommittedDelta, _substitutions)
                            : result.CommittedDelta;
                    }
                    _sessionAccumulated.Append(delta);
                    using (PerformanceProfiler.Measure("Session.TypeText"))
                        await _output.TypeTextAsync(delta, cancellationToken, skipModifierRestore: true);
                    OnCommittedChunk?.Invoke(delta);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown; nothing to log.
        }
        catch (Exception ex)
        {
            ReportSessionError(ex, "Transcription loop encountered an unhandled error.");
        }
    }

    private async Task CleanStreamingPreviewTranscriptionLoopAsync(CancellationToken cancellationToken)
    {
        var rawTranscript = new StringBuilder();
        var visiblePreview = new StringBuilder();
        var targetWindow = TargetWindowGuard.Capture();

        try
        {
            await foreach (var result in _engine.GetResultStreamAsync(cancellationToken))
            {
                var targetText = result.DisplayText ?? string.Empty;
                if (!string.IsNullOrEmpty(targetText))
                    OnInterimText?.Invoke(targetText);

                if (result.BackspaceCount > 0 && rawTranscript.Length > 0)
                {
                    int toRemove = Math.Min(result.BackspaceCount, rawTranscript.Length);
                    rawTranscript.Remove(rawTranscript.Length - toRemove, toRemove);
                }

                if (!string.IsNullOrEmpty(result.CommittedDelta))
                    rawTranscript.Append(result.CommittedDelta);

                bool canRefreshFinalPreview = result.IsFinal
                    && result.BackspaceCount == 0
                    && !string.IsNullOrEmpty(result.CommittedDelta);
                bool shouldRefreshPreview = !result.IsFinal || canRefreshFinalPreview;
                var previewText = !string.IsNullOrEmpty(targetText)
                    ? targetText
                    : rawTranscript.ToString();

                if (shouldRefreshPreview && !string.IsNullOrEmpty(previewText))
                {
                    var targetPreview = ApplySessionSubstitutions(previewText);
                    await TryReplaceVisiblePreviewAsync(
                        visiblePreview,
                        targetPreview,
                        targetWindow,
                        cancellationToken,
                        restoreForeground: false,
                        skipModifierRestore: true);
                }

                if (!string.IsNullOrEmpty(result.CommittedDelta))
                    OnCommittedChunk?.Invoke(result.CommittedDelta);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            ReportSessionError(ex, "Transcription loop encountered an unhandled error.");
        }

        var finalText = ApplySessionSubstitutions(rawTranscript.ToString());
        if (!string.IsNullOrEmpty(finalText)
            && _postProcessor is not null
            && !string.IsNullOrEmpty(_postProcessingPrompt))
        {
            using var spinnerCts = new CancellationTokenSource();
            var spinnerTask = await StartCleanProcessingSpinnerAsync(
                visiblePreview,
                targetWindow,
                spinnerCts.Token);

            OnPostProcessingStateChanged?.Invoke(true);
            try
            {
                var rewritten = await _postProcessor.RewriteAsync(finalText, _postProcessingPrompt, CancellationToken.None);
                if (!string.IsNullOrEmpty(rewritten))
                    finalText = rewritten;
            }
            finally
            {
                await spinnerCts.CancelAsync();
                if (spinnerTask is not null)
                {
                    try { await spinnerTask; }
                    catch (OperationCanceledException) { }
                }

                OnPostProcessingStateChanged?.Invoke(false);
            }
        }

        if (!string.Equals(visiblePreview.ToString(), finalText, StringComparison.Ordinal))
        {
            bool replaced = await TryReplaceVisiblePreviewAsync(
                visiblePreview,
                finalText,
                targetWindow,
                CancellationToken.None,
                restoreForeground: true,
                skipModifierRestore: false,
                boundLargeReplacementToCurrentLine: true);

            if (!replaced)
            {
                _logger.LogWarning(
                    "Leaving {Length}-char clean streaming preview unchanged because the target window lost focus and could not be restored.",
                    visiblePreview.Length);
                finalText = visiblePreview.ToString();
            }
        }

        if (!string.IsNullOrEmpty(finalText))
            _sessionAccumulated.Append(finalText);
    }

    private async Task<Task?> StartCleanProcessingSpinnerAsync(
        StringBuilder visiblePreview,
        TargetWindowGuard.Handle targetWindow,
        CancellationToken cancellationToken)
    {
        if (!TargetWindowGuard.IsStillForeground(targetWindow))
            return null;

        await _output.TypeTextAsync(SpinnerFrames[0].ToString(), CancellationToken.None, skipModifierRestore: true);
        visiblePreview.Append(SpinnerFrames[0]);

        return Task.Run(
            () => AnimateCleanProcessingSpinnerAsync(visiblePreview, targetWindow, cancellationToken),
            CancellationToken.None);
    }

    private async Task AnimateCleanProcessingSpinnerAsync(
        StringBuilder visiblePreview,
        TargetWindowGuard.Handle targetWindow,
        CancellationToken cancellationToken)
    {
        int frameIndex = 1;

        while (true)
        {
            await Task.Delay(SpinnerIntervalMs, cancellationToken);
            if (!TargetWindowGuard.IsStillForeground(targetWindow))
                continue;

            await _output.SendBackspacesAsync(1, CancellationToken.None, skipModifierRestore: true);
            if (visiblePreview.Length > 0)
                visiblePreview.Length--;

            var frame = SpinnerFrames[frameIndex % SpinnerFrames.Length];
            await _output.TypeTextAsync(frame.ToString(), CancellationToken.None, skipModifierRestore: true);
            visiblePreview.Append(frame);
            frameIndex++;
        }
    }

    private async Task<bool> TryReplaceVisiblePreviewAsync(
        StringBuilder visiblePreview,
        string targetText,
        TargetWindowGuard.Handle targetWindow,
        CancellationToken cancellationToken,
        bool restoreForeground,
        bool skipModifierRestore,
        bool boundLargeReplacementToCurrentLine = false)
    {
        var currentText = visiblePreview.ToString();
        if (string.Equals(currentText, targetText, StringComparison.Ordinal))
            return true;

        bool hasForeground = restoreForeground
            ? TargetWindowGuard.TryEnsureForeground(targetWindow)
            : TargetWindowGuard.IsStillForeground(targetWindow);
        if (!hasForeground)
            return false;

        int commonPrefixLength = CommonPrefixLength(currentText, targetText);
        int backspaceCount = visiblePreview.Length - commonPrefixLength;
        var delta = targetText[commonPrefixLength..];
        bool boundToCurrentLine = ShouldBoundReplacementToCurrentLine(
            boundLargeReplacementToCurrentLine,
            backspaceCount,
            currentText,
            targetText);

        if (boundToCurrentLine)
        {
            await _output.ReplaceTextAsync(
                currentText.Length,
                targetText,
                cancellationToken,
                skipModifierRestore,
                boundToCurrentLine: true,
                expectedExistingText: currentText);
            visiblePreview.Clear();
            visiblePreview.Append(targetText);
            return true;
        }

        if (backspaceCount > 0)
        {
            await _output.SendBackspacesAsync(backspaceCount, cancellationToken, skipModifierRestore);
            visiblePreview.Remove(commonPrefixLength, backspaceCount);

            int settleMs = CalculateBackspaceSettleMs(backspaceCount);
            _logger.LogDebug("Clean preview backspace settle: {SettleMs}ms for {Count} backspaces", settleMs, backspaceCount);
            await Task.Delay(settleMs, cancellationToken);
        }

        if (!string.IsNullOrEmpty(delta))
        {
            await _output.TypeTextAsync(delta, cancellationToken, skipModifierRestore);
            visiblePreview.Append(delta);
        }

        return true;
    }

    private static bool ShouldBoundReplacementToCurrentLine(
        bool enabled,
        int backspaceCount,
        string currentText,
        string targetText)
        => enabled
           && backspaceCount >= 32
           && currentText.Length >= 32
           && !string.IsNullOrEmpty(targetText);

    private string ApplySessionSubstitutions(string text)
        => _substitutions?.Count > 0
            ? SubstitutionProcessor.Apply(text, _substitutions)
            : text;

    private static int CommonPrefixLength(string left, string right)
    {
        int length = Math.Min(left.Length, right.Length);
        int index = 0;
        while (index < length && left[index] == right[index])
            index++;
        return index;
    }

    private static int CalculateBackspaceSettleMs(int backspaceCount)
    {
        if (OperatingSystem.IsWindows() && Output.ForegroundWindowDetector.IsTsfProblematic())
            return Math.Clamp(backspaceCount * TsfBackspacePerCharMs, TsfBackspaceMinMs, TsfBackspaceMaxMs);

        return BackspaceSettleMs;
    }

    // ── Spinner path: animate indicator, buffer text, type on stop ───────

    private async Task SpinnerTranscriptionLoopAsync(CancellationToken cancellationToken)
    {
        var accumulatedText = new StringBuilder();
        CancellationTokenSource? spinnerCts = null;
        Task? spinnerTask = null;
        bool spinnerStarted = false;

        // Pin the window that had focus when the session began. All spinner
        // animations and the final (optionally LLM-rewritten) typing pass are
        // gated on this window still being foreground, so the cleaned text
        // never leaks into whichever window the user clicked into while the
        // LLM was rewriting.
        var targetWindow = TargetWindowGuard.Capture();

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
                        var spinnerToken = spinnerCts.Token;
                        spinnerTask = Task.Run(() => RunSpinnerAsync(targetWindow, spinnerToken));
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
            ReportSessionError(ex, "Transcription loop encountered an unhandled error.");
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
        if (_substitutions?.Count > 0)
            finalText = SubstitutionProcessor.Apply(finalText, _substitutions);
        _sessionAccumulated.Append(finalText);
        if (!string.IsNullOrEmpty(finalText))
        {
            // Optional LLM post-processing pass (spinner mode only).
            if (_postProcessor is not null && !string.IsNullOrEmpty(_postProcessingPrompt))
            {
                OnPostProcessingStateChanged?.Invoke(true);
                try
                {
                    var rewritten = await _postProcessor.RewriteAsync(finalText, _postProcessingPrompt, CancellationToken.None);
                    if (!string.IsNullOrEmpty(rewritten))
                        finalText = rewritten;
                }
                finally
                {
                    OnPostProcessingStateChanged?.Invoke(false);
                }
            }

            try
            {
                // Re-focus the captured window before typing. If the user
                // clicked into a different app while we were transcribing /
                // rewriting, restoration usually succeeds because Hush is on
                // the foreground input queue (the hotkey release was the most
                // recent user-initiated foreground event). If restoration
                // fails we refuse to type rather than dump the cleaned text
                // into whichever window happens to be focused.
                if (!TargetWindowGuard.TryEnsureForeground(targetWindow))
                {
                    _logger.LogWarning(
                        "Dropping {Length}-char cleanse-mode output because the target window lost focus and could not be restored.",
                        finalText.Length);
                }
                else
                {
                    await _output.TypeTextAsync(finalText, CancellationToken.None);
                    _logger.LogDebug("Typed buffered text ({Length} chars) after spinner session.", finalText.Length);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to type accumulated text after spinner session.");
            }
        }
    }

    private async Task RunSpinnerAsync(TargetWindowGuard.Handle targetWindow, CancellationToken cancellationToken)
    {
        int frameIndex = 0;
        bool hasChar = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // If the user has clicked away from the original window, stop
                // animating. We deliberately do NOT try to restore focus
                // mid-spinner — the spinner is a continuous visual affordance,
                // and yanking focus back every 120 ms while the user is
                // interacting with another app would be worse than just
                // pausing. The final typing pass still attempts a restore.
                if (!TargetWindowGuard.IsStillForeground(targetWindow))
                {
                    _logger.LogDebug("Target window lost focus during spinner; pausing animation.");
                    hasChar = false;   // do not backspace — focus is elsewhere
                    return;
                }

                // Remove the previous frame character.
                if (hasChar)
                {
                    await _output.SendBackspacesAsync(1, CancellationToken.None);
                    hasChar = false;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Re-check focus between the backspace and the next frame —
                // SendInput is async at the OS layer and focus can change
                // within a single tick.
                if (!TargetWindowGuard.IsStillForeground(targetWindow))
                {
                    _logger.LogDebug("Target window lost focus during spinner; pausing animation.");
                    return;
                }

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
        // before the accumulated text is typed — but only if we are still
        // over the original window. Otherwise the backspace would delete a
        // real character in whatever window the user clicked into.
        if (hasChar && TargetWindowGuard.IsStillForeground(targetWindow))
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

    private async ValueTask ForwardAudioToEngineAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken)
    {
        try
        {
            await _engine.AppendAudioAsync(pcm, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Session is shutting down.
        }
        catch (Exception ex)
        {
            ReportSessionError(ex, "Failed to append microphone audio to the transcription session.");
        }
    }

    private void ReportSessionError(Exception ex, string message)
    {
        _logger.LogError(ex, message);
        if (Interlocked.Exchange(ref _sessionErrorRaised, 1) == 0)
            OnSessionError?.Invoke(ex);
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

        if (_sessionAccumulated.Length > 0)
        {
            _transcriptBuffer?.Push(_sessionAccumulated.ToString());
            _sessionAccumulated.Clear();
        }

        if (_autoSubmitKey != AutoSubmitKey.None)
            await _output.SendKeyAsync(_autoSubmitKey, CancellationToken.None);

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
