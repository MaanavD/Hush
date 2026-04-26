// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Threading.Channels;
using Hush.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PortAudioSharp;
using PaStream = PortAudioSharp.Stream;

namespace Hush.Core.Audio;

/// <summary>
/// Captures microphone audio via PortAudio. Single backend across Windows
/// (WASAPI/MME), macOS (CoreAudio), and Linux (ALSA/PulseAudio).
/// Delivers 16 kHz / 16-bit / mono PCM chunks to the supplied callback.
/// </summary>
public sealed class AudioCaptureService : IAudioCaptureService
{
    public const int SystemDefaultDeviceNumber = -1;
    private const int SampleRate = 16000;
    private const int Channels = 1;
    // ~50 ms at 16 kHz — matches the chunk size used by the previous NAudio
    // backend so downstream timing assumptions in the transcription engine
    // continue to hold.
    private const uint FramesPerBuffer = 800;
    private const int BytesPerFrame = 2 * Channels;

    private readonly ILogger<AudioCaptureService> _logger;
    private readonly Lock _stateLock = new();
    private Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? _audioAvailable;
    private Channel<byte[]>? _audioQueue;
    private Task? _audioPumpTask;
    private CancellationTokenSource? _audioPumpCts;
    private PaStream? _stream;
    // Holding a strong reference prevents the JIT-emitted thunk from being
    // collected while PortAudio's audio thread is calling it. PortAudioSharp2
    // also pins it internally, but we keep our own reference for clarity.
    private PaStream.Callback? _callback;
    private bool _disposed;

    // Process-wide one-time init. Pa_Terminate followed by another
    // Pa_Initialize within the same process is fragile on the Windows MME
    // host (CLR fatal in test runners), so we initialize lazily and leave
    // PortAudio running until the process exits.
    private static int s_portAudioInitialized;  // 0 = not, 1 = yes
    private static readonly Lock s_initLock = new();

    /// <inheritdoc/>
    public int DeviceIndex { get; set; } = SystemDefaultDeviceNumber;

    /// <inheritdoc/>
    public event Action<float>? AudioLevelChanged;

    public AudioCaptureService(ILogger<AudioCaptureService>? logger = null)
    {
        _logger = logger ?? NullLogger<AudioCaptureService>.Instance;
    }

    /// <inheritdoc/>
    public void Start(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> audioAvailable)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(audioAvailable);

