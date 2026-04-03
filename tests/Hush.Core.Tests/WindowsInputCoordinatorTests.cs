using Hush.Core.Output;

namespace Hush.Core.Tests;

/// <summary>
/// Tests for <see cref="WindowsInputCoordinator"/> which manages the
/// synchronization between clipboard paste and hotkey release detection.
///
/// Note: WindowsInputCoordinator uses a static counter. Because xUnit runs
/// tests in parallel across classes, other tests (e.g. KeystrokeTypingServiceTests)
/// may concurrently modify the suppression state. These tests verify relative
/// behaviour (scope create/dispose changes) rather than absolute state.
/// </summary>
public sealed class WindowsInputCoordinatorTests
{
    [Fact]
    public void SuppressScope_SetsAndResetsSuppression()
    {
        bool beforeScope = WindowsInputCoordinator.IsHotkeyReleaseDetectionSuppressed;

        using (WindowsInputCoordinator.SuppressHotkeyReleaseDetection())
        {
            Assert.True(WindowsInputCoordinator.IsHotkeyReleaseDetectionSuppressed);
        }

        // After dispose, state should return to what it was before
        Assert.Equal(beforeScope, WindowsInputCoordinator.IsHotkeyReleaseDetectionSuppressed);
    }

    [Fact]
    public void NestedScopes_AllMustDispose()
    {
        bool beforeScope = WindowsInputCoordinator.IsHotkeyReleaseDetectionSuppressed;

        var outer = WindowsInputCoordinator.SuppressHotkeyReleaseDetection();
        Assert.True(WindowsInputCoordinator.IsHotkeyReleaseDetectionSuppressed);

        var inner = WindowsInputCoordinator.SuppressHotkeyReleaseDetection();
        Assert.True(WindowsInputCoordinator.IsHotkeyReleaseDetectionSuppressed);

        inner.Dispose();
        // Still suppressed because outer scope is active
        Assert.True(WindowsInputCoordinator.IsHotkeyReleaseDetectionSuppressed);

        outer.Dispose();
        Assert.Equal(beforeScope, WindowsInputCoordinator.IsHotkeyReleaseDetectionSuppressed);
    }

    [Fact]
    public void DoubleDispose_IsIdempotent()
    {
        bool beforeScope = WindowsInputCoordinator.IsHotkeyReleaseDetectionSuppressed;

        var scope = WindowsInputCoordinator.SuppressHotkeyReleaseDetection();
        Assert.True(WindowsInputCoordinator.IsHotkeyReleaseDetectionSuppressed);

        scope.Dispose();
        bool afterFirstDispose = WindowsInputCoordinator.IsHotkeyReleaseDetectionSuppressed;

        // Second dispose should not decrement again
        scope.Dispose();
        Assert.Equal(afterFirstDispose, WindowsInputCoordinator.IsHotkeyReleaseDetectionSuppressed);
    }

    [Fact]
    public async Task ConcurrentScopes_ThreadSafe()
    {
        // Verify thread-safety of the counter under concurrent access
        int completed = 0;
        var barrier = new Barrier(10);

        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (int i = 0; i < 100; i++)
            {
                using var scope = WindowsInputCoordinator.SuppressHotkeyReleaseDetection();
                Assert.True(WindowsInputCoordinator.IsHotkeyReleaseDetectionSuppressed);
            }
            Interlocked.Increment(ref completed);
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(10, completed);
    }

    [Fact]
    public void InjectedExtraInfo_HasExpectedValue()
    {
        // This constant is used by the hotkey provider to identify injected events.
        // If it changes, the hotkey provider will stop filtering correctly.
        Assert.Equal((nuint)0x48555348, WindowsInputCoordinator.InjectedExtraInfo);
    }
}
