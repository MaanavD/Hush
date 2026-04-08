// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

namespace Hush.Core.Transcription;

/// <summary>
/// Represents a single transcription event emitted by the engine.
/// </summary>
/// <param name="DisplayText">
/// The most recent text to show in the overlay. May include unstable interim
/// hypotheses; intended for display only.
/// </param>
/// <param name="CommittedDelta">
/// The stable text segment that should be typed into the focused application.
/// Empty when the current event is an interim-only update.
/// </param>
/// <param name="IsFinal">
/// <see langword="true"/> when this result ends the transcription stream
/// (i.e. the session was stopped and all final text has been flushed).
/// </param>
/// <param name="StartTime">Optional start timestamp relative to session start.</param>
/// <param name="EndTime">Optional end timestamp relative to session start.</param>
public sealed record TranscriptionResult(
    string DisplayText,
    string CommittedDelta,
    bool IsFinal,
    TimeSpan? StartTime = null,
    TimeSpan? EndTime = null)
{
    /// <summary>
    /// Number of characters to erase (via backspace) before typing
    /// <see cref="CommittedDelta"/>. Used for speculative commit corrections
    /// when the model revises previously committed text.
    /// </summary>
    public int BackspaceCount { get; init; }
}
