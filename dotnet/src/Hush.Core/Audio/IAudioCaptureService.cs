// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

namespace Hush.Core.Audio;

/// <summary>
/// Captures microphone audio as 16 kHz / 16-bit / mono PCM and delivers it
/// to a caller-supplied async callback (typically the transcription engine's
/// append method).
/// </summary>
public interface IAudioCaptureService : IDisposable
{
    /// <summary>
    /// Starts capturing audio from the configured microphone.
    /// Each captured buffer is forwarded to <paramref name="audioAvailable"/>.
    /// </summary>
    void Start(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> audioAvailable);

    /// <summary>Stops capturing audio.</summary>
    void Stop();

    /// <summary>
    /// Gets or sets the device index to use. <c>-1</c> means system default.
    /// Must be set before calling <see cref="Start"/>.
    /// </summary>
    int DeviceIndex { get; set; }

    /// <summary>
    /// Raised on each audio buffer with a normalised RMS level in [0, 1].
    /// Used to drive live visualisation in the overlay.
    /// </summary>
    event Action<float>? AudioLevelChanged;
}
