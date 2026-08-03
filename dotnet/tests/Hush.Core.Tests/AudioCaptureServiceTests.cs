// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.Core.Audio;

namespace Hush.Core.Tests;

public sealed class AudioCaptureServiceTests
{
    [Theory]
    [InlineData("MMSYSERR_ALLOCATED", "busy or unavailable")]
    [InlineData("access denied by privacy control", "Microphone access is blocked")]
    [InlineData("waveInOpen failed for device", "system default microphone")]
    [InlineData("generic capture failure", "could not start microphone capture")]
    public void BuildStartupException_MapsDeviceFailuresToActionableMessages(string rawMessage, string expectedMessageFragment)
    {
        var translated = AudioCaptureService.BuildStartupException(new Exception(rawMessage));

        var invalidOperation = Assert.IsType<InvalidOperationException>(translated);
        Assert.Contains(expectedMessageFragment, invalidOperation.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildStartupException_PreservesExistingActionableExceptions()
    {
        var existing = new InvalidOperationException("No microphone was found.");

        var translated = AudioCaptureService.BuildStartupException(existing);

        Assert.Same(existing, translated);
    }
}