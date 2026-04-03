using Hush.Core.Output;

namespace Hush.Core.Tests;

/// <summary>
/// Tests for <see cref="SoundEffectService"/>.
/// Sound effects are platform-specific (NAudio on Windows, no-op elsewhere).
/// These tests verify the service doesn't crash and handles edge cases.
/// </summary>
public sealed class SoundEffectServiceTests
{
    [Fact]
    public async Task PlayStartAsync_DoesNotThrow()
    {
        var svc = new SoundEffectService();
        await svc.PlayStartAsync();
    }

    [Fact]
    public async Task PlayStopAsync_DoesNotThrow()
    {
        var svc = new SoundEffectService();
        await svc.PlayStopAsync();
    }

    [Fact]
    public async Task PlayStartAsync_WithCancellationToken_DoesNotThrow()
    {
        var svc = new SoundEffectService();
        using var cts = new CancellationTokenSource();
        await svc.PlayStartAsync(cts.Token);
    }

    [Fact]
    public async Task RapidPlayback_DoesNotCrash()
    {
        var svc = new SoundEffectService();

        // Simulate rapid hotkey press/release (user tapping quickly)
        var tasks = Enumerable.Range(0, 5).Select(async _ =>
        {
            await svc.PlayStartAsync();
            await svc.PlayStopAsync();
        });

        await Task.WhenAll(tasks);
    }
}
