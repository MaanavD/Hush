// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hush.Core.Output;

/// <summary>
/// Outputs transcribed text into the currently focused application.
/// On Windows, text is injected via <c>SendInput</c> with <c>KEYEVENTF_UNICODE</c>,
/// which does not touch the clipboard. A clipboard-paste fallback is available
/// for apps that don't support Unicode input events (set <c>ClipboardFallback = true</c>).
/// </summary>
public sealed class KeystrokeTypingService : ITextOutputService
{
    private readonly ILogger<KeystrokeTypingService> _logger;
    private readonly bool _useClipboardFallback;

    public KeystrokeTypingService(ILogger<KeystrokeTypingService>? logger = null, bool useClipboardFallback = false)
    {
        _logger = logger ?? NullLogger<KeystrokeTypingService>.Instance;
        _useClipboardFallback = useClipboardFallback;
    }

    /// <inheritdoc/>
    public Task TypeTextAsync(string text, CancellationToken cancellationToken = default, bool skipModifierRestore = false)
    {
        if (string.IsNullOrEmpty(text))
            return Task.CompletedTask;

        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        if (OperatingSystem.IsWindows())
        {
            return _useClipboardFallback
                ? WindowsClipboardTyper.TypeAsync(text, cancellationToken)
                : WindowsKeystrokeTyper.TypeAsync(text, cancellationToken, skipModifierRestore);
        }

        if (OperatingSystem.IsMacOS())
            return MacKeystrokeTyper.TypeAsync(text, cancellationToken);

        if (OperatingSystem.IsLinux())
            return LinuxKeystrokeTyper.TypeAsync(text, cancellationToken);

        throw new PlatformNotSupportedException(
            $"Text output is not supported on this platform ({RuntimeInformation.OSDescription}).");
    }

    /// <inheritdoc/>
    public Task SendBackspacesAsync(int count, CancellationToken cancellationToken = default, bool skipModifierRestore = false)
    {
        if (count <= 0)
            return Task.CompletedTask;

        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        if (OperatingSystem.IsWindows())
            return WindowsKeystrokeTyper.SendBackspacesAsync(count, cancellationToken, skipModifierRestore);

        if (OperatingSystem.IsMacOS())
            return MacKeystrokeTyper.SendBackspacesAsync(count, cancellationToken);

        if (OperatingSystem.IsLinux())
            return LinuxKeystrokeTyper.SendBackspacesAsync(count, cancellationToken);

        throw new PlatformNotSupportedException(
            $"Text output is not supported on this platform ({RuntimeInformation.OSDescription}).");
    }

}

// ────────────────────────────────────────────────────────────────────────────
// Windows — KEYEVENTF_UNICODE via SendInput (primary, clipboard-free)
//
// Each character is injected as a WM_CHAR-equivalent keyboard event using
// KEYEVENTF_UNICODE. This never touches the clipboard. Held modifiers are
// temporarily released to prevent the target app from interpreting the input
// as a keyboard shortcut (e.g. Ctrl+character → control sequence).
//
// If SendInput returns 0 for all characters (e.g. UIPI blocks injection to
// an elevated process), an InvalidOperationException is thrown so the caller
// can surface a user-visible message.
// ────────────────────────────────────────────────────────────────────────────

internal static class WindowsKeystrokeTyper
{
    private const uint INPUT_KEYBOARD     = 1;
    private const uint KEYEVENTF_KEYUP    = 0x0002;
    private const uint KEYEVENTF_UNICODE  = 0x0004;

    private const ushort VK_LSHIFT     = 0xA0;
    private const ushort VK_RSHIFT     = 0xA1;
    private const ushort VK_LCONTROL   = 0xA2;
    private const ushort VK_RCONTROL   = 0xA3;
    private const ushort VK_LMENU      = 0xA4;
    private const ushort VK_RMENU      = 0xA5;
    private const ushort VK_LWIN       = 0x5B;
    private const ushort VK_RWIN       = 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public INPUTUNION Union;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint   Flags;
        public uint   Time;
        public nint   ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(ushort vKey);

    private const ushort VK_BACK = 0x08;

    internal static Task TypeAsync(string text, CancellationToken cancellationToken, bool skipModifierRestore = false)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pressedModifiers = GetPressedModifierVks();

            // Build a single SendInput batch: modifier releases + text + optional modifier restores.
            // This prevents the OS from reasserting physical modifier keys between release and typing.
            int modCount = pressedModifiers.Count;
            int restoreCount = skipModifierRestore ? 0 : modCount;
            var inputs = new INPUT[modCount + text.Length * 2 + restoreCount];
            int idx = 0;

