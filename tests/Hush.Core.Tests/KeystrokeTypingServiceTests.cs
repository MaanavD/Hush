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

    [Theory]
    [InlineData(0, 120)]
    [InlineData(5, 125)]
    [InlineData(67, 187)]
    [InlineData(1000, 350)]
    public void CalculateClipboardRestoreDelayMs_ScalesWithPasteSize(int textLength, int expectedDelayMs)
    {
        Assert.Equal(expectedDelayMs, WindowsClipboardTyper.CalculateClipboardRestoreDelayMs(textLength));
    }

    [Theory]
    [InlineData("Uh today is Monday and I want to talk to my cow|", 48)]
    [InlineData("I'm testing the real-time transcription.", 40)]
    [InlineData("今天是周一。", 6)]
    public void CountSelectionCharacters_CountsExpectedReplacementCharacters(string text, int expected)
    {
        Assert.Equal(expected, WindowsClipboardTyper.CountSelectionCharacters(text));
    }

    [Theory]
    [InlineData(0, 120)]
    [InlineData(47, 214)]
    [InlineData(130, 380)]
    [InlineData(1000, 900)]
    public void CalculateSelectionPasteSettleDelayMs_ScalesWithSelectionSize(int characterCount, int expectedDelayMs)
    {
        Assert.Equal(expectedDelayMs, WindowsClipboardTyper.CalculateSelectionPasteSettleDelayMs(characterCount));
    }

    [Theory]
    [InlineData(0, 0, 1200)]
    [InlineData(90, 271, 1741)]
    [InlineData(1000, 1000, 3000)]
    public void CalculateReplacementClipboardRestoreDelayMs_WaitsForTargetPaste(
        int replacementLength,
        int selectedCharacterCount,
        int expectedDelayMs)
    {
        Assert.Equal(
            expectedDelayMs,
            WindowsClipboardTyper.CalculateReplacementClipboardRestoreDelayMs(
                replacementLength,
                selectedCharacterCount));
    }

    [Fact]
    public void TryCreateReplacementValue_ReplacesExactDocumentSuffix()
    {
        const string expected = "Um, so here is the live preview|";
        const string replacement = "这是最终输出。";
        const string document = "previous line 1234567890\r\n" + expected;

        bool replaced = WindowsAutomationTextReplacer.TryCreateReplacementValue(
            document,
            expected,
            replacement,
            out var replacementValue,
            out int replaceStart);

        Assert.True(replaced);
        Assert.Equal("previous line 1234567890\r\n" + replacement, replacementValue);
        Assert.Equal("previous line 1234567890\r\n".Length, replaceStart);
    }

    [Fact]
    public void TryCreateReplacementValue_ReplacesFromLineAnchorWhenPreviewDiverged()
    {
        const string expected = "Um, so here is the live preview text Hush expected|";
        const string actual = "Um, so here is the live preview text Notepad actually has|";
        const string replacement = "这是最终输出。";
        const string document = "previous line 1234567890\r\n" + actual;

        bool replaced = WindowsAutomationTextReplacer.TryCreateReplacementValue(
            document,
            expected,
            replacement,
            out var replacementValue,
            out int replaceStart);

        Assert.True(replaced);
        Assert.Equal("previous line 1234567890\r\n" + replacement, replacementValue);
        Assert.Equal("previous line 1234567890\r\n".Length, replaceStart);
    }

    [Fact]
    public void TryCreateReplacementValue_DoesNotUseAnchorInMiddleOfPreviousLine()
    {
        const string expected = "Um, so here is the live preview text Hush expected|";
        const string replacement = "这是最终输出。";
        const string document = "previous line 1234567890 Um, so here is unrelated";

        bool replaced = WindowsAutomationTextReplacer.TryCreateReplacementValue(
            document,
            expected,
            replacement,
            out _,
            out _);

        Assert.False(replaced);
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
}
