// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hush.Core.Configuration;

/// <summary>
/// Registers or unregisters Hush as an OS login startup item.
/// <list type="bullet">
///   <item>Windows — <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c></item>
///   <item>macOS — <c>~/Library/LaunchAgents/com.hush.app.plist</c></item>
///   <item>Linux — <c>~/.config/autostart/hush.desktop</c> (XDG autostart)</item>
/// </list>
/// The executable path is resolved via <see cref="Environment.ProcessPath"/>.
/// </summary>
public sealed class AutoStartService : IAutoStartService
{
    private const string AppName = "Hush";
    private const string MacPlistId = "com.hush.app";

    private readonly ILogger<AutoStartService> _logger;

    public AutoStartService(ILogger<AutoStartService>? logger = null)
        => _logger = logger ?? NullLogger<AutoStartService>.Instance;

    /// <inheritdoc/>
    public Task SetEnabledAsync(bool enable, CancellationToken cancellationToken = default)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                SetWindowsAutoStart(enable);
            else if (OperatingSystem.IsMacOS())
                SetMacAutoStart(enable);
            else if (OperatingSystem.IsLinux())
                SetLinuxAutoStart(enable);
            else
                _logger.LogWarning("Auto-start is not supported on this platform.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to {Action} auto-start.", enable ? "enable" : "disable");
            throw new InvalidOperationException(
                enable
                    ? "Hush could not enable launch at login. Check OS permissions and try again."
                    : "Hush could not disable launch at login. Check OS permissions and try again.",
                ex);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return Task.FromResult(IsWindowsAutoStartEnabled());
            if (OperatingSystem.IsMacOS())
                return Task.FromResult(File.Exists(MacPlistPath));
            if (OperatingSystem.IsLinux())
                return Task.FromResult(File.Exists(LinuxDesktopPath));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query auto-start state.");
        }

        return Task.FromResult(false);
    }

    // ── Windows ──────────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private static void SetWindowsAutoStart(bool enable)
    {
        const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        if (enable)
        {
            // OpenSubKey returns null if the key doesn't exist; ?? ensures we create it instead.
            // Only one of the two calls executes, so a single `using` safely disposes the result.
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true)
                ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(runKeyPath);
            key.SetValue(AppName, GetExecutablePath());
        }
        else
        {
            // When disabling, a missing key means the app was never registered — treat as no-op.
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
            key?.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsAutoStartEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue(AppName) is not null;
    }

    // ── macOS ─────────────────────────────────────────────────────────────────

    private static string MacPlistPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", $"{MacPlistId}.plist");

    private static void SetMacAutoStart(bool enable)
    {
        if (enable)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MacPlistPath)!);
            File.WriteAllText(MacPlistPath, BuildMacPlist());
        }
        else
        {
            if (File.Exists(MacPlistPath))
                File.Delete(MacPlistPath);
        }
    }

    private static string BuildMacPlist()
    {
        var execPath = GetExecutablePath();
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
                "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{MacPlistId}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{execPath}</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <false/>
            </dict>
            </plist>
            """;
    }

    // ── Linux (XDG autostart) ─────────────────────────────────────────────────

    private static string LinuxDesktopPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "autostart", "hush.desktop");

    private static void SetLinuxAutoStart(bool enable)
    {
        if (enable)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LinuxDesktopPath)!);
            File.WriteAllText(LinuxDesktopPath, BuildDesktopFile());
        }
        else
        {
            if (File.Exists(LinuxDesktopPath))
                File.Delete(LinuxDesktopPath);
        }
    }

    private static string BuildDesktopFile()
    {
        var execPath = GetExecutablePath();
        return $"""
            [Desktop Entry]
            Type=Application
            Name={AppName}
            Exec={execPath}
            Hidden=false
            NoDisplay=false
            X-GNOME-Autostart-enabled=true
            Comment=Hush offline voice-to-text
            """;
    }

    // ── Shared ────────────────────────────────────────────────────────────────

    private static string GetExecutablePath() =>
        Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "Cannot determine the current executable path. " +
                "Auto-start cannot be configured.");
}