            // Release held modifiers
            for (int i = 0; i < modCount; i++)
                inputs[idx++] = MakeVkInput(pressedModifiers[i], KEYEVENTF_KEYUP);

            // Type each character (down + up)
            for (int i = 0; i < text.Length; i++)
            {
                ushort ch = text[i];
                inputs[idx++] = MakeUnicodeInput(ch, KEYEVENTF_UNICODE);
                inputs[idx++] = MakeUnicodeInput(ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
            }

            // Optionally restore modifiers
            if (!skipModifierRestore)
            {
                var toRestore = GetModifiersToRestore(pressedModifiers);
                // Resize if fewer modifiers need restoring
                if (toRestore.Count < modCount)
                    Array.Resize(ref inputs, modCount + text.Length * 2 + toRestore.Count);
                for (int i = 0; i < toRestore.Count; i++)
                    inputs[idx++] = MakeVkInput(toRestore[i], 0);
            }

            uint sent = SendInput((uint)idx, inputs, Marshal.SizeOf<INPUT>());
            if (sent == 0 && text.Length > 0)
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    error == 5
                        ? "Cannot type into an elevated (administrator) application. " +
                          "Run Hush as administrator, or switch to a non-elevated window."
                        : $"SendInput failed (sent 0/{idx}, Win32 error {error}). " +
                          "The focused application may not accept simulated input.");
            }
        }, cancellationToken);
    }

    internal static Task SendBackspacesAsync(int count, CancellationToken cancellationToken, bool skipModifierRestore = false)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pressedModifiers = GetPressedModifierVks();

            // Single atomic SendInput batch: modifier releases + backspaces + optional restores.
            int modCount = pressedModifiers.Count;
            int restoreCount = skipModifierRestore ? 0 : modCount;
            var inputs = new INPUT[modCount + count * 2 + restoreCount];
            int idx = 0;

            for (int i = 0; i < modCount; i++)
                inputs[idx++] = MakeVkInput(pressedModifiers[i], KEYEVENTF_KEYUP);

            for (int i = 0; i < count; i++)
            {
                inputs[idx++] = MakeVkInput(VK_BACK, 0);
                inputs[idx++] = MakeVkInput(VK_BACK, KEYEVENTF_KEYUP);
            }

            if (!skipModifierRestore)
            {
                var toRestore = GetModifiersToRestore(pressedModifiers);
                if (toRestore.Count < modCount)
                    Array.Resize(ref inputs, modCount + count * 2 + toRestore.Count);
                for (int i = 0; i < toRestore.Count; i++)
                    inputs[idx++] = MakeVkInput(toRestore[i], 0);
            }

            SendInput((uint)idx, inputs, Marshal.SizeOf<INPUT>());
        }, cancellationToken);
    }

    internal static IReadOnlyList<ushort> GetModifiersToRestore(
        IReadOnlyList<ushort> releasedModifiers,
        IReadOnlyCollection<ushort>? physicallyPressedModifiers = null)
    {
        if (releasedModifiers.Count == 0)
            return Array.Empty<ushort>();

        physicallyPressedModifiers ??= GetPressedModifierVks();
        var physicallyPressedSet = new HashSet<ushort>(physicallyPressedModifiers);

        var modifiersToRestore = new List<ushort>(releasedModifiers.Count);
        foreach (ushort modifier in releasedModifiers)
        {
            if (physicallyPressedSet.Contains(modifier))
                modifiersToRestore.Add(modifier);
        }

        return modifiersToRestore;
    }

    private static void SendUnicodeString(string text)
    {
        // Build all key events at once for efficiency (down+up per char).
        // Surrogate pairs are handled by sending each surrogate code unit separately —
        // Windows merges them into a single WM_CHAR with the full codepoint.
        var inputs = new INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            ushort ch = text[i];
            inputs[i * 2]     = MakeUnicodeInput(ch, KEYEVENTF_UNICODE);
            inputs[i * 2 + 1] = MakeUnicodeInput(ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
        }

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == 0)
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                error == 5 // ERROR_ACCESS_DENIED — UIPI blocks injection to elevated windows
                    ? "Cannot type into an elevated (administrator) application. " +
                      "Run Hush as administrator, or switch to a non-elevated window."
                    : $"SendInput failed (sent 0/{inputs.Length}, Win32 error {error}). " +
                      "The focused application may not accept simulated input.");
        }
    }

    private static void SendModifierKeys(IReadOnlyList<ushort> virtualKeys, uint flags)
    {
        var inputs = new INPUT[virtualKeys.Count];
        for (int i = 0; i < virtualKeys.Count; i++)
            inputs[i] = MakeVkInput(virtualKeys[i], flags);

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static List<ushort> GetPressedModifierVks()
    {
        ushort[] candidates =
        [
            VK_LCONTROL, VK_RCONTROL,
            VK_LSHIFT, VK_RSHIFT,
            VK_LMENU, VK_RMENU,
            VK_LWIN, VK_RWIN,
        ];

        var pressed = new List<ushort>(4);
        foreach (ushort vk in candidates)
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                pressed.Add(vk);
        }
        return pressed;
    }

    private static INPUT MakeUnicodeInput(ushort scanCode, uint flags) => new()
    {
        Type = INPUT_KEYBOARD,
        Union = new INPUTUNION
        {
            Keyboard = new KEYBDINPUT
            {
                VirtualKey = 0,
                ScanCode = scanCode,
                Flags = flags,
                ExtraInfo = (nint)WindowsInputCoordinator.InjectedExtraInfo,
            }
        }
    };

    private static INPUT MakeVkInput(ushort vk, uint flags) => new()
    {
        Type = INPUT_KEYBOARD,
        Union = new INPUTUNION
        {
            Keyboard = new KEYBDINPUT
            {
                VirtualKey = vk,
                ScanCode = 0,
                Flags = flags,
                ExtraInfo = (nint)WindowsInputCoordinator.InjectedExtraInfo,
            }
        }
    };
}


