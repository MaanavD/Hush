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
    private bool _isListening;

    /// <summary>Model download progress 0–1. Shown before the model is ready.</summary>
    [ObservableProperty]
    private double _modelDownloadProgress;

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

    /// <summary>
    /// Latest streaming-diff debug line from the dictation session.
    /// Shows prefix length, erase count, typed/target text.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDebugInfo))]
    private string? _debugInfo;

    /// <summary>Whether to show the debug panel in the overlay.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDebugInfo))]
    private bool _showDebugPanel;

    private string _committedTranscript = string.Empty;
    private bool _showPartialTranscript = true;

    /// <summary>True when <see cref="ErrorMessage"/> is non-empty.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>True when the debug panel should be visible.</summary>
    public bool HasDebugInfo => ShowDebugPanel && !string.IsNullOrEmpty(DebugInfo);

    /// <summary>True when <see cref="TranscriptText"/> contains visible text.</summary>
    public bool HasTranscript => !string.IsNullOrWhiteSpace(TranscriptText);

    /// <summary>True while actively listening but no transcript text has appeared yet.</summary>
    public bool ShowListeningHint => IsListening && !HasTranscript;

    public bool IsPreparing => !IsModelReady && !HasError;

    /// <summary>
    /// Prepares the overlay transcript state for a new dictation session.
    /// </summary>
    public void BeginSession(bool showPartialTranscript)
    {
        _showPartialTranscript = showPartialTranscript;
        _committedTranscript = string.Empty;
        TranscriptText = string.Empty;
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
