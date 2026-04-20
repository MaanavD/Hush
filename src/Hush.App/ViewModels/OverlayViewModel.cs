// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using CommunityToolkit.Mvvm.ComponentModel;

namespace Hush.App.ViewModels;

/// <summary>
/// View-model for the floating overlay window shown during dictation.
/// </summary>
public sealed partial class OverlayViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowListeningHint))]
    [NotifyPropertyChangedFor(nameof(IsListeningRaw))]
    [NotifyPropertyChangedFor(nameof(IsListeningClean))]
    private bool _isListening;

    /// <summary>True when a clean-mode (LLM rewrite) session is active; drives teal overlay colour.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListeningRaw))]
    [NotifyPropertyChangedFor(nameof(IsListeningClean))]
    private bool _isCleanMode;

    /// <summary>
    /// True while the cleanse-mode LLM rewrite is running. The overlay surfaces
    /// this as a "Finishing…" hint so users know not to click into another app
    /// — during this window, focus changes cause the final text to be dropped
    /// (the guard in <c>DictationSession</c> refuses to type into a different
    /// window than the one active at session start).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFinalizingHint))]
    [NotifyPropertyChangedFor(nameof(IsListeningClean))]
    private bool _isFinalizing;

    /// <summary>Model download progress 0–1. Shown before the model is ready.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPreparingProgress))]
    private double _modelDownloadProgress;

    /// <summary>
    /// Human-readable sub-stage text displayed beneath "Preparing…" so the
    /// user can see what the startup is actually doing (connecting to
    /// Foundry Local, resolving the model, downloading, loading into the
    /// runtime, warming up post-processing, etc.).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreparingDetail))]
    [NotifyPropertyChangedFor(nameof(ShowPreparingProgress))]
    private string _preparingDetail = "Starting…";

    /// <summary>True once the model is loaded and dictation is possible.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreparing))]
    private bool _isModelReady;

    [ObservableProperty]
    private string _overlayPosition = "bottom-center";

    [ObservableProperty]
    private double _overlayOpacity = 0.85;

    /// <summary>
    /// Normalised RMS audio level in [0, 1] — updated ~10×/sec from the capture service.
    /// Drives the waveform bar animation in the overlay.
    /// </summary>
    [ObservableProperty]
    private float _audioLevel;

    /// <summary>
    /// Latest transcript text shown in the overlay while dictating.
    /// Depending on settings this may include unstable interim text, or only
    /// text that has already been committed into the target application.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTranscript))]
    [NotifyPropertyChangedFor(nameof(ShowListeningHint))]
    private string _transcriptText = string.Empty;

    /// <summary>
    /// Non-null when a recoverable error should be shown to the user (e.g. mic unavailable,
    /// session start failure). Set to <c>null</c> or empty to dismiss the error panel.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(IsPreparing))]
    private string? _errorMessage;

    private string _committedTranscript = string.Empty;
    private bool _showPartialTranscript = true;

    /// <summary>True when <see cref="ErrorMessage"/> is non-empty.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>True when <see cref="TranscriptText"/> contains visible text.</summary>
    public bool HasTranscript => !string.IsNullOrWhiteSpace(TranscriptText);

    /// <summary>True while actively listening but no transcript text has appeared yet.</summary>
    public bool ShowListeningHint => IsListening && !HasTranscript;

    public bool IsPreparing => !IsModelReady && !HasError;

    /// <summary>True when <see cref="PreparingDetail"/> carries useful text.</summary>
    public bool HasPreparingDetail => !string.IsNullOrWhiteSpace(PreparingDetail);

    /// <summary>
    /// Only show the download % pill when we're actually downloading; during
    /// "Connecting" / "Loading" phases the value is 0 and would be misleading.
    /// </summary>
    public bool ShowPreparingProgress => ModelDownloadProgress > 0.0 && ModelDownloadProgress < 1.0;

    /// <summary>Listening in raw (purple) mode.</summary>
    public bool IsListeningRaw => IsListening && !IsCleanMode;

    /// <summary>Listening in clean (teal) mode — shown only while still recording speech.</summary>
    public bool IsListeningClean => IsListening && IsCleanMode && !IsFinalizing;

    /// <summary>True when the overlay should show the "Finishing…" hint.</summary>
    public bool ShowFinalizingHint => IsFinalizing;

    /// <summary>
    /// Prepares the overlay transcript state for a new dictation session.
    /// </summary>
    public void BeginSession(bool showPartialTranscript, bool isCleanMode = false)
    {
        _showPartialTranscript = showPartialTranscript;
        _committedTranscript = string.Empty;
        TranscriptText = string.Empty;
        IsCleanMode = isCleanMode;
        IsFinalizing = false;
    }

    /// <summary>
    /// Updates the overlay with the latest full display text from the engine.
    /// </summary>
    public void UpdateInterimTranscript(string displayText)
    {
        if (!_showPartialTranscript)
            return;

        TranscriptText = displayText;
    }

    /// <summary>
    /// Records text that has been committed into the target application.
    /// </summary>
    public void AppendCommittedTranscript(string committedDelta)
    {
        if (string.IsNullOrEmpty(committedDelta))
            return;

        _committedTranscript += committedDelta;
        if (!_showPartialTranscript)
            TranscriptText = _committedTranscript;
    }

    /// <summary>
    /// Clears any transcript text from the overlay after a session completes.
    /// </summary>
    public void ClearSessionTranscript()
    {
        _committedTranscript = string.Empty;
        TranscriptText = string.Empty;
    }
}
