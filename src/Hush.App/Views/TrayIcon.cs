// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Hush.App.ViewModels;
using Hush.App.Views;

namespace Hush.App;

/// <summary>
/// System-tray icon and context menu.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly MainViewModel _mainVm;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly ITranscriptBuffer? _transcriptBuffer;
    private readonly global::Avalonia.Controls.TrayIcon _tray;
    private NativeMenuItem? _statusItem;
    private SettingsWindow? _settingsWindow;

    public TrayIcon(
        MainViewModel mainVm,
        IClassicDesktopStyleApplicationLifetime desktop,
        ITranscriptBuffer? transcriptBuffer = null)
    {
        _mainVm = mainVm;
        _desktop = desktop;
        _transcriptBuffer = transcriptBuffer;

        _tray = new global::Avalonia.Controls.TrayIcon
        {
            Icon = LoadTrayIcon(),
            ToolTipText = "Hush — Starting…",
            Menu = BuildContextMenu()
        };

        global::Avalonia.Controls.TrayIcon.SetIcons(
            Avalonia.Application.Current!,
            new global::Avalonia.Controls.TrayIcons { _tray });

        // Keep tooltip in sync with MainViewModel status.
        _mainVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.StatusMessage) or nameof(MainViewModel.IsListening))
                UpdateStatus();
        };

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var status = _mainVm.IsListening
                ? "🎙 Listening…"
                : _mainVm.StatusMessage;

            _tray.ToolTipText = $"Hush — {status}";

            if (_statusItem is not null)
                _statusItem.Header = status;
        });
    }

    private NativeMenu BuildContextMenu()
    {
        var menu = new NativeMenu();

        // 1. Status (non-clickable)
        _statusItem = new NativeMenuItem("Starting…") { IsEnabled = false };
        menu.Add(_statusItem);
        menu.Add(new NativeMenuItemSeparator());

        // 2. Keyboard reminders (non-clickable)
        menu.Add(new NativeMenuItem("Ctrl+H — Dictate") { IsEnabled = false });
        menu.Add(new NativeMenuItem("Alt+H — Dictate & clean") { IsEnabled = false });
        menu.Add(new NativeMenuItemSeparator());

        // 3. Copy last dictation (no-op if transcript buffer not wired yet)
        var copyItem = new NativeMenuItem("Copy last dictation");
        copyItem.Click += (_, _) =>
        {
            var text = _transcriptBuffer?.GetLatestTranscript();
            if (text is null) return;
            // Clipboard wiring deferred until ITranscriptBuffer PR merges.
        };
        menu.Add(copyItem);

        // 4. Settings
        var settingsItem = new NativeMenuItem("Settings…");
        settingsItem.Click += (_, _) => OpenSettings();
        menu.Add(settingsItem);

        menu.Add(new NativeMenuItemSeparator());

        // 5. Quit
        var quitItem = new NativeMenuItem("Quit Hush");
        quitItem.Click += (_, _) => _desktop.Shutdown();
        menu.Add(quitItem);

        return menu;
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        // Refresh the microphone list each time settings opens, in case
        // devices were plugged/unplugged since last time.
        _mainVm.SettingsViewModel.RefreshMicrophones();

        _settingsWindow = new SettingsWindow
        {
            DataContext = _mainVm.SettingsViewModel
        };
        _settingsWindow.Closed += async (_, _) =>
        {
            // Auto-save when the settings window is closed.
            await _mainVm.SaveSettingsAsync();
        };
        _settingsWindow.Show();
    }

    private static global::Avalonia.Controls.WindowIcon? LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("avares://Hush.App/Assets/hush-icon.png");
            using var stream = Avalonia.Platform.AssetLoader.Open(uri);
            var bitmap = new Bitmap(stream);
            return new global::Avalonia.Controls.WindowIcon(bitmap);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() { /* TrayIcon lifecycle managed by Avalonia */ }
}

// Stub until feature/dictionary-buffer-extras merges into master.
// TODO: Remove once the real ITranscriptBuffer arrives from that PR.
#if !HUSH_CORE_POSTPROCESSING
public interface ITranscriptBuffer
{
    /// <summary>Returns the most recent full transcript, or <c>null</c> if none.</summary>
    string? GetLatestTranscript();
}
#endif
