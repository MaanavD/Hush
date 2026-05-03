using System.Buffers.Binary;

namespace Hush.E2E.Tests;

public sealed class WavPcmFileTests
{
    [Fact]
    public void DetectVoiceOnsetMilliseconds_ReturnsFirstSampleAboveThreshold()
    {
        var pcm = new byte[1600 * 2];
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(800 * 2, 2), 700);
        var wav = new WavPcmFile("test.wav", AudioFormat: 1, Channels: 1, SampleRate: 16000, BitsPerSample: 16, pcm);

        Assert.Equal(50, wav.DetectVoiceOnsetMilliseconds());
    }

    [Fact]
    public void DetectVoiceOnsetMilliseconds_ReturnsZeroForSilence()
    {
        var wav = new WavPcmFile("test.wav", AudioFormat: 1, Channels: 1, SampleRate: 16000, BitsPerSample: 16, new byte[3200]);

        Assert.Equal(0, wav.DetectVoiceOnsetMilliseconds());
    }
}
