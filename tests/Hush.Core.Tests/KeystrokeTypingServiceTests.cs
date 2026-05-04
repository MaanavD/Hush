// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.Core.Output;
using Moq;

namespace Hush.Core.Tests;

/// <summary>
/// Tests for <see cref="KeystrokeTypingService"/> verifying the text output
/// dispatch logic. Actual keystroke injection (SendInput / CGEvent) is tested
/// via integration or manual testing. These tests verify routing and edge cases.
/// </summary>
public sealed class KeystrokeTypingServiceTests
{
    // ── Empty / null text ────────────────────────────────────────────────

    [Fact]
    public async Task TypeTextAsync_EmptyString_ReturnsImmediately()
    {
        var svc = new KeystrokeTypingService();
        // Should complete instantly without touching any OS API
        await svc.TypeTextAsync(string.Empty);
    }

    [Fact]
    public async Task TypeTextAsync_NullString_ReturnsImmediately()
    {
        var svc = new KeystrokeTypingService();
        await svc.TypeTextAsync(null!);
    }

    [Fact]
    public void GetModifiersToRestore_RestoresOnlyStillPressedModifiers()
    {
        IReadOnlyList<ushort> modifiersToRestore = WindowsKeystrokeTyper.GetModifiersToRestore(
            releasedModifiers: new ushort[] { 0xA2, 0xA0 },
            physicallyPressedModifiers: new ushort[] { 0xA0 });

        Assert.Equal(new ushort[] { 0xA0 }, modifiersToRestore);
    }

    [Fact]
    public void GetModifiersToRestore_WhenNothingStillPressed_ReturnsEmpty()
    {
        IReadOnlyList<ushort> modifiersToRestore = WindowsKeystrokeTyper.GetModifiersToRestore(
            releasedModifiers: new ushort[] { 0xA2, 0xA0 },
            physicallyPressedModifiers: Array.Empty<ushort>());

        Assert.Empty(modifiersToRestore);
    }

    // ── Platform dispatch (only testable on current platform) ────────────

    [Fact]
    public async Task TypeTextAsync_OnCurrentPlatform_DoesNotThrow()
    {
        // This test verifies the service can be instantiated and invoked
        // without crashing. Actual text output goes to the test runner's
        // focused window. We skip assertion on what was typed.
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            // Unsupported platform — verify it throws PlatformNotSupportedException
            var svc = new KeystrokeTypingService();
            await Assert.ThrowsAsync<PlatformNotSupportedException>(
                () => svc.TypeTextAsync("test"));
            return;
        }

        // On supported platforms, at minimum the service should not crash.
        // We might type into the test runner output — that's acceptable.
        var service = new KeystrokeTypingService();

        // Type a guaranteed-safe string (single space)
        // This validates the entire clipboard-paste pipeline on Windows.
        try
        {
            await service.TypeTextAsync(" ");
        }
        catch (InvalidOperationException)
        {
            // SendInput may fail if test runner is in a restricted context
            // (e.g., service mode, no desktop session). That's okay.
        }
        catch (DllNotFoundException)
        {
            // Native libraries (e.g. libX11 on Linux) may be absent in headless
            // CI environments. That's acceptable — the service is not expected to
            // work without the underlying display server.
        }
    }

    // ── Cancellation ─────────────────────────────────────────────────────

    [Fact]
    public async Task TypeTextAsync_CancelledToken_Throws()
    {
        var svc = new KeystrokeTypingService();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // CancellationToken is checked before clipboard operations
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.TypeTextAsync("test", cts.Token));
    }

    [Fact]
    public void GetPasteCompletionDelay_TsfWindowsUseLongerClipboardDelay()
    {
        int defaultDelay = WindowsClipboardTyper.GetPasteCompletionDelay(isTsfProblematic: false);
        int tsfDelay = WindowsClipboardTyper.GetPasteCompletionDelay(isTsfProblematic: true);

        Assert.True(tsfDelay > defaultDelay);
    }
}