// ────────────────────────────────────────────────────────────────────────────
// Windows — Clipboard paste fallback (opt-in via ClipboardFallback setting)
//
// Some legacy apps don't handle KEYEVENTF_UNICODE. This path saves/restores
// the clipboard around a Ctrl+V paste. Must run on an STA thread for OLE
// clipboard access.
// ────────────────────────────────────────────────────────────────────────────

internal static class WindowsClipboardTyper
{
    private const uint INPUT_KEYBOARD  = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL    = 0x11;
    private const ushort VK_V          = 0x56;
    private const uint CF_UNICODETEXT  = 13;
    private const uint GMEM_MOVEABLE   = 0x0002;
    private const ushort VK_LSHIFT     = 0xA0;
    private const ushort VK_RSHIFT     = 0xA1;
    private const ushort VK_LCONTROL   = 0xA2;
    private const ushort VK_RCONTROL   = 0xA3;
    private const ushort VK_LMENU      = 0xA4;
    private const ushort VK_RMENU      = 0xA5;
    private const ushort VK_LWIN       = 0x5B;
    private const ushort VK_RWIN       = 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public INPUTUNION Union;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint   Flags;
        public uint   Time;
        public nint   ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(ushort vKey);

    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint hWnd);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint uFormat, nint hMem);
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint hMem);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint hMem);

    internal static Task TypeAsync(string text, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var t = new Thread(() =>
        {
            List<ushort>? pressedModifiers = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                pressedModifiers = GetPressedModifierVks();
                if (pressedModifiers.Count > 0)
                    SendKeys(pressedModifiers, KEYEVENTF_KEYUP);

                string? savedText = TryGetClipboardText();
                TrySetClipboardText(text);
                SendCtrlV();

                Thread.Sleep(30);
                TrySetClipboardText(savedText ?? string.Empty);

                if (pressedModifiers.Count > 0)
                {
                    var modifiersToRestore = WindowsKeystrokeTyper.GetModifiersToRestore(pressedModifiers);
                    if (modifiersToRestore.Count > 0)
                        SendKeys(modifiersToRestore, 0);
                }

                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                try
                {
                    if (pressedModifiers is { Count: > 0 })
                    {
                        var modifiersToRestore = WindowsKeystrokeTyper.GetModifiersToRestore(pressedModifiers);
                        if (modifiersToRestore.Count > 0)
                            SendKeys(modifiersToRestore, 0);
                    }
                }
                catch
                {
                    // Best effort only; preserve the original typing exception.
                }

                tcs.TrySetException(ex);
            }
        });

        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Name = "Hush.ClipboardPaste";
        t.Start();
        return tcs.Task;
    }

    private static string? TryGetClipboardText()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(0))
            {
                try
                {
                    if (!IsClipboardFormatAvailable(CF_UNICODETEXT))
                        return null;
                    var h = GetClipboardData(CF_UNICODETEXT);
                    if (h == 0) return null;
                    var ptr = GlobalLock(h);
                    if (ptr == 0) return null;
                    var s = Marshal.PtrToStringUni(ptr);
                    GlobalUnlock(h);
                    return s;
                }
                finally { CloseClipboard(); }
            }
            Thread.Sleep(20);
        }
        return null;
    }

    private static void TrySetClipboardText(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(0))
            {
                try
                {
                    EmptyClipboard();
                    int bytes = (text.Length + 1) * 2;
                    var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes);
                    if (hGlobal == 0) return;
                    var ptr = GlobalLock(hGlobal);
                    if (ptr == 0) { GlobalFree(hGlobal); return; }
                    Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                    Marshal.WriteInt16(ptr + text.Length * 2, 0);
                    GlobalUnlock(hGlobal);
                    SetClipboardData(CF_UNICODETEXT, hGlobal);
                    return;
                }
                finally { CloseClipboard(); }
            }
            Thread.Sleep(20);
        }
    }

    private static void SendCtrlV()
    {
        INPUT[] inputs =
        [
            MakeKey(VK_CONTROL, 0),
            MakeKey(VK_V, 0),
            MakeKey(VK_V, KEYEVENTF_KEYUP),
            MakeKey(VK_CONTROL, KEYEVENTF_KEYUP),
        ];

        SendInputs(inputs);
    }

    private static void SendKeys(IReadOnlyList<ushort> virtualKeys, uint flags)
    {
        var inputs = new INPUT[virtualKeys.Count];
        for (int i = 0; i < virtualKeys.Count; i++)
            inputs[i] = MakeKey(virtualKeys[i], flags);

        SendInputs(inputs);
    }

    private static void SendInputs(INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"SendInput failed while typing dictated text (sent {sent}/{inputs.Length}, Win32={error}).");
        }
    }

    private static List<ushort> GetPressedModifierVks()
    {
        ushort[] candidates =
        [
            VK_LCONTROL, VK_RCONTROL,
            VK_LSHIFT, VK_RSHIFT,
            VK_LMENU, VK_RMENU,
            VK_LWIN, VK_RWIN,
        ];

        var pressed = new List<ushort>(4);
        foreach (ushort candidate in candidates)
        {
            if ((GetAsyncKeyState(candidate) & 0x8000) != 0)
                pressed.Add(candidate);
        }

        return pressed;
    }

    private static INPUT MakeKey(ushort vk, uint flags) => new()
    {
        Type = INPUT_KEYBOARD,
        Union = new INPUTUNION
        {
            Keyboard = new KEYBDINPUT
            {
                VirtualKey = vk,
                ScanCode = 0,
                Flags = flags,
                ExtraInfo = (nint)WindowsInputCoordinator.InjectedExtraInfo,
            }
        }
    };
}


