// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

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
    private const int DefaultDeviceNumber = -1;

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
        ArgumentNullException.ThrowIfNull(audioAvailable);

#if WINDOWS
        if (NAudio.Wave.WaveIn.DeviceCount <= 0)
            throw new InvalidOperationException(
                "No microphone was found. Connect or enable a recording device, then try again.");

        _audioAvailable = audioAvailable;

        try
        {
            _waveIn = new NAudio.Wave.WaveInEvent
            {
                DeviceNumber = DefaultDeviceNumber,
                WaveFormat = new NAudio.Wave.WaveFormat(rate: 16000, bits: 16, channels: 1),
                BufferMilliseconds = 50
            };

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();
            _logger.LogInformation(
                "Audio capture started (NAudio/WaveInEvent, device='{DeviceName}', deviceCount={DeviceCount}).",
                GetDefaultDeviceLabel(),
                NAudio.Wave.WaveIn.DeviceCount);
        }
        catch (Exception ex)
        {
            CleanupWaveIn();
            _audioAvailable = null;
            throw BuildStartupException(ex);
        }
#else
        _logger.LogError("Audio capture is only supported on Windows in this version.");
        throw new PlatformNotSupportedException(
            "Microphone capture is not yet available on this platform in the current Hush build. "
            + "The default dictation path is currently supported on Windows; "
            + "macOS and Linux capture backends are still pending.");
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
        CleanupWaveIn();
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

    internal static Exception BuildStartupException(Exception exception)
    {
        if (exception is InvalidOperationException or PlatformNotSupportedException)
            return exception;

        var message = exception.Message;

        if (message.Contains("allocated", StringComparison.OrdinalIgnoreCase)
            || message.Contains("busy", StringComparison.OrdinalIgnoreCase)
            || message.Contains("MMSYSERR_ALLOCATED", StringComparison.OrdinalIgnoreCase))
        {
            return new InvalidOperationException(
                "The current microphone is busy or unavailable. Close other recording apps or change the system default input device, then try again.",
                exception);
        }

        if (message.Contains("access", StringComparison.OrdinalIgnoreCase)
            || message.Contains("permission", StringComparison.OrdinalIgnoreCase)
            || message.Contains("privacy", StringComparison.OrdinalIgnoreCase)
            || message.Contains("denied", StringComparison.OrdinalIgnoreCase))
        {
            return new InvalidOperationException(
                "Microphone access is blocked. Grant Hush permission to use the microphone in your OS privacy settings, then try again.",
                exception);
        }

        if (message.Contains("device", StringComparison.OrdinalIgnoreCase)
            || message.Contains("driver", StringComparison.OrdinalIgnoreCase)
            || message.Contains("waveIn", StringComparison.OrdinalIgnoreCase)
            || message.Contains("microphone", StringComparison.OrdinalIgnoreCase))
        {
            return new InvalidOperationException(
                "Hush could not start the system default microphone. Check that an input device is connected and available, then try again.",
                exception);
        }

        return new InvalidOperationException(
            "Hush could not start microphone capture. Check your audio device and permissions, then try again.",
            exception);
    }

#if WINDOWS
    private void CleanupWaveIn()
    {
        if (_waveIn is null)
            return;

        _waveIn.DataAvailable -= OnDataAvailable;

        try
        {
            _waveIn.StopRecording();
        }
        catch
        {
            // Best-effort cleanup only.
        }

        _waveIn.Dispose();
        _waveIn = null;
    }

    private static string GetDefaultDeviceLabel()
    {
        try
        {
            return NAudio.Wave.WaveIn.GetCapabilities(DefaultDeviceNumber).ProductName;
        }
        catch
        {
            return "system default";
        }
    }
#endif
}
