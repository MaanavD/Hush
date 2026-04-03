namespace Hush.Core.Session;

/// <summary>
/// Controls a single push-to-talk dictation session.
/// </summary>
public interface IDictationSession : IAsyncDisposable
{
    /// <summary>
    /// Raised when the transcription engine produces an interim (possibly unstable)
    /// display text update. The overlay should reflect this immediately.
    /// <para>
    /// <b>Important:</b> Do NOT type this text into the target application —
    /// it may be revised by subsequent interim events. Only committed text
    /// (see <see cref="OnCommittedChunk"/>) is typed.
    /// </para>
    /// </summary>
    event Action<string>? OnInterimText;

    /// <summary>Raised when a stable, committed text segment has been typed into the
    /// focused application. The overlay can use this to visually confirm output.
    /// </summary>
    event Action<string>? OnCommittedChunk;

    /// <summary>
    /// Raised continuously while the session is active with a normalised RMS
    /// audio level in [0, 1]. Use this to drive live visualisation.
    /// </summary>
    event Action<float>? OnAudioLevel;

    /// <summary>Raised when the session has fully stopped and all output has been flushed.</summary>
    event Action? OnSessionStopped;

    /// <summary>Starts microphone capture and the transcription loop.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops microphone capture, signals the transcription session to finalise,
    /// and waits for all pending committed text to be typed.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