// ────────────────────────────────────────────────────────────────────────────
// macOS — CGEventCreateKeyboardEvent + CGEventKeyboardSetUnicodeString
// ────────────────────────────────────────────────────────────────────────────

internal static class MacKeystrokeTyper
{
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // kCGHIDEventTap = 0; posting here delivers events to the active application.
    private const uint kCGHIDEventTap = 0;

    [DllImport(CG)]
    private static extern nint CGEventCreateKeyboardEvent(nint source, ushort virtualKey, bool keyDown);

    [DllImport(CG)]
    private static extern void CGEventKeyboardSetUnicodeString(
        nint @event, nuint stringLength,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] unicodeString);

    [DllImport(CG)] private static extern void CGEventPost(uint tap, nint @event);
    [DllImport(CF)] private static extern void CFRelease(nint cf);

    // macOS virtual key code for Delete (Backspace).
    private const ushort kVK_Delete = 0x33;

    internal static Task TypeAsync(string text, CancellationToken cancellationToken)
    {
        // Each Unicode code unit is sent as a key-down + key-up event with no
        // virtual key code (vk=0) — CGEventKeyboardSetUnicodeString bypasses
        // keycode/keysym lookup entirely, matching Windows' KEYEVENTF_UNICODE.
        foreach (char ch in text)
        {
            cancellationToken.ThrowIfCancellationRequested();

            char[] arr = [ch];

            nint down = CGEventCreateKeyboardEvent(0, 0, true);
            CGEventKeyboardSetUnicodeString(down, 1, arr);
            CGEventPost(kCGHIDEventTap, down);
            CFRelease(down);

            nint up = CGEventCreateKeyboardEvent(0, 0, false);
            CGEventKeyboardSetUnicodeString(up, 1, arr);
            CGEventPost(kCGHIDEventTap, up);
            CFRelease(up);
        }

        return Task.CompletedTask;
    }

    internal static Task SendBackspacesAsync(int count, CancellationToken cancellationToken)
    {
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            nint down = CGEventCreateKeyboardEvent(0, kVK_Delete, true);
            CGEventPost(kCGHIDEventTap, down);
            CFRelease(down);

            nint up = CGEventCreateKeyboardEvent(0, kVK_Delete, false);
            CGEventPost(kCGHIDEventTap, up);
            CFRelease(up);
        }

        return Task.CompletedTask;
    }
}

