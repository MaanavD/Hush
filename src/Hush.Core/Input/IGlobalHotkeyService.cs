namespace Hush.Core.Input;

/// <summary>
/// Registers and unregisters a global (system-wide) hotkey.
/// Implementations are per-platform (Windows: RegisterHotKey,
/// macOS: CGEventTap, Linux: XGrabKey).
/// </summary>
public interface IGlobalHotkeyService : IDisposable
{
    /// <summary>Raised when the hotkey transitions from up to down (pressed).</summary>
    event EventHandler? HotkeyPressed;

    /// <summary>Raised when the hotkey transitions from down to up (released).</summary>
    event EventHandler? HotkeyReleased;

    /// <summary>
    /// Registers the global hotkey described by <paramref name="hotkey"/>,
    /// e.g. <c>"Ctrl+Shift+H"</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the hotkey is already in use by another application.
    /// </exception>
    void Register(string hotkey);

    /// <summary>Unregisters the current global hotkey if one is registered.</summary>
    void Unregister();
}