        lock (_stateLock)
        {
            if (_stream is not null)
                throw new InvalidOperationException("Audio capture is already running.");

            try
            {
                EnsurePortAudioInitialized();

                int device = ResolveInputDevice();
                var deviceInfo = PortAudio.GetDeviceInfo(device);

                if (deviceInfo.maxInputChannels <= 0)
                    throw new InvalidOperationException(
                        "The selected audio device has no input channels. Choose a different microphone in Settings.");

                var audioQueue = Channel.CreateUnbounded<byte[]>(
                    new UnboundedChannelOptions
                    {
                        SingleReader = true,
                        SingleWriter = false,
                        AllowSynchronousContinuations = false
                    });
                var pumpCts = new CancellationTokenSource();

                _audioAvailable = audioAvailable;
                _audioQueue = audioQueue;
                _audioPumpCts = pumpCts;
                _audioPumpTask = Task.Run(
                    () => PumpAudioAsync(audioQueue.Reader, audioAvailable, pumpCts.Token),
                    CancellationToken.None);
                _callback = StreamCallback;

                var inputParams = new StreamParameters
                {
                    device = device,
                    channelCount = Channels,
                    sampleFormat = SampleFormat.Int16,
                    // defaultLowInputLatency favors low-latency push-to-talk
                    // over rock-solid throughput; matches the previous 50 ms
                    // NAudio buffer profile.
                    suggestedLatency = deviceInfo.defaultLowInputLatency,
                    hostApiSpecificStreamInfo = IntPtr.Zero,
                };

                _stream = new PaStream(
                    inParams: inputParams,
                    outParams: null,
                    sampleRate: SampleRate,
                    framesPerBuffer: FramesPerBuffer,
                    streamFlags: StreamFlags.ClipOff,
                    callback: _callback,
                    userData: this);

                _stream.Start();

                _logger.LogInformation(
                    "Audio capture started (PortAudio, device={DeviceIndex} '{DeviceName}', hostApi={HostApi}, sampleRate={SampleRate}).",
                    device,
                    deviceInfo.name,
                    deviceInfo.hostApi,
                    SampleRate);
            }
            catch (Exception ex)
            {
                CleanupStream();
                var pump = DetachAudioPump(completeWriter: true);
                StopAudioPump(pump, cancel: true);
                _audioAvailable = null;
                _callback = null;
                throw BuildStartupException(ex);
            }
        }
    }

    private StreamCallbackResult StreamCallback(
        IntPtr input,
        IntPtr output,
        uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags,
        IntPtr userDataPtr)
    {
        // The PortAudio callback runs on a real-time audio thread. Anything
        // that throws here will tear the host process down, so we *must*
        // swallow exceptions and return Continue.
        try
        {
            var queue = _audioQueue;
            if (_audioAvailable is null || queue is null || input == IntPtr.Zero || frameCount == 0)
                return StreamCallbackResult.Continue;

            using var _prof = PerformanceProfiler.Measure("Audio.OnDataAvailable");

            int byteCount = checked((int)(frameCount * BytesPerFrame));
            var buffer = new byte[byteCount];
            Marshal.Copy(input, buffer, 0, byteCount);

            float rms;
            using (PerformanceProfiler.Measure("Audio.ComputeRms"))
                rms = ComputeRms(buffer, byteCount);
            try { AudioLevelChanged?.Invoke(rms); }
            catch { /* visualisation must never break capture */ }

            // Never await on PortAudio's real-time thread. A background pump
            // serializes appends to preserve audio order and drains on Stop().
            queue.Writer.TryWrite(buffer);
        }
        catch
        {
            // Don't log on the audio thread (allocator/IO); just swallow.
        }
        return StreamCallbackResult.Continue;
    }

    private async Task PumpAudioAsync(
        ChannelReader<byte[]> reader,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sink,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var buffer in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                using var _ = PerformanceProfiler.Measure("Audio.AppendPump");
                await sink(buffer, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Capture is shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio append pump failed.");
        }
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

    /// <inheritdoc/>
    public void Stop()
    {
        (Channel<byte[]>? Queue, Task? PumpTask, CancellationTokenSource? PumpCts) pump;
        lock (_stateLock)
        {
            CleanupStream();
            pump = DetachAudioPump(completeWriter: true);
            _audioAvailable = null;
            _callback = null;
        }
        StopAudioPump(pump, cancel: false);
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

    private void CleanupStream()
    {
        if (_stream is null)
            return;

        try { _stream.Stop(); }
        catch { /* best-effort */ }

        try { _stream.Close(); }
        catch { /* best-effort */ }

        try { _stream.Dispose(); }
        catch { /* best-effort */ }

        _stream = null;
    }

    private (Channel<byte[]>? Queue, Task? PumpTask, CancellationTokenSource? PumpCts) DetachAudioPump(bool completeWriter)
    {
        var queue = _audioQueue;
        var pumpTask = _audioPumpTask;
        var pumpCts = _audioPumpCts;

        _audioQueue = null;
        _audioPumpTask = null;
        _audioPumpCts = null;

        if (completeWriter)
            queue?.Writer.TryComplete();

        return (queue, pumpTask, pumpCts);
    }

    private void StopAudioPump(
        (Channel<byte[]>? Queue, Task? PumpTask, CancellationTokenSource? PumpCts) pump,
        bool cancel)
    {
        if (cancel)
            pump.PumpCts?.Cancel();

        if (pump.PumpTask is not null)
        {
            try
            {
                if (!pump.PumpTask.Wait(TimeSpan.FromSeconds(3)))
                {
                    _logger.LogWarning("Timed out waiting for queued audio appends to drain; cancelling the audio pump.");
                    pump.PumpCts?.Cancel();
                    pump.PumpTask.Wait(TimeSpan.FromSeconds(1));
                }
            }
            catch (AggregateException ex)
            {
                _logger.LogWarning(ex.Flatten(), "Audio append pump completed with an error during shutdown.");
            }
        }

        pump.PumpCts?.Dispose();
    }

    private static void EnsurePortAudioInitialized()
    {
        if (s_portAudioInitialized != 0) return;
        lock (s_initLock)
        {
            if (s_portAudioInitialized != 0) return;
            PortAudio.LoadNativeLibrary();
            PortAudio.Initialize();
            s_portAudioInitialized = 1;
        }
    }

    private int ResolveInputDevice()
    {
        if (DeviceIndex == SystemDefaultDeviceNumber)
        {
            int defaultDevice = PortAudio.DefaultInputDevice;
            if (defaultDevice == PortAudio.NoDevice)
                throw new InvalidOperationException(
                    "No microphone was found. Connect or enable a recording device, then try again.");
            return defaultDevice;
        }

        int deviceCount = PortAudio.DeviceCount;
        if (DeviceIndex < 0 || DeviceIndex >= deviceCount)
            throw new InvalidOperationException(
                $"The selected microphone is no longer available. Choose another in Settings (got index {DeviceIndex}, {deviceCount} devices present).");

        return DeviceIndex;
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

    /// <summary>
    /// Returns a list of available input devices as (index, name) pairs.
    /// Index <c>-1</c> is the system default. PortAudio is initialized once
    /// and kept alive for the process lifetime; calls here are cheap.
    /// </summary>
    public static List<(int Index, string Name)> GetAvailableDevices()
    {
        var devices = new List<(int, string)> { (SystemDefaultDeviceNumber, "System Default") };

        try
        {
            EnsurePortAudioInitialized();

            int count = PortAudio.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                DeviceInfo info;
                try { info = PortAudio.GetDeviceInfo(i); }
                catch { continue; }

                if (info.maxInputChannels <= 0)
                    continue;

                string name = string.IsNullOrWhiteSpace(info.name) ? $"Input device {i}" : info.name;
                devices.Add((i, name));
            }
        }
        catch
        {
            // Best-effort enumeration; settings UI should still show "System Default".
        }

        return devices;
    }
}