// ────────────────────────────────────────────────────────────────────────────
// Linux — XTest + temporary keysym mapping for full Unicode support
// ────────────────────────────────────────────────────────────────────────────

internal static class LinuxKeystrokeTyper
{
    private const string X11 = "libX11";
    private const string Xtst = "libXtst";

    [DllImport(X11)] private static extern nint XOpenDisplay(string? displayName);
    [DllImport(X11)] private static extern int XCloseDisplay(nint display);
    [DllImport(X11)] private static extern int XSync(nint display, bool discard);
    [DllImport(X11)] private static extern int XFlush(nint display);
    [DllImport(X11)] private static extern int XDisplayKeycodes(nint display, out int minKeycode, out int maxKeycode);
    [DllImport(X11)] private static extern int XChangeKeyboardMapping(
        nint display, int firstKeycode, int keysymsPerKeycode, ulong[] keysyms, int numCodes);
    [DllImport(X11)] private static extern void XFree(nint data);

    [DllImport(Xtst)] private static extern int XTestFakeKeyEvent(
        nint display, uint keycode, bool isPress, ulong delay);

    // X11 keysym for BackSpace.
    private const ulong XK_BackSpace = 0xFF08;

    internal static Task TypeAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text)) return Task.CompletedTask;

        nint display = XOpenDisplay(null);
        if (display == 0)
            throw new InvalidOperationException(
                "Cannot connect to X11 display. Ensure the DISPLAY environment variable is set. " +
                "Wayland-only sessions are not supported in this version.");

        try
        {
            XDisplayKeycodes(display, out _, out int maxKeycode);

            // Use the highest available keycode as a scratch slot for temporary Unicode mapping.
            // High keycodes are typically unused on modern keyboards.
            int scratch = maxKeycode;

            foreach (char ch in text)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // X11 Unicode keysym convention: U+XXXX → 0x01000000 | codepoint.
                // ASCII chars also have direct keysym identity so we handle both with one formula.
                ulong keysym = 0x01000000UL | (ulong)ch;

                // Map the scratch keycode to our Unicode keysym (no-shift and shift both the same).
                ulong[] mapping = [keysym, keysym];
                XChangeKeyboardMapping(display, scratch, 2, mapping, 1);
                // Sync ensures the X server processes the mapping change before we send the event.
                XSync(display, false);

                XTestFakeKeyEvent(display, (uint)scratch, true, 0);
                XTestFakeKeyEvent(display, (uint)scratch, false, 0);
                XFlush(display);
            }

            // Give the X server a moment to process the final event, then restore the scratch
            // keycode to a neutral state (no keysym).
            XSync(display, false);
            ulong[] clear = [0UL, 0UL];
            XChangeKeyboardMapping(display, scratch, 2, clear, 1);
            XSync(display, false);
        }
        finally
        {
            XCloseDisplay(display);
        }

        return Task.CompletedTask;
    }

    internal static Task SendBackspacesAsync(int count, CancellationToken cancellationToken)
    {
        if (count <= 0) return Task.CompletedTask;

        nint display = XOpenDisplay(null);
        if (display == 0)
            throw new InvalidOperationException(
                "Cannot connect to X11 display. Ensure the DISPLAY environment variable is set.");

        try
        {
            XDisplayKeycodes(display, out _, out int maxKeycode);
            int scratch = maxKeycode;

            // Map scratch keycode to BackSpace keysym.
            ulong[] mapping = [XK_BackSpace, XK_BackSpace];
            XChangeKeyboardMapping(display, scratch, 2, mapping, 1);
            XSync(display, false);

            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                XTestFakeKeyEvent(display, (uint)scratch, true, 0);
                XTestFakeKeyEvent(display, (uint)scratch, false, 0);
            }

            XFlush(display);
            XSync(display, false);

            ulong[] clear = [0UL, 0UL];
            XChangeKeyboardMapping(display, scratch, 2, clear, 1);
            XSync(display, false);
        }
        finally
        {
            XCloseDisplay(display);
        }

        return Task.CompletedTask;
    }
}
