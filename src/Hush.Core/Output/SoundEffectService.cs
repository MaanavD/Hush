// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
#if WINDOWS
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
#endif

namespace Hush.Core.Output;

/// <summary>
/// Plays short, pleasant two-note chimes to signal dictation start and stop.
/// <list type="bullet">
///   <item>Windows — NAudio sine-wave synthesis with cosine-windowed fade-in/out
///   eliminates the clickiness of <see cref="Console.Beep"/>. Start = ascending
///   880→1109 Hz, stop = descending 660→523 Hz.</item>
///   <item>macOS / Linux — no-op; native audio API integration is deferred to
///   a future milestone.</item>
/// </list>
/// </summary>
public sealed class SoundEffectService : ISoundEffectService
{
    private readonly ILogger<SoundEffectService> _logger;

    public SoundEffectService(ILogger<SoundEffectService>? logger = null)
        => _logger = logger ?? NullLogger<SoundEffectService>.Instance;

    /// <inheritdoc/>
    public Task PlayStartAsync(CancellationToken cancellationToken = default)
    {
#if WINDOWS
        return PlayChimeAsync([880.0, 1109.0], [90, 70]);
#else
        return Task.CompletedTask;
#endif
    }

    /// <inheritdoc/>
    public Task PlayStopAsync(CancellationToken cancellationToken = default)
    {
#if WINDOWS
        return PlayChimeAsync([660.0, 523.0], [90, 70]);
#else
        return Task.CompletedTask;
#endif
    }

#if WINDOWS
    /// <summary>
    /// Plays a multi-note chime by concatenating sine-wave segments and awaiting
    /// playback completion asynchronously so callers are not blocked.
    /// </summary>
    private Task PlayChimeAsync(double[] frequencies, int[] durations)
    {
        try
        {
            ISampleProvider[] segments = frequencies.Zip(durations)
                .Select(p => (ISampleProvider)new FadedSineProvider(p.First, p.Second))
                .ToArray();

            var source = new ConcatenatingSampleProvider(segments);
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var waveOut = new WaveOutEvent { DesiredLatency = 50 };
            waveOut.Init(source);
            waveOut.PlaybackStopped += (_, _) =>
            {
                waveOut.Dispose();
                tcs.TrySetResult();
            };
            waveOut.Play();
            return tcs.Task;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sound effect playback failed.");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Single-frequency sine wave with cosine-window fade-in and fade-out (≈12 ms)
    /// to eliminate clicks at note boundaries. Amplitude is kept at 22% to blend
    /// naturally with desktop audio without being intrusive.
    /// </summary>
    private sealed class FadedSineProvider : ISampleProvider
    {
        private const int SampleRate = 44100;
        private const float PeakAmplitude = 0.22f;

        private readonly WaveFormat _format;
        private readonly double _frequency;
        private readonly int _totalSamples;
        private int _position;
        private double _phase;

        public FadedSineProvider(double frequency, int durationMs)
        {
            _format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);
            _frequency = frequency;
            _totalSamples = durationMs * SampleRate / 1000;
        }

        public WaveFormat WaveFormat => _format;

        public int Read(float[] buffer, int offset, int count)
        {
            int remaining = _totalSamples - _position;
            int toRead = Math.Min(count, remaining);
            double increment = 2 * Math.PI * _frequency / SampleRate;
            // ~12 ms fade window
            int fadeSamples = SampleRate / 80;

            for (int i = 0; i < toRead; i++)
            {
                float env;
                if (_position < fadeSamples)
                    // fade in via raised cosine
                    env = PeakAmplitude * (float)(0.5 - 0.5 * Math.Cos(Math.PI * _position / fadeSamples));
                else if (_position >= _totalSamples - fadeSamples)
                    // fade out via raised cosine
                    env = PeakAmplitude * (float)(0.5 - 0.5 * Math.Cos(Math.PI * (_totalSamples - _position) / fadeSamples));
                else
                    env = PeakAmplitude;

                buffer[offset + i] = (float)(Math.Sin(_phase) * env);
                _phase += increment;
                if (_phase >= 2 * Math.PI) _phase -= 2 * Math.PI;
                _position++;
            }

            return toRead;
        }
    }
#endif
}

