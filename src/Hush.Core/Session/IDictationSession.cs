// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.Core.Configuration;

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

    /// <summary>Raised when the session encounters a runtime error after it has already started.</summary>
    event Action<Exception>? OnSessionError;

    /// <summary>Starts microphone capture and the transcription loop.</summary>
    /// <param name="language">BCP-47 language tag for the transcription model.</param>
    /// <param name="streamingCommit">
    /// When <see langword="true"/>, words are committed progressively as they
    /// stabilise. When <see langword="false"/>, all text is held until the
    /// session stops (batch mode).
    /// </param>
    /// <param name="showSpinner">
    /// When <see langword="true"/>, a rotating indicator character is animated
    /// in the focused application while dictation is active. All committed text
    /// is buffered and typed in one shot after the session ends.
    /// </param>
    /// <param name="postProcessingPrompt">
    /// Optional LLM post-processing prompt. Reserved for <c>feature/llm-postprocessing</c>;
    /// pass <see langword="null"/> to skip LLM post-processing.
    /// </param>
    /// <param name="autoSubmitKey">Key combination to send after dictation ends.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task StartAsync(
        string language = "en",
        bool streamingCommit = true,
        bool showSpinner = false,
        string? postProcessingPrompt = null,
        AutoSubmitKey autoSubmitKey = AutoSubmitKey.None,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops microphone capture, signals the transcription session to finalise,
    /// and waits for all pending committed text to be typed.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
