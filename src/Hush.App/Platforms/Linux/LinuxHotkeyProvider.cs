// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hush.Core.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hush.App.Platforms.Linux;

/// <summary>
/// Global hotkey provider for Linux using X11 <c>XGrabKey</c> on the root window.
/// Wayland-only sessions are not supported in v1 (the user will see a clear error).
/// </summary>
/// <remarks>
/// XGrabKey establishes a passive key grab: when the requested chord is pressed,
/// X11 routes <c>KeyPress</c> and <c>KeyRelease</c> events to our connection.
/// A background polling thread calls <c>XPending</c> / <c>XNextEvent</c> to
/// process them without blocking.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxHotkeyProvider : IGlobalHotkeyService
{
    private const string X11 = "libX11";

    [DllImport(X11)] private static extern nint XOpenDisplay(string? displayName);
    [DllImport(X11)] private static extern int XCloseDisplay(nint display);
    [DllImport(X11)] private static extern nint XDefaultRootWindow(nint display);
    [DllImport(X11)] private static extern int XSelectInput(nint display, nint window, long eventMask);
    [DllImport(X11)] private static extern int XGrabKey(
        nint display, int keycode, uint modifiers, nint grabWindow,
        bool ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(X11)] private static extern int XUngrabKey(
        nint display, int keycode, uint modifiers, nint grabWindow);
    [DllImport(X11)] private static extern int XNextEvent(nint display, out XEvent @event);
    [DllImport(X11)] private static extern int XPending(nint display);
    [DllImport(X11)] private static extern int XFlush(nint display);
    [DllImport(X11)] private static extern ulong XStringToKeysym(string @string);
    [DllImport(X11)] private static extern int XKeysymToKeycode(nint display, ulong keysym);

    // X11 event types
    private const int KeyPress = 2;
    private const int KeyRelease = 3;

    // X11 modifier masks
    private const uint ShiftMask = 1;
    private const uint LockMask = 2;   // CapsLock
    private const uint ControlMask = 4;
    private const uint Mod1Mask = 8;   // Alt
    private const uint Mod2Mask = 16;  // NumLock
    private const uint Mod4Mask = 64;  // Super / Win key
    private const uint AnyModifier = 0x8000;

    // XGrabKey pointer/keyboard modes
    private const int GrabModeAsync = 1;

    // XSelectInput mask for key events
    private const long KeyPressMask = 1;
    private const long KeyReleaseMask = 2;

    // X11 generic XEvent — 192 bytes on 64-bit (24 × sizeof(long)).
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct XEvent
    {
        [FieldOffset(0)]  public int Type;
        [FieldOffset(8)]  public ulong Serial;
        [FieldOffset(16)] public int SendEvent;
        [FieldOffset(24)] public nint Display;
        [FieldOffset(32)] public nint Window;
        [FieldOffset(40)] public nint Root;
        [FieldOffset(48)] public nint Subwindow;
        [FieldOffset(56)] public ulong Time;
        [FieldOffset(64)] public int X;
        [FieldOffset(68)] public int Y;
        [FieldOffset(72)] public int XRoot;
        [FieldOffset(76)] public int YRoot;
        [FieldOffset(80)] public uint State;    // active modifier mask
        [FieldOffset(84)] public uint Keycode;
        [FieldOffset(88)] public int SameScreen;
    }

    // ── State ────────────────────────────────────────────────────────────────
    private readonly ILogger<LinuxHotkeyProvider> _logger;
    private nint _display;
    private nint _rootWindow;
    private int _keycode;
    private uint _modMask;
    private Thread? _thread;
    private volatile bool _disposed;

    public event EventHandler? HotkeyPressed;
    public event EventHandler? HotkeyReleased;

    public LinuxHotkeyProvider(ILogger<LinuxHotkeyProvider>? logger = null)
        => _logger = logger ?? NullLogger<LinuxHotkeyProvider>.Instance;

    /// <inheritdoc/>
    public void Register(string hotkey)
    {
        _display = XOpenDisplay(null);
        if (_display == 0)
            throw new PlatformNotSupportedException(
                "Could not open an X11 display connection. " +
                "Ensure the DISPLAY environment variable is set. " +
                "Wayland-only sessions are not supported in Hush v1.");

        _rootWindow = XDefaultRootWindow(_display);
        ParseHotkey(hotkey, out _keycode, out _modMask);

        if (_keycode == 0)
            throw new ArgumentException($"Unrecognised hotkey: '{hotkey}'.");

        // Select key events on the root window so passive grabs deliver to us.
        XSelectInput(_display, _rootWindow, KeyPressMask | KeyReleaseMask);

        // Grab with common lock-key variants so presses still fire when e.g. NumLock is on.
        foreach (uint extra in LockVariants())
            XGrabKey(_display, _keycode, _modMask | extra, _rootWindow, false, GrabModeAsync, GrabModeAsync);

        XFlush(_display);

        _thread = new Thread(PollLoop) { IsBackground = true, Name = "Hush.X11HotkeyPoll" };
        _thread.Start();
        _logger.LogInformation("Linux hotkey '{Hotkey}' grabbed (keycode={Kc}, mods=0x{M:X}).",
            hotkey, _keycode, _modMask);
    }

    private void PollLoop()
    {
        while (!_disposed)
        {
            if (XPending(_display) > 0)
            {
                XNextEvent(_display, out var ev);
                // Strip lock-key bits before comparing modifiers.
                uint cleanState = ev.State & ~(LockMask | Mod2Mask);

                if (ev.Type == KeyPress && ev.Keycode == (uint)_keycode && cleanState == _modMask)
                    HotkeyPressed?.Invoke(this, EventArgs.Empty);
                else if (ev.Type == KeyRelease && ev.Keycode == (uint)_keycode)
                    HotkeyReleased?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Thread.Sleep(8); // ~8 ms polling — imperceptible for a hotkey
            }
        }
    }

    /// <inheritdoc/>
    public void Unregister()
    {
        if (_display == 0) return;

        foreach (uint extra in LockVariants())
            XUngrabKey(_display, _keycode, _modMask | extra, _rootWindow);

        XCloseDisplay(_display);
        _display = 0;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void ParseHotkey(string hotkey, out int keycode, out uint modMask)
    {
        keycode = 0;
        modMask = 0;

        foreach (var part in hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modMask |= ControlMask; break;
                case "shift":             modMask |= ShiftMask; break;
                case "alt":               modMask |= Mod1Mask; break;
                case "super" or "win":    modMask |= Mod4Mask; break;
                default:
                    // XStringToKeysym accepts X11 keysym names (case-sensitive) like "space", "Return"
                    var xname = part.ToLowerInvariant() switch
                    {
                        "space" => "space", "return" => "Return", "tab" => "Tab",
                        "escape" or "esc" => "Escape", "delete" => "Delete",
                        _ => part // single letters work directly
                    };
                    ulong keysym = XStringToKeysym(xname);
                    if (keysym != 0)
                        keycode = XKeysymToKeycode(_display, keysym);
                    break;
            }
        }
    }

    /// <summary>
    /// Returns the extra modifier masks to grab to handle CapsLock / NumLock combos.
    /// Grabbing with {base}, {base+CapsLock}, {base+NumLock}, {base+CapsLock+NumLock}
    /// ensures the chord fires regardless of which lock keys are active.
    /// </summary>
    private static IEnumerable<uint> LockVariants()
    {
        yield return 0;
        yield return LockMask;
        yield return Mod2Mask;
        yield return LockMask | Mod2Mask;
    }
}

