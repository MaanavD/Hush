namespace Hush.E2E.Tests;

public sealed class WavFileAudioCaptureServiceTests
{
    [Fact]
    public async Task Start_RaisesAudioStreamingStartedBeforeFirstChunk()
    {
        var wav = new WavPcmFile(
            "test.wav",
            AudioFormat: 1,
            Channels: 1,
            SampleRate: 16000,
            BitsPerSample: 16,
            new byte[1600]);
        using var capture = new WavFileAudioCaptureService(wav, delayScale: 0, chunkDurationMilliseconds: 25);
        var events = new List<string>();

        capture.AudioStreamingStarted += () => events.Add("started");
        capture.Start((_, _) =>
        {
            events.Add("chunk");
            return ValueTask.CompletedTask;
        });

        await capture.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("started", events[0]);
        Assert.Contains("chunk", events);
        Assert.Single(events, item => item == "started");
    }
}
