// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Runtime.InteropServices;
using Hush.Core.Input;
using Hush.Core.Output;

namespace Hush.App.Platforms.Windows;

/// <summary>
/// Registers a global hotkey on Windows using <c>RegisterHotKey</c> / <c>UnregisterHotKey</c>
/// from user32.dll and a message-pump thread to receive <c>WM_HOTKEY</c> messages.
/// <para>
/// Key-release detection tracks <b>physical</b> key-up events through the
/// low-level keyboard hook. This makes the poller immune to synthetic
/// modifier key-up/key-down events injected by the text output path,
/// enabling session-scoped modifier management without false releases.
/// </para>
/// </summary>
public sealed class WindowsHotkeyProvider : IGlobalHotkeyService
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 9001;

    // Modifier flags for RegisterHotKey
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    // Virtual key codes used for release-polling
    private const ushort VK_LSHIFT = 0xA0;
    private const ushort VK_RSHIFT = 0xA1;
    private const ushort VK_LCONTROL = 0xA2;
    private const ushort VK_RCONTROL = 0xA3;
    private const ushort VK_LMENU = 0xA4;
    private const ushort VK_RMENU = 0xA5;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG lpMsg);

    /// <summary>High-order bit set = key is currently held.</summary>
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(ushort vKey);

    // ── Low-level keyboard hook ───────────────────────────────────────────────
    // Installed while dictation is active to swallow all keyboard events.
    // Prevents the hotkey chord from auto-repeating into the focused app,
    // and blocks any accidental typing while speaking.
    private const int WH_KEYBOARD_LL = 13;
    private const int HC_ACTION = 0;
    private const uint LLKHF_INJECTED = 0x10;

    private delegate nint LowLevelKeyboardProc(int nCode, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nuint wParam, nint lParam);

    private LowLevelKeyboardProc? _hookProc;   // held as field to prevent GC
    private nint _hook;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint HWnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint DwExtraInfo;
    }

    private Thread? _messageThread;
    private volatile bool _registered;
    private volatile bool _keyDown;
    private bool _disposed;

    // Physical VKs that belong to the active chord. Release only fires once all are up.
    private ushort[] _trackedReleaseVks = Array.Empty<ushort>();

    // ── Hook-based physical release tracking ─────────────────────────────────
    // Tracks which chord keys are still physically held by watching for
    // non-injected WM_KEYUP events in the hook. Synthetic modifier releases
    // (from the text output path) are injected and therefore ignored, making
    // the poller immune to the session-scoped modifier management.
    private readonly HashSet<ushort> _physicallyHeldChordKeys = new();
    private int _physicallyHeldCount;

    /// <inheritdoc/>
    public event EventHandler? HotkeyPressed;

    /// <inheritdoc/>
    public event EventHandler? HotkeyReleased;

    /// <inheritdoc/>
    public void Register(string hotkey)
    {
        if (_registered)
            Unregister();

        ParseHotkey(hotkey, out uint modifiers, out uint vk);
        _trackedReleaseVks = BuildTrackedReleaseVks(modifiers, (ushort)vk);

        _messageThread = new Thread(() => MessageLoop(modifiers, vk))
        {
            IsBackground = true,
            Name = "HushHotkeyThread"
        };
        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();
    }

    private void MessageLoop(uint modifiers, uint vk)
    {
        if (!RegisterHotKey(nint.Zero, HOTKEY_ID, modifiers | MOD_NOREPEAT, vk))
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"RegisterHotKey failed with Win32 error {err}. " +
                "The hotkey may already be in use by another application.");
        }

        _registered = true;

        while (GetMessage(out var msg, nint.Zero, 0, 0))
        {
            if (msg.Message == WM_HOTKEY && (int)msg.WParam == HOTKEY_ID)
            {
                if (!_keyDown)
                {
                    _keyDown = true;

                    // Snapshot which chord keys are physically held right now.
                    // This must happen before the hook is installed and before
                    // any synthetic key events are injected.
                    _physicallyHeldChordKeys.Clear();
                    foreach (var vk2 in _trackedReleaseVks)
                    {
                        if ((GetAsyncKeyState(vk2) & 0x8000) != 0)
                            _physicallyHeldChordKeys.Add(vk2);
                    }
                    Volatile.Write(ref _physicallyHeldCount, _physicallyHeldChordKeys.Count);

                    // Suppress all keyboard input while dictating.
                    // The hook is installed on this thread which runs GetMessage,
                    // so the pump is present and hook callbacks will fire.
                    _hookProc = SuppressAllKeyboardInput;
                    _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, nint.Zero, 0);
                    HotkeyPressed?.Invoke(this, EventArgs.Empty);
                    _ = PollForReleaseAsync();
                }
            }

            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnregisterHotKey(nint.Zero, HOTKEY_ID);
        _registered = false;
    }

    private async Task PollForReleaseAsync()
    {
        int releaseCount = 0;   // consecutive polls where all chord keys are physically released
        while (_keyDown && _registered)
        {
            await Task.Delay(16).ConfigureAwait(false);

            // Check hook-based physical tracking instead of GetAsyncKeyState.
            // This is immune to synthetic modifier release/restore events
            // injected by the text output path during streaming sessions.
            bool anyPhysicallyHeld = Volatile.Read(ref _physicallyHeldCount) > 0;

            if (!anyPhysicallyHeld)
            {
                // Require 2 consecutive polls with every chord key released before firing.
                // This avoids flushing output while Ctrl/Shift are still physically down.
                if (++releaseCount >= 2)
                {
                    _keyDown = false;
                    // Remove the keyboard suppression hook before firing the event
                    // so the target app can receive input normally again.
                    if (_hook != 0)
                    {
                        UnhookWindowsHookEx(_hook);
                        _hook = 0;
                        _hookProc = null;
                    }
                    HotkeyReleased?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }
            else
            {
                releaseCount = 0;   // reset while any chord key remains physically down
            }
        }
    }

    /// <inheritdoc/>
    public void Unregister()
    {
        _registered = false;
        _keyDown = false;
        if (_hook != 0)
        {
            UnhookWindowsHookEx(_hook);
            _hook = 0;
            _hookProc = null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Unregister();
    }

    private static void ParseHotkey(string hotkey, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        foreach (var part in hotkey.Split('+'))
        {
            switch (part.Trim().ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL": modifiers |= MOD_CONTROL; break;
                case "ALT":     modifiers |= MOD_ALT; break;
                case "SHIFT":   modifiers |= MOD_SHIFT; break;
                case "WIN":
                case "WINDOWS": modifiers |= MOD_WIN; break;
                case "SPACE":   vk = 0x20; break;
                default:
                    if (part.Trim().Length == 1)
                        vk = (uint)char.ToUpperInvariant(part.Trim()[0]);
                    break;
            }
        }

        if (vk == 0)
            throw new ArgumentException(
                $"Could not parse virtual key from hotkey string: '{hotkey}'");
    }

    private nint SuppressAllKeyboardInput(int nCode, nuint wParam, nint lParam)
    {
        if (nCode != HC_ACTION || !_keyDown)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        const nuint WM_KEYUP      = 0x0101;
        const nuint WM_SYSKEYDOWN = 0x0104;
        const nuint WM_SYSKEYUP   = 0x0105;

        // Always let system-key events through (Alt+F4, Win+L, Win+D, etc.)
        if (wParam == WM_SYSKEYDOWN || wParam == WM_SYSKEYUP)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        var keyboard = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
        var vkCode = (ushort)keyboard.VkCode;

        // Let Hush's own injected typing events through while the physical keyboard remains blocked.
        if ((keyboard.Flags & LLKHF_INJECTED) != 0
            || keyboard.DwExtraInfo == WindowsInputCoordinator.InjectedExtraInfo)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        // Physical chord key release: update our tracking set and let the
        // event through so the OS key-state table stays consistent.
        if (wParam == WM_KEYUP && Array.IndexOf(_trackedReleaseVks, vkCode) >= 0)
        {
            if (_physicallyHeldChordKeys.Remove(vkCode))
                Interlocked.Decrement(ref _physicallyHeldCount);
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        // Escape cancels the active dictation session and unblocks the keyboard.
        if (vkCode == 0x1B /* VK_ESCAPE */)
        {
            _keyDown = false;
            var h = _hook;
            _hook = 0;
            _hookProc = null;
            if (h != 0) ThreadPool.QueueUserWorkItem(_ => UnhookWindowsHookEx(h));
            HotkeyReleased?.Invoke(this, EventArgs.Empty);
            return 1; // still swallow the Escape keydown itself
        }

        // Block all other keys (printable chars, F-keys, arrows, etc.)
        return 1;
    }

    private static ushort[] BuildTrackedReleaseVks(uint modifiers, ushort mainVk)
    {
        var vks = new List<ushort> { mainVk };
        if ((modifiers & MOD_CONTROL) != 0) { vks.Add(VK_LCONTROL); vks.Add(VK_RCONTROL); }
        if ((modifiers & MOD_SHIFT) != 0)   { vks.Add(VK_LSHIFT); vks.Add(VK_RSHIFT); }
        if ((modifiers & MOD_ALT) != 0)     { vks.Add(VK_LMENU); vks.Add(VK_RMENU); }
        if ((modifiers & MOD_WIN) != 0)     { vks.Add(VK_LWIN); vks.Add(VK_RWIN); }
        return vks.Distinct().ToArray();
    }
}