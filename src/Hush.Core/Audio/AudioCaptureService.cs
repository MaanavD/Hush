using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hush.Core.Audio;

/// <summary>
/// Captures microphone audio via a platform-appropriate backend.
/// On Windows, NAudio's <c>WaveInEvent</c> is used. On macOS/Linux, a
/// compatible backend will be selected during the validation spike
/// (see SPEC §14 open question 2).
/// </summary>
public sealed class AudioCaptureService : IAudioCaptureService
{
    private readonly ILogger<AudioCaptureService> _logger;
    private Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? _audioAvailable;
    private bool _disposed;

    /// <inheritdoc/>
    public event Action<float>? AudioLevelChanged;

#if WINDOWS
    private NAudio.Wave.WaveInEvent? _waveIn;
#endif

    public AudioCaptureService(ILogger<AudioCaptureService>? logger = null)
    {
        _logger = logger ?? NullLogger<AudioCaptureService>.Instance;
    }

    /// <inheritdoc/>
    public void Start(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> audioAvailable)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _audioAvailable = audioAvailable;

#if WINDOWS
        _waveIn = new NAudio.Wave.WaveInEvent
        {
            WaveFormat = new NAudio.Wave.WaveFormat(rate: 16000, bits: 16, channels: 1),
            BufferMilliseconds = 40
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.StartRecording();
        _logger.LogInformation("Audio capture started (NAudio/WaveInEvent).");
#else
        _logger.LogError("Audio capture is only supported on Windows in this version.");
        throw new PlatformNotSupportedException(
            "Microphone capture is currently supported on Windows only. "
            + "macOS and Linux audio backends are planned for a future release.");
#endif
    }

#if WINDOWS
    private void OnDataAvailable(object? sender, NAudio.Wave.WaveInEventArgs e)
    {
        if (_audioAvailable is null || e.BytesRecorded <= 0)
            return;

        // Copy buffer because NAudio reuses it after this callback returns.
        var copy = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);

        // Compute RMS from signed 16-bit PCM and fire for visualisation.
        AudioLevelChanged?.Invoke(ComputeRms(e.Buffer, e.BytesRecorded));

        // Fire-and-forget; transcription SDK handles backpressure internally.
        _ = _audioAvailable(copy, CancellationToken.None);
    }

    private static float ComputeRms(byte[] buffer, int byteCount)
    {
        int samples = byteCount / 2;
        if (samples == 0) return 0f;
        double sumSq = 0.0;
        double peak = 0.0;
        for (int i = 0; i < byteCount - 1; i += 2)
        {
            short s = (short)(buffer[i] | (buffer[i + 1] << 8));
            double norm = s / 32768.0;
            sumSq += norm * norm;
            double magnitude = Math.Abs(norm);
            if (magnitude > peak)
                peak = magnitude;
        }

        double rms = Math.Sqrt(sumSq / samples);
        double boosted = Math.Max(rms * 7.5, peak * 2.6);
        return (float)Math.Clamp(Math.Pow(boosted, 0.8), 0.0, 1.0);
    }
#endif

    /// <inheritdoc/>
    public void Stop()
    {
#if WINDOWS
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveIn = null;
#endif
        _audioAvailable = null;
        _logger.LogInformation("Audio capture stopped.");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _disposed = true;
    }
}
