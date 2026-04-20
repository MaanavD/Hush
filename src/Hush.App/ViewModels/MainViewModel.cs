// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using CommunityToolkit.Mvvm.ComponentModel;
using Hush.Core.Audio;
using Hush.Core.Configuration;
using Hush.Core.Input;
using Hush.Core.Output;
using Hush.Core.PostProcessing;
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
    private readonly IAudioCaptureService _audioCapture;
    private readonly OverlayViewModel _overlayVm;
    private readonly ILogger<MainViewModel> _logger;
    private readonly IPostProcessingService? _postProcessor;

    [ObservableProperty]
    private bool _isModelReady;

    [ObservableProperty]
    private bool _isListening;

    // Re-entrancy guards: a spurious or duplicate hotkey press (e.g. Alt auto-
    // repeat edge cases, or menu-activation swallowing a keyup) must not
    // trigger a second StartAsync while the first session is still live —
    // the underlying Foundry Local streaming handle cannot be started twice.
    private int _rawSessionBusy;
    private int _cleanSessionBusy;

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
        IAudioCaptureService audioCapture,
        OverlayViewModel overlayVm,
        ILogger<MainViewModel>? logger = null,
        IPostProcessingService? postProcessor = null)
    {
        _settings = settings;
        _settingsService = settingsService;
        _engine = engine;
        _dictationSession = dictationSession;
        _hotkeyService = hotkeyService;
        _soundEffects = soundEffects;
        _autoStart = autoStart;
        _audioCapture = audioCapture;
        _overlayVm = overlayVm;
        _logger = logger ?? NullLogger<MainViewModel>.Instance;
        _postProcessor = postProcessor;

        _overlayVm.OverlayPosition = settings.OverlayPosition;
        _overlayVm.OverlayOpacity = settings.OverlayOpacity;

        SettingsViewModel = new SettingsViewModel(settings);

        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _hotkeyService.HotkeyReleased += OnHotkeyReleased;
        _hotkeyService.CleanHotkeyPressed += OnCleanHotkeyPressed;
        _hotkeyService.CleanHotkeyReleased += OnCleanHotkeyReleased;

        _dictationSession.OnAudioLevel += level =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _overlayVm.AudioLevel = level;
            });

        _dictationSession.OnInterimText += text =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _overlayVm.UpdateInterimTranscript(text);
            });

        _dictationSession.OnCommittedChunk += chunk =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _overlayVm.AppendCommittedTranscript(chunk);
            });

        _dictationSession.OnSessionStopped += () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _overlayVm.IsListening = false;
                _overlayVm.AudioLevel = 0f;
                _overlayVm.ClearSessionTranscript();
                _overlayVm.IsFinalizing = false;
                IsListening = false;
            });

        _dictationSession.OnPostProcessingStateChanged += active =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _overlayVm.IsFinalizing = active;
            });
    }

    /// <summary>
    /// Downloads and loads the model if needed. Should be called once at startup.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        StatusMessage = "Starting Hush…";
        _overlayVm.PreparingDetail = "Starting Hush…";

        var progress = new Progress<double>(p =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ModelDownloadProgress = p;
                _overlayVm.ModelDownloadProgress = p;
                if (p > 0.0 && p < 1.0)
                    StatusMessage = $"Downloading… {p:P0}";
            });
        });

        var statusProgress = new Progress<string>(s =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _overlayVm.PreparingDetail = s;
                // Only overwrite the status line when we're not showing a
                // more specific "Downloading… xx%" message.
                if (ModelDownloadProgress <= 0.0 || ModelDownloadProgress >= 1.0)
                    StatusMessage = s;
            });
        });

        try
        {
            await _engine.InitializeAsync(
                _settings.TranscriptionModel, progress,
                statusProgress: statusProgress,
                cancellationToken: cancellationToken);

            IsModelReady = true;
            _overlayVm.IsModelReady = true;
            StatusMessage = BuildReadyStatusMessage();
            _logger.LogInformation("Hush is ready.");

            // If PostProcessingEnabled, warm up the LLM rewrite model in the background.
            if (_settings.PostProcessingEnabled && _postProcessor is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _postProcessor.InitializeAsync(_settings.PostProcessingModel, cancellationToken);
                        _logger.LogInformation("Post-processing model '{Model}' ready.", _settings.PostProcessingModel);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Post-processing model init failed; clean mode will use plain transcript.");
                    }
                });
            }

            // Warm up first-press code paths so the JIT doesn't stall the
            // initial hotkey press. A quick start/stop also validates that the
            // default microphone path is actually usable before the first dictation.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _dictationSession.StartAsync(_settings.Language, _settings.StreamingCommit,
                        showSpinner: false, postProcessingPrompt: null);
                    await _dictationSession.StopAsync();
                    _logger.LogDebug("JIT warmup complete.");
                }
                catch (Exception ex)
                {
                    var message = Hush.App.RuntimeUserMessageBuilder.BuildSessionErrorMessage(ex);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        _overlayVm.ErrorMessage = message;
                        StatusMessage = $"Error: {message}";
                    });
                    _logger.LogWarning(ex, "Startup dictation warmup failed.");
                }
            });
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
        var previousCleanHotkey = _settings.CleanHotkey;
        var previousPostProcessingModel = _settings.PostProcessingModel;
        SettingsViewModel.Apply();

        string? hotkeyError = null;
        if (!string.Equals(previousHotkey, _settings.Hotkey, StringComparison.Ordinal))
        {
            hotkeyError = TryApplyHotkeyChange(previousHotkey, _settings.Hotkey);
            if (hotkeyError is not null)
            {
                _settings.Hotkey = previousHotkey;
                SettingsViewModel.Hotkey = previousHotkey;
            }
        }

        string? cleanHotkeyError = null;
        if (!string.Equals(previousCleanHotkey, _settings.CleanHotkey, StringComparison.Ordinal))
        {
            cleanHotkeyError = TryApplyCleanHotkeyChange(previousCleanHotkey, _settings.CleanHotkey);
            if (cleanHotkeyError is not null)
            {
                _settings.CleanHotkey = previousCleanHotkey;
                SettingsViewModel.CleanHotkey = previousCleanHotkey;
            }
        }

        try
        {
            await _settingsService.SaveAsync(_settings, cancellationToken);
            await _autoStart.SetEnabledAsync(_settings.AutoStart, cancellationToken);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Failed to save settings.");
            var message = Hush.App.RuntimeUserMessageBuilder.BuildSettingsSaveMessage(ex);
            _overlayVm.ErrorMessage = message;
            StatusMessage = $"Error: {message}";
            return;
        }

        _overlayVm.OverlayPosition = _settings.OverlayPosition;
        _overlayVm.OverlayOpacity = _settings.OverlayOpacity;
        _audioCapture.DeviceIndex = _settings.MicrophoneDeviceIndex;

        // Re-initialize post-processor if the model alias changed.
        if (_settings.PostProcessingEnabled
            && _postProcessor is not null
            && !string.Equals(previousPostProcessingModel, _settings.PostProcessingModel, StringComparison.Ordinal))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _postProcessor.InitializeAsync(_settings.PostProcessingModel, cancellationToken);
                    _logger.LogInformation("Post-processing model updated to '{Model}'.", _settings.PostProcessingModel);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Post-processing model re-init failed.");
                }
            });
        }

        if (hotkeyError is not null)
        {
            _overlayVm.ErrorMessage = hotkeyError;
            StatusMessage = $"Error: {hotkeyError}";
        }
        else if (cleanHotkeyError is not null)
        {
            _overlayVm.ErrorMessage = cleanHotkeyError;
            StatusMessage = $"Error: {cleanHotkeyError}";
        }
        else if (IsModelReady)
        {
            StatusMessage = BuildReadyStatusMessage();
        }
    }

    private async void OnHotkeyPressed(object? sender, EventArgs e)
    {
        if (!IsModelReady)
            return;

        // Drop duplicate presses while a session is already starting or live.
        if (System.Threading.Interlocked.CompareExchange(ref _rawSessionBusy, 1, 0) != 0)
        {
            _logger.LogDebug("Ignoring duplicate raw hotkey press; session already active.");
            return;
        }

        try
        {
            // Hotkey events arrive on the hotkey STA thread — marshal UI mutations.
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsListening = true;
                _overlayVm.BeginSession(_settings.PartialsInOverlay, isCleanMode: false);
                _overlayVm.IsListening = true;
                _overlayVm.ErrorMessage = null;   // Clear previous error on new attempt.
            });

            if (_settings.SoundEffects)
                _ = _soundEffects.PlayStartAsync();

            // Post-processing only applies in batch (non-streaming) mode.
            var postProcessingPrompt = (!_settings.StreamingCommit && _settings.PostProcessingEnabled)
                ? _settings.GetActivePrompt().Prompt
                : null;

            await _dictationSession.StartAsync(
                _settings.Language,
                _settings.StreamingCommit,
                showSpinner: !_settings.StreamingCommit,
                postProcessingPrompt: postProcessingPrompt);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Failed to start dictation session.");

            // Self-heal: Foundry Local's native streaming session can leak if a
            // duplicate start raced against a stop. Force a stop so the next
            // press lands on a clean slate.
            try { await _dictationSession.StopAsync(); }
            catch (Exception stopEx) { _logger.LogWarning(stopEx, "Recovery StopAsync failed."); }

            try
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _overlayVm.IsListening = false;
                    IsListening = false;
                    _overlayVm.ErrorMessage = Hush.App.RuntimeUserMessageBuilder.BuildSessionErrorMessage(ex);
                    StatusMessage = $"Error: {_overlayVm.ErrorMessage}";
                });
            }
            catch { /* app may be shutting down */ }
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _rawSessionBusy, 0);
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

    private async void OnCleanHotkeyPressed(object? sender, EventArgs e)
    {
        if (!IsModelReady)
            return;

        // Drop duplicate presses while a clean session is already starting or live.
        if (System.Threading.Interlocked.CompareExchange(ref _cleanSessionBusy, 1, 0) != 0)
        {
            _logger.LogDebug("Ignoring duplicate clean hotkey press; session already active.");
            return;
        }

        try
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsListening = true;
                _overlayVm.BeginSession(_settings.PartialsInOverlay, isCleanMode: true);
                _overlayVm.IsListening = true;
                _overlayVm.ErrorMessage = null;
            });

            if (_settings.SoundEffects)
                _ = _soundEffects.PlayStartAsync();

            var prompt = (_settings.PostProcessingEnabled && _postProcessor?.IsReady == true)
                ? _settings.GetActivePrompt().Prompt
                : null;

            await _dictationSession.StartAsync(
                _settings.Language,
                streamingCommit: false,
                showSpinner: true,
                postProcessingPrompt: prompt);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Failed to start clean dictation session.");

            // Self-heal: clear any leaked native streaming session.
            try { await _dictationSession.StopAsync(); }
            catch (Exception stopEx) { _logger.LogWarning(stopEx, "Recovery StopAsync failed."); }

            try
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _overlayVm.IsListening = false;
                    IsListening = false;
                    _overlayVm.ErrorMessage = Hush.App.RuntimeUserMessageBuilder.BuildSessionErrorMessage(ex);
                    StatusMessage = $"Error: {_overlayVm.ErrorMessage}";
                });
            }
            catch { /* app may be shutting down */ }
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _cleanSessionBusy, 0);
        }
    }

    private async void OnCleanHotkeyReleased(object? sender, EventArgs e)
    {
        try
        {
            await _dictationSession.StopAsync();

            if (_settings.SoundEffects)
                _ = _soundEffects.PlayStopAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Failed to stop clean dictation session.");
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

    /// <summary>Triggers the raw hotkey pressed event (used by RemoteControlService).</summary>
    internal void TriggerHotkeyPressed() => OnHotkeyPressed(this, EventArgs.Empty);

    /// <summary>Triggers the raw hotkey released event (used by RemoteControlService).</summary>
    internal void TriggerHotkeyReleased() => OnHotkeyReleased(this, EventArgs.Empty);

    /// <summary>Triggers the clean hotkey pressed event (used by RemoteControlService).</summary>
    internal void TriggerCleanHotkeyPressed() => OnCleanHotkeyPressed(this, EventArgs.Empty);

    /// <summary>Triggers the clean hotkey released event (used by RemoteControlService).</summary>
    internal void TriggerCleanHotkeyReleased() => OnCleanHotkeyReleased(this, EventArgs.Empty);

    /// <summary>Cancels the active session if any (used by RemoteControlService).</summary>
    internal async void CancelSession()
    {
        try { await _dictationSession.StopAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "Cancel session error."); }
    }

    /// <summary>Returns the last committed transcript text from the overlay (used by RemoteControlService).</summary>
    internal string LastTranscript => _overlayVm.TranscriptText;

    private string? TryApplyHotkeyChange(string previousHotkey, string requestedHotkey)
    {
        try
        {
            _hotkeyService.Unregister();
            _hotkeyService.Register(requestedHotkey);
            _logger.LogInformation("Hotkey changed to '{Hotkey}'.", requestedHotkey);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to re-register hotkey '{Hotkey}'.", requestedHotkey);
            TryRestoreHotkey(previousHotkey);
            return Hush.App.RuntimeUserMessageBuilder.BuildHotkeyRegistrationMessage(ex, requestedHotkey);
        }
    }

    private string? TryApplyCleanHotkeyChange(string previousHotkey, string requestedHotkey)
    {
        try
        {
            _hotkeyService.UnregisterClean();
            _hotkeyService.RegisterClean(requestedHotkey);
            _logger.LogInformation("Clean hotkey changed to '{Hotkey}'.", requestedHotkey);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to re-register clean hotkey '{Hotkey}'.", requestedHotkey);
            TryRestoreCleanHotkey(previousHotkey);
            return Hush.App.RuntimeUserMessageBuilder.BuildHotkeyRegistrationMessage(ex, requestedHotkey);
        }
    }

    private void TryRestoreHotkey(string hotkey)
    {
        try
        {
            _hotkeyService.Unregister();
            _hotkeyService.Register(hotkey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore previous hotkey '{Hotkey}'.", hotkey);
        }
    }

    private void TryRestoreCleanHotkey(string hotkey)
    {
        try
        {
            _hotkeyService.UnregisterClean();
            _hotkeyService.RegisterClean(hotkey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore previous clean hotkey '{Hotkey}'.", hotkey);
        }
    }

    private string BuildReadyStatusMessage() =>
        $"Ready — hold {_settings.Hotkey} (raw) or {_settings.CleanHotkey} (clean)";
}
