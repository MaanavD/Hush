// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
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
            .SetMinimumLevel(LogLevel.Debug)
            .AddConsole()
            .AddProvider(new FileLoggerProvider(logPath)));
        _logger = _loggerFactory.CreateLogger<App>();
        RegisterUnhandledExceptionHandlers();

        try
        {
            var settingsService = new SettingsService(_loggerFactory.CreateLogger<SettingsService>());
            var settings = await settingsService.LoadAsync();

            _transcriptionEngine = new TranscriptionEngine(_loggerFactory.CreateLogger<TranscriptionEngine>());
            _audioCapture = new AudioCaptureService(_loggerFactory.CreateLogger<AudioCaptureService>())
            {
                DeviceIndex = settings.MicrophoneDeviceIndex
            };
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
                _dictationSession, _hotkeyService, soundEffects, autoStart, _audioCapture, overlayVm,
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
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            HandleStartupFailure(desktop, ex);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void RegisterUnhandledExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                _logger?.LogCritical(ex, "Unhandled application exception.");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _logger?.LogError(e.Exception, "Unobserved task exception.");
            e.SetObserved();
        };
    }

    private void HandleStartupFailure(IClassicDesktopStyleApplicationLifetime desktop, Exception exception)
    {
        var message = RuntimeUserMessageBuilder.BuildInitializationErrorMessage(exception);
        _logger?.LogCritical(exception, "Fatal startup error.");
        Console.Error.WriteLine(message);

        if (_overlayWindow?.DataContext is OverlayViewModel overlayVm)
            overlayVm.ErrorMessage = message;

        if (_mainVm is not null)
            _mainVm.StatusMessage = $"Error: {message}";

        if (desktop.MainWindow is null)
        {
            desktop.MainWindow = BuildStartupFailureWindow(message);
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow.Show();
        }
    }

    private static Window BuildStartupFailureWindow(string message)
    {
        return new Window
        {
            Title = "Hush — Startup Error",
            Width = 460,
            Height = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Hush couldn’t finish starting.",
                        FontSize = 18,
                        FontWeight = Avalonia.Media.FontWeight.SemiBold
                    },
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = "Check ~/.hush/hush.log for more detail.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Opacity = 0.75
                    }
                }
            }
        };
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
