// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Hush.App.ViewModels;
using Hush.App.Views;
using Hush.Core.Audio;
using Hush.Core.Configuration;
using Hush.Core.Input;
using Hush.Core.Output;
using Hush.Core.Session;
using Hush.Core.Transcription;
using Microsoft.Extensions.Logging;

namespace Hush.App;

/// <summary>Application entry point. Bootstraps logging, services, hotkey registration, and the overlay/tray UI.</summary>
public sealed class App : Application
{
    private MainViewModel? _mainVm;
    private OverlayWindow? _overlayWindow;
    private TrayIcon? _trayIcon;

    // Disposable resources — cleaned up on shutdown.
    private ILoggerFactory? _loggerFactory;
    private IGlobalHotkeyService? _hotkeyService;
    private IDictationSession? _dictationSession;
    private ITranscriptionEngine? _transcriptionEngine;
    private IAudioCaptureService? _audioCapture;
    private ILogger<App>? _logger;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        // Prevent the app from exiting when all windows are closed — it lives in the tray.
        desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".hush", "hush.log");

        _loggerFactory = LoggerFactory.Create(b => b
            .AddConsole()
            .AddProvider(new FileLoggerProvider(logPath)));
        _logger = _loggerFactory.CreateLogger<App>();

        var settingsService = new SettingsService(_loggerFactory.CreateLogger<SettingsService>());
        var settings = await settingsService.LoadAsync();

        _transcriptionEngine = new TranscriptionEngine(_loggerFactory.CreateLogger<TranscriptionEngine>());
        _audioCapture = new AudioCaptureService(_loggerFactory.CreateLogger<AudioCaptureService>());
        var textOutput = new KeystrokeTypingService(
            _loggerFactory.CreateLogger<KeystrokeTypingService>(),
            useClipboardFallback: settings.ClipboardFallback);
        _dictationSession = new DictationSession(
            _transcriptionEngine, _audioCapture, textOutput,
            _loggerFactory.CreateLogger<DictationSession>());

        var platformHotkeyProvider = CreatePlatformHotkeyProvider();
        _hotkeyService = new GlobalHotkeyService(platformHotkeyProvider, _loggerFactory.CreateLogger<GlobalHotkeyService>());

        var overlayVm = new OverlayViewModel();
        var soundEffects = new SoundEffectService(_loggerFactory.CreateLogger<SoundEffectService>());
        var autoStart = new AutoStartService(_loggerFactory.CreateLogger<AutoStartService>());
        _mainVm = new MainViewModel(
            settings, settingsService, _transcriptionEngine,
            _dictationSession, _hotkeyService, soundEffects, autoStart, overlayVm,
            _loggerFactory.CreateLogger<MainViewModel>());

        _overlayWindow = new OverlayWindow { DataContext = overlayVm };
        _overlayWindow.Show();   // Show once so visibility changes never re-activate the window.

        _trayIcon = new TrayIcon(_mainVm, desktop);

        try
        {
            _hotkeyService.Register(settings.Hotkey);
        }
        catch (Exception ex)
        {
            var message = RuntimeUserMessageBuilder.BuildHotkeyRegistrationMessage(ex, settings.Hotkey);
            overlayVm.ErrorMessage = message;
            _mainVm.StatusMessage = $"Error: {message}";
            _logger.LogError(ex, "Failed to register startup hotkey '{Hotkey}'.", settings.Hotkey);
        }

        // Wire cleanup on shutdown.
        desktop.ShutdownRequested += OnShutdownRequested;

        await _mainVm.InitializeAsync();

        base.OnFrameworkInitializationCompleted();
    }

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        try
        {
            _hotkeyService?.Unregister();
            _hotkeyService?.Dispose();

            if (_dictationSession is not null)
                await _dictationSession.DisposeAsync();

            if (_transcriptionEngine is not null)
                await _transcriptionEngine.DisposeAsync();

            _audioCapture?.Dispose();
            _trayIcon?.Dispose();
            _loggerFactory?.Dispose();
        }
        catch
        {
            // Best-effort cleanup; do not block shutdown.
        }
    }

    private static IGlobalHotkeyService CreatePlatformHotkeyProvider()
    {
        if (OperatingSystem.IsWindows())
            return new Platforms.Windows.WindowsHotkeyProvider();
        if (OperatingSystem.IsMacOS())
            return new Platforms.macOS.MacHotkeyProvider();
        if (OperatingSystem.IsLinux())
            return new Platforms.Linux.LinuxHotkeyProvider();
        throw new PlatformNotSupportedException("No hotkey provider for this platform.");
    }
}
