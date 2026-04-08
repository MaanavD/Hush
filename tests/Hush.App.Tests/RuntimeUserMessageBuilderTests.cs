// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.App;

namespace Hush.App.Tests;

public sealed class RuntimeUserMessageBuilderTests
{
    [Fact]
    public void BuildHotkeyRegistrationMessage_FormatsHotkeyConflict()
    {
        var message = RuntimeUserMessageBuilder.BuildHotkeyRegistrationMessage(
            new InvalidOperationException("RegisterHotKey failed with Win32 error 1409. The hotkey may already be in use by another application."),
            "Ctrl+Shift+H");

        Assert.Contains("already in use", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ctrl+Shift+H", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildHotkeyRegistrationMessage_FormatsMacAccessibilityGuidance()
    {
        var message = RuntimeUserMessageBuilder.BuildHotkeyRegistrationMessage(
            new PlatformNotSupportedException("Grant Accessibility permission to Hush in System Settings."),
            "Ctrl+Shift+H");

        Assert.Contains("Accessibility", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildHotkeyRegistrationMessage_FormatsLinuxWaylandGuidance()
    {
        var message = RuntimeUserMessageBuilder.BuildHotkeyRegistrationMessage(
            new PlatformNotSupportedException("Could not open an X11 display connection. Wayland-only sessions are not supported in Hush v1."),
            "Ctrl+Shift+H");

        Assert.Contains("X11", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Wayland", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildSessionErrorMessage_PreservesActionableMicrophoneMessage()
    {
        var message = RuntimeUserMessageBuilder.BuildSessionErrorMessage(
            new InvalidOperationException("Microphone access is blocked. Grant Hush permission to use the microphone in your OS privacy settings, then try again."));

        Assert.Contains("Microphone access is blocked", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildSessionErrorMessage_PreservesElevatedWindowMessage()
    {
        var message = RuntimeUserMessageBuilder.BuildSessionErrorMessage(
            new InvalidOperationException("Cannot type into an elevated (administrator) application. Run Hush as administrator, or switch to a non-elevated window."));

        Assert.Contains("administrator", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildSessionErrorMessage_UsesFallbackForUnknownFailure()
    {
        var message = RuntimeUserMessageBuilder.BuildSessionErrorMessage(new Exception("boom"));

        Assert.Contains("Check ~/.hush/hush.log", message, StringComparison.Ordinal);
    }
}