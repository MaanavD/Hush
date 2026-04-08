// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.Core.Input;
using Moq;

namespace Hush.Core.Tests;

/// <summary>
/// Tests for <see cref="GlobalHotkeyService"/> covering the aggregation layer
/// and event forwarding from platform providers.
/// </summary>
public sealed class GlobalHotkeyServiceTests
{
    // ── Event forwarding ─────────────────────────────────────────────────

    [Fact]
    public void HotkeyPressed_ForwardedFromInner()
    {
        var innerMock = new Mock<IGlobalHotkeyService>();
        var service = new GlobalHotkeyService(innerMock.Object);

        bool pressed = false;
        service.HotkeyPressed += (_, _) => pressed = true;

        innerMock.Raise(s => s.HotkeyPressed += null, EventArgs.Empty);

        Assert.True(pressed);
        service.Dispose();
    }

    [Fact]
    public void HotkeyReleased_ForwardedFromInner()
    {
        var innerMock = new Mock<IGlobalHotkeyService>();
        var service = new GlobalHotkeyService(innerMock.Object);

        bool released = false;
        service.HotkeyReleased += (_, _) => released = true;

        innerMock.Raise(s => s.HotkeyReleased += null, EventArgs.Empty);

        Assert.True(released);
        service.Dispose();
    }

    // ── Register / Unregister delegation ─────────────────────────────────

    [Fact]
    public void Register_DelegatesToInner()
    {
        var innerMock = new Mock<IGlobalHotkeyService>();
        var service = new GlobalHotkeyService(innerMock.Object);

        service.Register("Ctrl+Shift+H");

        innerMock.Verify(s => s.Register("Ctrl+Shift+H"), Times.Once);
        service.Dispose();
    }

    [Fact]
    public void Unregister_DelegatesToInner()
    {
        var innerMock = new Mock<IGlobalHotkeyService>();
        var service = new GlobalHotkeyService(innerMock.Object);

        service.Unregister();

        innerMock.Verify(s => s.Unregister(), Times.Once);
        service.Dispose();
    }

    // ── Dispose delegation ───────────────────────────────────────────────

    [Fact]
    public void Dispose_DelegatesToInner()
    {
        var innerMock = new Mock<IGlobalHotkeyService>();
        var service = new GlobalHotkeyService(innerMock.Object);

        service.Dispose();

        innerMock.Verify(s => s.Dispose(), Times.Once);
    }

    // ── Rapid press/release sequence ─────────────────────────────────────

    [Fact]
    public void RapidPressRelease_AllEventsForwarded()
    {
        var innerMock = new Mock<IGlobalHotkeyService>();
        var service = new GlobalHotkeyService(innerMock.Object);

        int pressCount = 0;
        int releaseCount = 0;
        service.HotkeyPressed += (_, _) => pressCount++;
        service.HotkeyReleased += (_, _) => releaseCount++;

        // Simulate rapid hotkey usage
        for (int i = 0; i < 20; i++)
        {
            innerMock.Raise(s => s.HotkeyPressed += null, EventArgs.Empty);
            innerMock.Raise(s => s.HotkeyReleased += null, EventArgs.Empty);
        }

        Assert.Equal(20, pressCount);
        Assert.Equal(20, releaseCount);
        service.Dispose();
    }

    // ── No subscribers — does not throw ──────────────────────────────────

    [Fact]
    public void NoSubscribers_InnerEventsDoNotThrow()
    {
        var innerMock = new Mock<IGlobalHotkeyService>();
        var service = new GlobalHotkeyService(innerMock.Object);

        // Fire inner events without any subscribers on the outer service
        innerMock.Raise(s => s.HotkeyPressed += null, EventArgs.Empty);
        innerMock.Raise(s => s.HotkeyReleased += null, EventArgs.Empty);

        // Should not throw
        service.Dispose();
    }

    // ── Hotkey format strings ────────────────────────────────────────────

    [Theory]
    [InlineData("Ctrl+Shift+H")]
    [InlineData("Alt+Space")]
    [InlineData("F5")]
    [InlineData("Ctrl+Alt+Shift+F12")]
    public void Register_AcceptsVariousHotkeyFormats(string hotkey)
    {
        var innerMock = new Mock<IGlobalHotkeyService>();
        var service = new GlobalHotkeyService(innerMock.Object);

        service.Register(hotkey);
        innerMock.Verify(s => s.Register(hotkey), Times.Once);

        service.Dispose();
    }
}
