// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.Core.Configuration;

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
    /// <param name="skipModifierRestore">When <see langword="true"/>, held modifier
    /// keys (Ctrl/Shift/Alt/Win) are released before typing but NOT re-pressed
    /// afterwards. Use this during rapid sequential typing (streaming) to prevent
    /// the modifier release/restore cycle from corrupting keystrokes.</param>
    Task TypeTextAsync(string text, CancellationToken cancellationToken = default, bool skipModifierRestore = false);

    /// <summary>
    /// Sends <paramref name="count"/> backspace keystrokes to the focused application,
    /// erasing previously typed characters. Used for speculative commit corrections.
    /// </summary>
    Task SendBackspacesAsync(int count, CancellationToken cancellationToken = default, bool skipModifierRestore = false);

    /// <summary>
    /// Replaces recently typed text with <paramref name="replacementText"/>.
    /// Implementations may use platform-specific safer replacement strategies
    /// when <paramref name="boundToCurrentLine"/> is requested.
    /// </summary>
    Task ReplaceTextAsync(
        int backspaceCount,
        string replacementText,
        CancellationToken cancellationToken = default,
        bool skipModifierRestore = false,
        bool boundToCurrentLine = false,
        string? expectedExistingText = null);

    /// <summary>Sends a single well-known key combination to the focused application.</summary>
    Task SendKeyAsync(AutoSubmitKey key, CancellationToken cancellationToken = default);
}
