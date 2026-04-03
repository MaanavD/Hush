using CommunityToolkit.Mvvm.ComponentModel;

namespace Hush.App.ViewModels;

/// <summary>
/// View-model for the floating overlay window shown during dictation.
/// </summary>
public sealed partial class OverlayViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isListening;

    [ObservableProperty]
    private string _interimText = string.Empty;

    /// <summary>Model download progress 0–1. Shown before the model is ready.</summary>
    [ObservableProperty]
    private double _modelDownloadProgress;

    /// <summary>True once the model is loaded and dictation is possible.</summary>
    [ObservableProperty]
    private bool _isModelReady;

    /// <summary>
    /// Normalised RMS audio level in [0, 1] — updated ~10×/sec from the capture service.
    /// Drives the waveform bar animation in the overlay.
    /// </summary>
    [ObservableProperty]
    private float _audioLevel;

    /// <summary>
    /// Non-null when a recoverable error should be shown to the user (e.g. mic unavailable,
    /// session start failure). Set to <c>null</c> or empty to dismiss the error panel.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    /// <summary>True when <see cref="ErrorMessage"/> is non-empty.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
}
