// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

namespace Hush.Core.Output;

/// <summary>
/// Provides start/stop audio feedback for dictation sessions.
/// Implementations must be safe to call from any thread.
/// </summary>
public interface ISoundEffectService
{
    /// <summary>Plays a short tone indicating that recording has started.</summary>
    Task PlayStartAsync(CancellationToken cancellationToken = default);

    /// <summary>Plays a short tone indicating that recording has stopped.</summary>
    Task PlayStopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Plays a short ascending chime to signal that LLM post-processing has
    /// completed and the rewritten text is about to be typed.
    /// </summary>
    Task PlayProcessingCompleteAsync(CancellationToken cancellationToken = default);
}
