using CommunityToolkit.Mvvm.ComponentModel;
using Hush.Core.Configuration;
using Hush.Core.Input;
using Hush.Core.Output;
using Hush.Core.Session;
using Hush.Core.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hush.App.ViewModels;

/// <summary>
/// Root application view-model. Manages model download state and orchestrates
/// the hotkey ↔ dictation session lifecycle.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly HushSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly ITranscriptionEngine _engine;
    private readonly IDictationSession _dictationSession;
    private readonly IGlobalHotkeyService _hotkeyService;
    private readonly ISoundEffectService _soundEffects;
    private readonly IAutoStartService _autoStart;
    private readonly OverlayViewModel _overlayVm;
    private readonly ILogger<MainViewModel> _logger;

    [ObservableProperty]
    private bool _isModelReady;

    [ObservableProperty]
    private bool _isListening;

    [ObservableProperty]
    private double _modelDownloadProgress;

    [ObservableProperty]
    private string _statusMessage = "Initialising…";

    /// <summary>Exposed so the tray can open the settings window with context.</summary>
    public SettingsViewModel SettingsViewModel { get; }

    public MainViewModel(
        HushSettings settings,
        SettingsService settingsService,
        ITranscriptionEngine engine,
        IDictationSession dictationSession,
        IGlobalHotkeyService hotkeyService,
        ISoundEffectService soundEffects,
        IAutoStartService autoStart,
        OverlayViewModel overlayVm,
        ILogger<MainViewModel>? logger = null)
    {
        _settings = settings;
        _settingsService = settingsService;
        _engine = engine;
        _dictationSession = dictationSession;
        _hotkeyService = hotkeyService;
        _soundEffects = soundEffects;
        _autoStart = autoStart;
        _overlayVm = overlayVm;
        _logger = logger ?? NullLogger<MainViewModel>.Instance;

        SettingsViewModel = new SettingsViewModel(settings);

        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _hotkeyService.HotkeyReleased += OnHotkeyReleased;

        _dictationSession.OnInterimText += text =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _overlayVm.InterimText = text;
            });

        _dictationSession.OnAudioLevel += level =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _overlayVm.AudioLevel = level;
            });

        _dictationSession.OnSessionStopped += () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _overlayVm.IsListening = false;
                _overlayVm.AudioLevel = 0f;
                IsListening = false;
            });
    }

    /// <summary>
    /// Downloads and loads the model if needed. Should be called once at startup.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        StatusMessage = "Checking model…";
        var progress = new Progress<double>(p =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ModelDownloadProgress = p;
                _overlayVm.ModelDownloadProgress = p;
                StatusMessage = $"Downloading… {p:P0}";
            });
        });

        try
        {
            await _engine.InitializeAsync(
                _settings.TranscriptionModel, progress, cancellationToken: cancellationToken);

            IsModelReady = true;
            _overlayVm.IsModelReady = true;
            StatusMessage = "Ready — hold Ctrl+Shift+H to dictate";
            _logger.LogInformation("Hush is ready.");
        }
        catch (Exception ex)
        {
            var msg = ex is InvalidOperationException
                ? ex.Message   // Model-not-found carries a user-readable message.
                : $"Failed to load model '{_settings.TranscriptionModel}'.";
            StatusMessage = $"Error: {msg}";
            _overlayVm.ErrorMessage = msg;
            _logger.LogError(ex, "Failed to initialise model.");
        }
    }

    /// <summary>
    /// Persists <see cref="SettingsViewModel"/> state to disk and applies
    /// the auto-start preference to the OS.
    /// </summary>
    public async Task SaveSettingsAsync(CancellationToken cancellationToken = default)
    {
        var previousHotkey = _settings.Hotkey;
        SettingsViewModel.Apply();
        await _settingsService.SaveAsync(_settings, cancellationToken);
        await _autoStart.SetEnabledAsync(_settings.AutoStart, cancellationToken);

        // Re-register the hotkey if it changed.
        if (!string.Equals(previousHotkey, _settings.Hotkey, StringComparison.Ordinal))
        {
            try
            {
                _hotkeyService.Unregister();
                _hotkeyService.Register(_settings.Hotkey);
                _logger.LogInformation("Hotkey changed to '{Hotkey}'.", _settings.Hotkey);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to re-register hotkey '{Hotkey}'.", _settings.Hotkey);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _overlayVm.ErrorMessage = $"Could not register hotkey '{_settings.Hotkey}'. It may be in use by another application.";
                    StatusMessage = $"Error: {_overlayVm.ErrorMessage}";
                });
            }
        }
    }

    private async void OnHotkeyPressed(object? sender, EventArgs e)
    {
        if (!IsModelReady)
            return;

        try
        {
            // Hotkey events arrive on the hotkey STA thread — marshal UI mutations.
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsListening = true;
                _overlayVm.IsListening = true;
                _overlayVm.InterimText = string.Empty;
                _overlayVm.ErrorMessage = null;   // Clear previous error on new attempt.
            });

            if (_settings.SoundEffects)
                _ = _soundEffects.PlayStartAsync();

            await _dictationSession.StartAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Failed to start dictation session.");

            try
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _overlayVm.IsListening = false;
                    IsListening = false;
                    _overlayVm.ErrorMessage = BuildSessionErrorMessage(ex);
                    StatusMessage = $"Error: {_overlayVm.ErrorMessage}";
                });
            }
            catch { /* app may be shutting down */ }
        }
    }

    private async void OnHotkeyReleased(object? sender, EventArgs e)
    {
        try
        {
            await _dictationSession.StopAsync();

            if (_settings.SoundEffects)
                _ = _soundEffects.PlayStopAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Failed to stop dictation session.");
            try
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _overlayVm.IsListening = false;
                    IsListening = false;
                });
            }
            catch { /* app may be shutting down */ }
        }
    }

    private static string BuildSessionErrorMessage(Exception ex) => ex switch
    {
        // Distinguish common, user-actionable failures from generic ones.
        InvalidOperationException { Message: var m } when m.Contains("microphone", StringComparison.OrdinalIgnoreCase)
            || m.Contains("audio", StringComparison.OrdinalIgnoreCase)
            || m.Contains("device", StringComparison.OrdinalIgnoreCase)
            => "Microphone unavailable. Check your audio device and permissions.",
        InvalidOperationException { Message: var m } when m.Contains("elevated", StringComparison.OrdinalIgnoreCase)
            || m.Contains("administrator", StringComparison.OrdinalIgnoreCase)
            => m, // UIPI message is already user-readable.
        PlatformNotSupportedException { Message: var m } => m,
        OperationCanceledException => "Session was cancelled.",
        _ => "Could not start dictation. Check ~/.hush/hush.log for details."
    };
}
