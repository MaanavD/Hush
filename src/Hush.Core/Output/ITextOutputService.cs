// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

namespace Hush.Core.Output;

/// <summary>
/// Types text into whichever application currently owns OS keyboard focus,
/// without touching the clipboard.
/// </summary>
public interface ITextOutputService
{
    /// <summary>
    /// Types <paramref name="text"/> into the currently focused application by
    /// simulating keyboard input. Returns when the keystrokes have been dispatched.
    /// </summary>
    Task TypeTextAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends <paramref name="count"/> backspace keystrokes to the focused application,
    /// erasing previously typed characters. Used for speculative commit corrections.
    /// </summary>
    Task SendBackspacesAsync(int count, CancellationToken cancellationToken = default);
}
