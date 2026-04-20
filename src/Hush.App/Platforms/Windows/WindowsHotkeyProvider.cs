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
    private const uint WM_QUIT = 0x0012;
    private const int HOTKEY_ID = 9001;
    private const int HOTKEY_ID_CLEAN = 9002;
    private const int ReleasePollIntervalMs = 24;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, nuint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

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

    // ── Clean-side hook ───────────────────────────────────────────────────────
    private LowLevelKeyboardProc? _cleanHookProc;
    private nint _cleanHook;

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
    private uint _messageThreadId;
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

    // ── Clean-side state ──────────────────────────────────────────────────────
    private Thread? _cleanMessageThread;
    private uint _cleanMessageThreadId;
    private volatile bool _cleanRegistered;
    private volatile bool _cleanKeyDown;
    private ushort[] _cleanTrackedReleaseVks = Array.Empty<ushort>();
    private readonly HashSet<ushort> _cleanPhysicallyHeldChordKeys = new();
    private int _cleanPhysicallyHeldCount;

    /// <inheritdoc/>
    public event EventHandler? HotkeyPressed;

    /// <inheritdoc/>
    public event EventHandler? HotkeyReleased;

    /// <inheritdoc/>
    public event EventHandler? CleanHotkeyPressed;

    /// <inheritdoc/>
    public event EventHandler? CleanHotkeyReleased;

    /// <inheritdoc/>
    public void Register(string hotkey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_registered || _messageThread is not null)
            Unregister();

        ParseHotkey(hotkey, out uint modifiers, out uint vk);
        _trackedReleaseVks = BuildTrackedReleaseVks(modifiers, (ushort)vk);

        var registrationReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _messageThread = new Thread(() =>
        {
            if (vk == 0)
                ModifierOnlyMessageLoop(modifiers, isClean: false, registrationReady);
            else
                MessageLoop(modifiers, vk, registrationReady);
        })
        {
            IsBackground = true,
            Name = "HushHotkeyThread"
        };
        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();
        registrationReady.Task.GetAwaiter().GetResult();
    }

    private void MessageLoop(uint modifiers, uint vk, TaskCompletionSource registrationReady)
    {
        _messageThreadId = GetCurrentThreadId();

        try
        {
            if (!RegisterHotKey(nint.Zero, HOTKEY_ID, modifiers | MOD_NOREPEAT, vk))
            {
                int err = Marshal.GetLastWin32Error();
                registrationReady.TrySetException(new InvalidOperationException(
                    $"RegisterHotKey failed with Win32 error {err}. " +
                    "The hotkey may already be in use by another application."));
                return;
            }

            _registered = true;
            registrationReady.TrySetResult();

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
        }
        catch (Exception ex)
        {
            registrationReady.TrySetException(ex);
        }
        finally
        {
            CleanupKeyboardHook();
            _physicallyHeldChordKeys.Clear();
            Volatile.Write(ref _physicallyHeldCount, 0);

            if (_registered)
                UnregisterHotKey(nint.Zero, HOTKEY_ID);

            _registered = false;
            _keyDown = false;
            _messageThreadId = 0;
            _messageThread = null;
        }
    }

    private async Task PollForReleaseAsync()
    {
        int releaseCount = 0;   // consecutive polls where all chord keys are physically released
        while (_keyDown && _registered)
        {
            await Task.Delay(ReleasePollIntervalMs).ConfigureAwait(false);

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
        CleanupKeyboardHook();
        _physicallyHeldChordKeys.Clear();
        Volatile.Write(ref _physicallyHeldCount, 0);

        var thread = _messageThread;
        var threadId = _messageThreadId;
        if (threadId != 0)
            PostThreadMessage(threadId, WM_QUIT, 0, 0);

        if (thread is not null && thread != Thread.CurrentThread)
            thread.Join(TimeSpan.FromSeconds(2));
    }

    /// <inheritdoc/>
    public void RegisterClean(string hotkey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_cleanRegistered || _cleanMessageThread is not null)
            UnregisterClean();

        ParseHotkey(hotkey, out uint modifiers, out uint vk);
        _cleanTrackedReleaseVks = BuildTrackedReleaseVks(modifiers, (ushort)vk);

        var registrationReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cleanMessageThread = new Thread(() =>
        {
            if (vk == 0)
                ModifierOnlyMessageLoop(modifiers, isClean: true, registrationReady);
            else
                CleanMessageLoop(modifiers, vk, registrationReady);
        })
        {
            IsBackground = true,
            Name = "HushCleanHotkeyThread"
        };
        _cleanMessageThread.SetApartmentState(ApartmentState.STA);
        _cleanMessageThread.Start();
        registrationReady.Task.GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public void UnregisterClean()
    {
        _cleanRegistered = false;
        _cleanKeyDown = false;
        CleanupCleanKeyboardHook();
        _cleanPhysicallyHeldChordKeys.Clear();
        Volatile.Write(ref _cleanPhysicallyHeldCount, 0);

        var thread = _cleanMessageThread;
        var threadId = _cleanMessageThreadId;
        if (threadId != 0)
            PostThreadMessage(threadId, WM_QUIT, 0, 0);

        if (thread is not null && thread != Thread.CurrentThread)
            thread.Join(TimeSpan.FromSeconds(2));
    }

    private void CleanMessageLoop(uint modifiers, uint vk, TaskCompletionSource registrationReady)
    {
        _cleanMessageThreadId = GetCurrentThreadId();

        try
        {
            if (!RegisterHotKey(nint.Zero, HOTKEY_ID_CLEAN, modifiers | MOD_NOREPEAT, vk))
            {
                int err = Marshal.GetLastWin32Error();
                registrationReady.TrySetException(new InvalidOperationException(
                    $"RegisterHotKey (clean) failed with Win32 error {err}. " +
                    "The hotkey may already be in use by another application."));
                return;
            }

            _cleanRegistered = true;
            registrationReady.TrySetResult();

            while (GetMessage(out var msg, nint.Zero, 0, 0))
            {
                if (msg.Message == WM_HOTKEY && (int)msg.WParam == HOTKEY_ID_CLEAN)
                {
                    if (!_cleanKeyDown)
                    {
                        _cleanKeyDown = true;

                        _cleanPhysicallyHeldChordKeys.Clear();
                        foreach (var vk2 in _cleanTrackedReleaseVks)
                        {
                            if ((GetAsyncKeyState(vk2) & 0x8000) != 0)
                                _cleanPhysicallyHeldChordKeys.Add(vk2);
                        }
                        Volatile.Write(ref _cleanPhysicallyHeldCount, _cleanPhysicallyHeldChordKeys.Count);

                        _cleanHookProc = SuppressAllKeyboardInputClean;
                        _cleanHook = SetWindowsHookEx(WH_KEYBOARD_LL, _cleanHookProc, nint.Zero, 0);
                        CleanHotkeyPressed?.Invoke(this, EventArgs.Empty);
                        _ = PollForCleanReleaseAsync();
                    }
                }

                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            registrationReady.TrySetException(ex);
        }
        finally
        {
            CleanupCleanKeyboardHook();
            _cleanPhysicallyHeldChordKeys.Clear();
            Volatile.Write(ref _cleanPhysicallyHeldCount, 0);

            if (_cleanRegistered)
                UnregisterHotKey(nint.Zero, HOTKEY_ID_CLEAN);

            _cleanRegistered = false;
            _cleanKeyDown = false;
            _cleanMessageThreadId = 0;
            _cleanMessageThread = null;
        }
    }

    private async Task PollForCleanReleaseAsync()
    {
        int releaseCount = 0;
        while (_cleanKeyDown && _cleanRegistered)
        {
            await Task.Delay(ReleasePollIntervalMs).ConfigureAwait(false);

            bool anyPhysicallyHeld = Volatile.Read(ref _cleanPhysicallyHeldCount) > 0;

            if (!anyPhysicallyHeld)
            {
                if (++releaseCount >= 2)
                {
                    _cleanKeyDown = false;
                    if (_cleanHook != 0)
                    {
                        UnhookWindowsHookEx(_cleanHook);
                        _cleanHook = 0;
                        _cleanHookProc = null;
                    }
                    CleanHotkeyReleased?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }
            else
            {
                releaseCount = 0;
            }
        }
    }

    private nint SuppressAllKeyboardInputClean(int nCode, nuint wParam, nint lParam)
    {
        if (nCode != HC_ACTION || !_cleanKeyDown)
            return CallNextHookEx(_cleanHook, nCode, wParam, lParam);

        const nuint WM_KEYUP      = 0x0101;
        const nuint WM_SYSKEYDOWN = 0x0104;
        const nuint WM_SYSKEYUP   = 0x0105;

        if (wParam == WM_SYSKEYDOWN || wParam == WM_SYSKEYUP)
            return CallNextHookEx(_cleanHook, nCode, wParam, lParam);

        var keyboard = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
        var vkCode = (ushort)keyboard.VkCode;

        if ((keyboard.Flags & LLKHF_INJECTED) != 0
            || keyboard.DwExtraInfo == WindowsInputCoordinator.InjectedExtraInfo)
            return CallNextHookEx(_cleanHook, nCode, wParam, lParam);

        if (wParam == WM_KEYUP && Array.IndexOf(_cleanTrackedReleaseVks, vkCode) >= 0)
        {
            if (_cleanPhysicallyHeldChordKeys.Remove(vkCode))
                Interlocked.Decrement(ref _cleanPhysicallyHeldCount);
            return CallNextHookEx(_cleanHook, nCode, wParam, lParam);
        }

        if (vkCode == 0x1B /* VK_ESCAPE */)
        {
            _cleanKeyDown = false;
            var h = _cleanHook;
            _cleanHook = 0;
            _cleanHookProc = null;
            if (h != 0) ThreadPool.QueueUserWorkItem(_ => UnhookWindowsHookEx(h));
            CleanHotkeyReleased?.Invoke(this, EventArgs.Empty);
            return 1;
        }

        return 1;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Unregister();
        UnregisterClean();
    }

    private void CleanupKeyboardHook()
    {
        if (_hook != 0)
        {
            UnhookWindowsHookEx(_hook);
            _hook = 0;
            _hookProc = null;
        }
    }

    private void CleanupCleanKeyboardHook()
    {
        if (_cleanHook != 0)
        {
            UnhookWindowsHookEx(_cleanHook);
            _cleanHook = 0;
            _cleanHookProc = null;
        }
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

        if (vk == 0 && modifiers == 0)
            throw new ArgumentException(
                $"Could not parse virtual key from hotkey string: '{hotkey}'");
    }

    // ── Modifier-only hotkey support ──────────────────────────────────────────
    //
    // RegisterHotKey cannot register a chord with only modifier keys (e.g. plain
    // "Alt" or "Ctrl+Shift"), so modifier-only push-to-talk is implemented via
    // an always-on low-level keyboard hook that watches for the target modifier
    // to be held in isolation.
    //
    // A hold-debounce (~250 ms) distinguishes a dictation gesture from the
    // user's ordinary modifier usage: Alt+Tab, Alt+F4, menu access, etc. are
    // all pressed-and-released in well under 250 ms, so those interactions
    // remain unaffected. If any non-modifier key is pressed during the
    // debounce window, the candidate is cancelled outright.
    private const int ModifierHoldDebounceMs = 250;

    private long _modifierCandidateDownTicks;          // Environment.TickCount64 at press, 0 = no candidate.
    private long _cleanModifierCandidateDownTicks;

    /// <summary>Expands a modifier mask into the set of left/right VKs that satisfy it.</summary>
    private static ushort[] ExpandModifierVks(uint modifiers)
    {
        var list = new List<ushort>(8);
        if ((modifiers & MOD_ALT) != 0)     { list.Add(VK_LMENU); list.Add(VK_RMENU); }
        if ((modifiers & MOD_CONTROL) != 0) { list.Add(VK_LCONTROL); list.Add(VK_RCONTROL); }
        if ((modifiers & MOD_SHIFT) != 0)   { list.Add(VK_LSHIFT); list.Add(VK_RSHIFT); }
        if ((modifiers & MOD_WIN) != 0)     { list.Add(VK_LWIN); list.Add(VK_RWIN); }
        return list.ToArray();
    }

    /// <summary>
    /// Returns <see langword="true"/> when at least one VK from each required
    /// modifier group is physically held.
    /// </summary>
    private static bool AllRequiredModifiersHeld(uint modifiers)
    {
        bool AnyHeld(ushort a, ushort b) =>
            (GetAsyncKeyState(a) & 0x8000) != 0 || (GetAsyncKeyState(b) & 0x8000) != 0;

        if ((modifiers & MOD_ALT) != 0     && !AnyHeld(VK_LMENU, VK_RMENU)) return false;
        if ((modifiers & MOD_CONTROL) != 0 && !AnyHeld(VK_LCONTROL, VK_RCONTROL)) return false;
        if ((modifiers & MOD_SHIFT) != 0   && !AnyHeld(VK_LSHIFT, VK_RSHIFT)) return false;
        if ((modifiers & MOD_WIN) != 0     && !AnyHeld(VK_LWIN, VK_RWIN)) return false;
        return true;
    }

    /// <summary>
    /// Returns <see langword="true"/> if any target modifier VK — other than
    /// <paramref name="releasingVk"/> — is still physically held. Used by the
    /// modifier-only hook path where <paramref name="releasingVk"/> is the
    /// key currently going up. This bypasses the GetAsyncKeyState race that
    /// can briefly still report the releasing key as held.
    /// </summary>
    private static bool AnyOtherTargetModifierHeld(ushort[] targetVks, ushort releasingVk)
    {
        for (int i = 0; i < targetVks.Length; i++)
        {
            ushort vk = targetVks[i];
            if (vk == releasingVk) continue;
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        }
        return false;
    }

    private void ModifierOnlyMessageLoop(uint modifiers, bool isClean, TaskCompletionSource ready)
    {
        var targetModifierVks = ExpandModifierVks(modifiers);

        if (isClean)
            _cleanMessageThreadId = GetCurrentThreadId();
        else
            _messageThreadId = GetCurrentThreadId();

        try
        {
            // Install the always-on hook. It both detects the gesture and
            // suppresses stray keys while dictation is active.
            LowLevelKeyboardProc proc = (nCode, wParam, lParam) =>
                ModifierOnlyHookProc(nCode, wParam, lParam, modifiers, targetModifierVks, isClean);

            nint hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, nint.Zero, 0);
            if (hook == 0)
            {
                int err = Marshal.GetLastWin32Error();
                ready.TrySetException(new InvalidOperationException(
                    $"Failed to install keyboard hook for modifier-only hotkey (error {err})."));
                return;
            }

            if (isClean)
            {
                _cleanHookProc = proc;
                _cleanHook = hook;
                _cleanRegistered = true;
            }
            else
            {
                _hookProc = proc;
                _hook = hook;
                _registered = true;
            }
            ready.TrySetResult();

            while (GetMessage(out var msg, nint.Zero, 0, 0))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
        }
        finally
        {
            if (isClean)
            {
                CleanupCleanKeyboardHook();
                _cleanRegistered = false;
                _cleanKeyDown = false;
                _cleanModifierCandidateDownTicks = 0;
                _cleanMessageThreadId = 0;
                _cleanMessageThread = null;
            }
            else
            {
                CleanupKeyboardHook();
                _registered = false;
                _keyDown = false;
                _modifierCandidateDownTicks = 0;
                _messageThreadId = 0;
                _messageThread = null;
            }
        }
    }

    private nint ModifierOnlyHookProc(int nCode, nuint wParam, nint lParam, uint modifiers, ushort[] targetVks, bool isClean)
    {
        nint hookHandle = isClean ? _cleanHook : _hook;
        if (nCode != HC_ACTION)
            return CallNextHookEx(hookHandle, nCode, wParam, lParam);

        const nuint WM_KEYDOWN    = 0x0100;
        const nuint WM_KEYUP      = 0x0101;
        const nuint WM_SYSKEYDOWN = 0x0104;
        const nuint WM_SYSKEYUP   = 0x0105;

        var keyboard = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
        var vkCode   = (ushort)keyboard.VkCode;
        bool isDown  = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
        bool isUp    = wParam == WM_KEYUP   || wParam == WM_SYSKEYUP;

        // Ignore our own injected events — typed output must pass through.
        if ((keyboard.Flags & LLKHF_INJECTED) != 0
            || keyboard.DwExtraInfo == WindowsInputCoordinator.InjectedExtraInfo)
            return CallNextHookEx(hookHandle, nCode, wParam, lParam);

        bool active = isClean ? _cleanKeyDown : _keyDown;
        bool isTargetModifier = Array.IndexOf(targetVks, vkCode) >= 0;

        if (active)
        {
            // ── Dictation is live: act like the keyed-hotkey suppression hook.

            // Any required modifier released → end the gesture.
            // NOTE: We cannot rely on AllRequiredModifiersHeld(modifiers) here
            // because GetAsyncKeyState races with the keyup event — it often
            // still reports the key as held while the hook is running,
            // causing the Released event to silently get dropped and the
            // next HotkeyPressed to collide with a still-live session.
            // Treat the specific vkCode going up as released and evaluate
            // the remaining target modifiers directly.
            if (isUp && isTargetModifier && !AnyOtherTargetModifierHeld(targetVks, vkCode))
            {
                if (isClean) _cleanKeyDown = false;
                else         _keyDown = false;
                if (isClean) CleanHotkeyReleased?.Invoke(this, EventArgs.Empty);
                else         HotkeyReleased?.Invoke(this, EventArgs.Empty);
                return CallNextHookEx(hookHandle, nCode, wParam, lParam);
            }

            // Pass modifier events through so the OS key-state table stays
            // consistent (output path injects its own temporary release).
            if (isTargetModifier)
                return CallNextHookEx(hookHandle, nCode, wParam, lParam);

            // Escape cancels the session.
            if (isDown && vkCode == 0x1B)
            {
                if (isClean) _cleanKeyDown = false;
                else         _keyDown = false;
                if (isClean) CleanHotkeyReleased?.Invoke(this, EventArgs.Empty);
                else         HotkeyReleased?.Invoke(this, EventArgs.Empty);
                return 1;
            }

            // Swallow everything else so stray keys do not land in the
            // focused app while the user is speaking.
            return 1;
        }

        // ── Not active: watch for the gesture ────────────────────────────────

        if (isDown)
        {
            if (isTargetModifier)
            {
                long now = Environment.TickCount64;
                long existing = isClean ? _cleanModifierCandidateDownTicks : _modifierCandidateDownTicks;
                if (existing == 0)
                {
                    if (isClean) _cleanModifierCandidateDownTicks = now;
                    else         _modifierCandidateDownTicks = now;
                }
                else if (now - existing >= ModifierHoldDebounceMs && AllRequiredModifiersHeld(modifiers))
                {
                    // Auto-repeat fired this event; debounce window has
                    // elapsed with the modifier still held and no stray key
                    // pressed → promote to active.
                    PromoteModifierCandidateToActive(isClean);
                }
                return CallNextHookEx(hookHandle, nCode, wParam, lParam);
            }

            // Non-modifier key pressed during candidate window → ordinary
            // shortcut (Alt+Tab etc.). Cancel and let it through.
            if (isClean) _cleanModifierCandidateDownTicks = 0;
            else         _modifierCandidateDownTicks = 0;
            return CallNextHookEx(hookHandle, nCode, wParam, lParam);
        }

        if (isUp && isTargetModifier)
        {
            long existing = isClean ? _cleanModifierCandidateDownTicks : _modifierCandidateDownTicks;
            if (existing != 0)
            {
                long held = Environment.TickCount64 - existing;
                if (isClean) _cleanModifierCandidateDownTicks = 0;
                else         _modifierCandidateDownTicks = 0;

                // Long hold without auto-repeat promotion (some keyboards
                // produce sparse repeats): promote on release if the
                // debounce threshold was reached.
                if (held >= ModifierHoldDebounceMs)
                {
                    PromoteModifierCandidateToActive(isClean);
                    if (isClean) _cleanKeyDown = false;
                    else         _keyDown = false;
                    if (isClean) CleanHotkeyReleased?.Invoke(this, EventArgs.Empty);
                    else         HotkeyReleased?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        return CallNextHookEx(hookHandle, nCode, wParam, lParam);
    }

    private void PromoteModifierCandidateToActive(bool isClean)
    {
        if (isClean)
        {
            if (_cleanKeyDown) return;
            _cleanKeyDown = true;
            CleanHotkeyPressed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            if (_keyDown) return;
            _keyDown = true;
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
        }
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