// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

namespace Hush.App;

/// <summary>Translates low-level exceptions from hotkey registration and dictation session startup into clear, actionable user-facing messages.</summary>
internal static class RuntimeUserMessageBuilder
{
    public static string BuildHotkeyRegistrationMessage(Exception exception, string hotkey)
    {
        if (exception is ArgumentException)
            return $"Hotkey '{hotkey}' is not valid. Use combinations like Ctrl+Shift+H or Alt+Space.";

        if (exception is PlatformNotSupportedException { Message: var platformMessage })
        {
            if (platformMessage.Contains("Accessibility", StringComparison.OrdinalIgnoreCase))
            {
                return "Hush cannot register its global hotkey until Accessibility access is granted in System Settings > Privacy & Security > Accessibility.";
            }

            if (platformMessage.Contains("Wayland", StringComparison.OrdinalIgnoreCase)
                || platformMessage.Contains("X11", StringComparison.OrdinalIgnoreCase)
                || platformMessage.Contains("DISPLAY", StringComparison.OrdinalIgnoreCase))
            {
                return "Global hotkeys on Linux require an X11 desktop session. Wayland-only sessions are not supported in this build.";
            }

            return platformMessage;
        }

        if (exception.Message.Contains("already be in use", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("in use", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("RegisterHotKey failed", StringComparison.OrdinalIgnoreCase))
        {
            return $"Hotkey '{hotkey}' is already in use by another application. Choose a different shortcut in Settings.";
        }

        return $"Could not register hotkey '{hotkey}'. Choose a different shortcut in Settings.";
    }

    public static string BuildSessionErrorMessage(Exception exception) => exception switch
    {
        InvalidOperationException { Message: var message } when IsActionableRuntimeMessage(message) => message,
        PlatformNotSupportedException { Message: var message } => message,
        OperationCanceledException => "Session was cancelled.",
        _ => "Could not start dictation. Check ~/.hush/hush.log for details."
    };

    public static string BuildSettingsSaveMessage(Exception exception) => exception switch
    {
        InvalidOperationException { Message: var message } when IsActionableRuntimeMessage(message) => message,
        UnauthorizedAccessException => "Hush could not save settings because it does not have permission to write its configuration files.",
        IOException => "Hush could not save settings because the configuration file is unavailable right now. Try again in a moment.",
        _ => "Hush could not save settings. Check ~/.hush/hush.log for details."
    };

    public static string BuildInitializationErrorMessage(Exception exception) => exception switch
    {
        InvalidOperationException { Message: var message } when IsActionableRuntimeMessage(message) || IsStartupMessage(message) => message,
        UnauthorizedAccessException => "Hush could not access its startup files because it does not have permission to read or write them.",
        IOException => "Hush could not access one of its startup files. Check that ~/.hush is available and try again.",
        _ => "Hush failed to start. Check ~/.hush/hush.log for details."
    };

    private static bool IsActionableRuntimeMessage(string message)
    {
        return message.Contains("microphone", StringComparison.OrdinalIgnoreCase)
            || message.Contains("audio", StringComparison.OrdinalIgnoreCase)
            || message.Contains("device", StringComparison.OrdinalIgnoreCase)
            || message.Contains("permission", StringComparison.OrdinalIgnoreCase)
            || message.Contains("privacy", StringComparison.OrdinalIgnoreCase)
            || message.Contains("elevated", StringComparison.OrdinalIgnoreCase)
            || message.Contains("administrator", StringComparison.OrdinalIgnoreCase)
            || message.Contains("hotkey", StringComparison.OrdinalIgnoreCase)
            || message.Contains("launch at login", StringComparison.OrdinalIgnoreCase)
            || message.Contains("auto-start", StringComparison.OrdinalIgnoreCase)
            || message.Contains("X11", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Accessibility", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStartupMessage(string message)
    {
        return message.Contains("Foundry Local", StringComparison.OrdinalIgnoreCase)
            || message.Contains("model", StringComparison.OrdinalIgnoreCase)
            || message.Contains("catalog", StringComparison.OrdinalIgnoreCase)
            || message.Contains("configuration", StringComparison.OrdinalIgnoreCase)
            || message.Contains("settings", StringComparison.OrdinalIgnoreCase);
    }
}