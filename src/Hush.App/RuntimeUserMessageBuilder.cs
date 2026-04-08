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
            || message.Contains("X11", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Accessibility", StringComparison.OrdinalIgnoreCase);
    }
}