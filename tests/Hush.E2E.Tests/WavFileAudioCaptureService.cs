using Hush.Core.Audio;

namespace Hush.E2E.Tests;

internal sealed class WavFileAudioCaptureService : IAudioCaptureService
{
    private const int DefaultChunkDurationMilliseconds = 50;

    private readonly WavPcmFile _wav;
    private readonly double _delayScale;
    private readonly int _chunkDurationMilliseconds;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _streamCts;

    public WavFileAudioCaptureService(WavPcmFile wav, double delayScale, int chunkDurationMilliseconds = DefaultChunkDurationMilliseconds)
    {
        _wav = wav;
        _delayScale = delayScale;
        _chunkDurationMilliseconds = chunkDurationMilliseconds > 0
            ? chunkDurationMilliseconds
            : DefaultChunkDurationMilliseconds;
    }

    public event Action<float>? AudioLevelChanged;

    public event Action? AudioStreamingStarted;

    public int DeviceIndex { get; set; } = -1;

    public Task Completion => _completion.Task;

    public void Start(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> audioAvailable)
    {
        _streamCts = new CancellationTokenSource();
        _ = Task.Run(() => StreamAsync(audioAvailable, _streamCts.Token), CancellationToken.None);
    }

    public void Stop() => _streamCts?.Cancel();

    public void Dispose()
    {
        try
        {
            _streamCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _streamCts?.Dispose();
    }

    private async Task StreamAsync(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> audioAvailable,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytesPerSampleFrame = _wav.Channels * (_wav.BitsPerSample / 8);
            var chunkSize = _wav.SampleRate * bytesPerSampleFrame * _chunkDurationMilliseconds / 1000;
            if (chunkSize <= 0)
                throw new InvalidOperationException($"Invalid WAV chunk size for {_wav.FilePath}.");

            var notifiedStart = false;
            for (int offset = 0; offset < _wav.PcmData.Length; offset += chunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(chunkSize, _wav.PcmData.Length - offset);
                var chunk = _wav.PcmData.AsMemory(offset, length);
                if (!notifiedStart)
                {
                    AudioStreamingStarted?.Invoke();
                    notifiedStart = true;
                }

                AudioLevelChanged?.Invoke(CalculateRmsLevel(chunk.Span));
                await audioAvailable(chunk, cancellationToken);

                if (_delayScale > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(_chunkDurationMilliseconds * _delayScale), cancellationToken);
            }

            _completion.TrySetResult();
        }
        catch (OperationCanceledException ex)
        {
            _completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            _completion.TrySetException(ex);
        }
    }

    private static float CalculateRmsLevel(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length < 2)
            return 0;

        double sumSquares = 0;
        int sampleCount = pcm.Length / 2;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            short sample = BitConverter.ToInt16(pcm[i..(i + 2)]);
            double normalized = sample / 32768.0;
            sumSquares += normalized * normalized;
        }

        return (float)Math.Sqrt(sumSquares / sampleCount);
    }
}
