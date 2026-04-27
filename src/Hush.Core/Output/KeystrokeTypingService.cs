// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Globalization;
using System.Runtime.InteropServices;
using Hush.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hush.Core.Output;

/// <summary>
/// Outputs transcribed text into the currently focused application.
/// On Windows, text is injected via <c>SendInput</c> with <c>KEYEVENTF_UNICODE</c>,
/// which does not touch the clipboard. A clipboard-paste fallback is used
/// automatically for apps whose TSF layer garbles Unicode input events
/// (e.g. Windows 11 Notepad, WinUI3 TextBox), or when <c>ClipboardFallback = true</c>.
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

        _logger.LogDebug("TypeText: len={Len} skip={Skip} text={Text}",
            text.Length, skipModifierRestore, text);

        if (OperatingSystem.IsWindows())
        {
            bool useClipboard = _useClipboardFallback || ForegroundWindowDetector.IsTsfProblematic();
            if (useClipboard)
                _logger.LogDebug("TypeText: using clipboard paste (TSF-problematic window detected)");
            return useClipboard
                ? WindowsClipboardTyper.TypeAsync(text, cancellationToken)
                : WindowsKeystrokeTyper.TypeAsync(text, cancellationToken, skipModifierRestore, _logger);
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

        _logger.LogDebug("SendBackspaces: count={Count} skip={Skip}", count, skipModifierRestore);

        if (OperatingSystem.IsWindows())
            return WindowsKeystrokeTyper.SendBackspacesAsync(count, cancellationToken, skipModifierRestore, _logger);

        if (OperatingSystem.IsMacOS())
            return MacKeystrokeTyper.SendBackspacesAsync(count, cancellationToken);

        if (OperatingSystem.IsLinux())
            return LinuxKeystrokeTyper.SendBackspacesAsync(count, cancellationToken);

        throw new PlatformNotSupportedException(
            $"Text output is not supported on this platform ({RuntimeInformation.OSDescription}).");
    }

    /// <inheritdoc/>
    public async Task ReplaceTextAsync(
        int backspaceCount,
        string replacementText,
        CancellationToken cancellationToken = default,
        bool skipModifierRestore = false,
        bool boundToCurrentLine = false,
        string? expectedExistingText = null)
    {
        if (cancellationToken.IsCancellationRequested)
            await Task.FromCanceled(cancellationToken);

        if (OperatingSystem.IsWindows()
            && boundToCurrentLine
            && ForegroundWindowDetector.IsTsfProblematic())
        {
            _logger.LogDebug(
                "ReplaceText: current-line bounded replacement len={Len} expectedLen={ExpectedLen}",
                replacementText.Length,
                expectedExistingText?.Length ?? 0);
            if (await WindowsAutomationTextReplacer.TryReplaceFocusedSuffixAsync(
                    expectedExistingText,
                    replacementText,
                    cancellationToken,
                    _logger))
            {
                return;
            }

            await WindowsClipboardTyper.ReplaceExpectedTextAsync(
                expectedExistingText,
                replacementText,
                cancellationToken);
            return;
        }

        await SendBackspacesAsync(backspaceCount, cancellationToken, skipModifierRestore);
        await TypeTextAsync(replacementText, cancellationToken, skipModifierRestore);
    }

    /// <inheritdoc/>
    public Task SendKeyAsync(AutoSubmitKey key, CancellationToken cancellationToken = default)
    {
        if (key == AutoSubmitKey.None)
            return Task.CompletedTask;

        if (OperatingSystem.IsWindows())
            return WindowsKeystrokeTyper.SendKeyAsync(key, cancellationToken);

        if (OperatingSystem.IsMacOS())
            return MacKeystrokeTyper.SendKeyAsync(key, cancellationToken);

        if (OperatingSystem.IsLinux())
            return LinuxKeystrokeTyper.SendKeyAsync(key, cancellationToken);

        throw new PlatformNotSupportedException(
            $"Text output is not supported on this platform ({RuntimeInformation.OSDescription}).");
    }

}

// ────────────────────────────────────────────────────────────────────────────
// Windows — Foreground window detection for TSF-problematic apps
//
// Windows 11 Notepad and other WinUI3 apps use the Text Services Framework
// (TSF) for text input. TSF garbles KEYEVENTF_UNICODE events — even when
// sent one character at a time — producing repeated last-character output.
// This helper detects such windows so the typing service can switch to
// clipboard-paste automatically.
// ────────────────────────────────────────────────────────────────────────────

internal static class ForegroundWindowDetector
{
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(nint hWnd, char[] lpClassName, int nMaxCount);

    // Known window class names whose TSF layer garbles KEYEVENTF_UNICODE events.
    private static readonly string[] TsfProblematicClasses =
    [
        "Notepad",                         // Windows 11 Notepad (WinUI3)
        "WinUIDesktopWin32WindowClass",    // Generic WinUI3 host window
    ];

    /// <summary>
    /// Returns true when the foreground window belongs to an app class that is
    /// known to garble <c>KEYEVENTF_UNICODE</c> SendInput events.
    /// </summary>
    internal static bool IsTsfProblematic()
    {
        nint hwnd = GetForegroundWindow();
        if (hwnd == 0) return false;

        var buf = new char[256];
        int len = GetClassName(hwnd, buf, buf.Length);
        if (len <= 0) return false;

        var className = new ReadOnlySpan<char>(buf, 0, len);
        foreach (string problematic in TsfProblematicClasses)
        {
            if (className.Equals(problematic, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Also catch child-process WinUI3 windows whose class contains "WinUI"
        return className.Contains("WinUI", StringComparison.OrdinalIgnoreCase);
    }
}

// ────────────────────────────────────────────────────────────────────────────
// Windows — UI Automation document-value replacement
//
// Keyboard selection in Windows 11 Notepad/WinUI3 can under-select long wrapped
// live previews even when every SendInput call succeeds. UI Automation lets us
// operate on the editor's actual text value instead: if the focused editable
// value ends with the Hush-owned live preview, replace that suffix in one step.
// The clipboard/keyboard path remains as a fallback for controls without
// ValuePattern support.
// ────────────────────────────────────────────────────────────────────────────

internal static class WindowsAutomationTextReplacer
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_END = 0x23;
    private const int UIA_ValuePatternId = 10002;
    private const int MinPrefixAnchorLength = 16;
    private const int CaretRestoreSettleMs = 50;

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
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    internal static Task<bool> TryReplaceFocusedSuffixAsync(
        string? expectedExistingText,
        string replacementText,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(expectedExistingText))
            return Task.FromResult(false);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                tcs.TrySetResult(TryReplaceFocusedSuffix(expectedExistingText, replacementText, logger));
            }
            catch (OperationCanceledException ex)
            {
                tcs.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "UI Automation clean replacement failed; falling back to keyboard replacement.");
                tcs.TrySetResult(false);
            }
        });

        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Name = "Hush.UIAReplace";
        t.Start();
        return tcs.Task;
    }

    internal static bool TryCreateReplacementValue(
        string currentValue,
        string expectedExistingText,
        string replacementText,
        out string replacementValue,
        out int replaceStart)
    {
        replacementValue = currentValue;
        replaceStart = -1;

        if (string.IsNullOrEmpty(expectedExistingText))
            return false;

        if (currentValue.EndsWith(expectedExistingText, StringComparison.Ordinal))
        {
            replaceStart = currentValue.Length - expectedExistingText.Length;
            replacementValue = currentValue[..replaceStart] + replacementText;
            return true;
        }

        int exactIndex = currentValue.LastIndexOf(expectedExistingText, StringComparison.Ordinal);
        if (exactIndex >= 0)
        {
            replaceStart = exactIndex;
            replacementValue = currentValue[..exactIndex] + replacementText;
            return true;
        }

        var anchor = expectedExistingText[..Math.Min(expectedExistingText.Length, MinPrefixAnchorLength)];
        int anchorIndex = currentValue.LastIndexOf(anchor, StringComparison.Ordinal);
        if (anchorIndex >= 0 && IsLineSuffix(currentValue, anchorIndex))
        {
            replaceStart = anchorIndex;
            replacementValue = currentValue[..anchorIndex] + replacementText;
            return true;
        }

        return false;
    }

    private static bool IsLineSuffix(string text, int index)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            if (text[i] is '\r' or '\n')
                return true;
            if (!char.IsWhiteSpace(text[i]))
                return false;
        }

        return true;
    }

    private static bool TryReplaceFocusedSuffix(
        string expectedExistingText,
        string replacementText,
        ILogger? logger)
    {
        Type? automationType = Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"));
        if (automationType is null)
            return false;

        object? automationObject = null;
        object? valuePatternObject = null;
        try
        {
            automationObject = Activator.CreateInstance(automationType);
            if (automationObject is not IUIAutomation automation)
                return false;

            int hr = automation.GetFocusedElement(out var focusedElement);
            if (hr != 0 || focusedElement is null)
            {
                logger?.LogDebug("UI Automation clean replacement skipped: GetFocusedElement hr={Hr}.", hr);
                return false;
            }

            hr = focusedElement.GetCurrentPattern(UIA_ValuePatternId, out valuePatternObject);
            if (hr != 0 || valuePatternObject is not IUIAutomationValuePattern valuePattern)
            {
                logger?.LogDebug("UI Automation clean replacement skipped: ValuePattern hr={Hr}.", hr);
                return false;
            }

            hr = valuePattern.get_CurrentIsReadOnly(out int isReadOnly);
            if (hr != 0 || isReadOnly != 0)
            {
                logger?.LogDebug(
                    "UI Automation clean replacement skipped: read-only={ReadOnly} hr={Hr}.",
                    isReadOnly,
                    hr);
                return false;
            }

            hr = valuePattern.get_CurrentValue(out string? currentValue);
            if (hr != 0 || currentValue is null)
            {
                logger?.LogDebug("UI Automation clean replacement skipped: CurrentValue hr={Hr}.", hr);
                return false;
            }

            if (!TryCreateReplacementValue(
                    currentValue,
                    expectedExistingText,
                    replacementText,
                    out var replacementValue,
                    out int replaceStart))
            {
                logger?.LogDebug(
                    "UI Automation clean replacement skipped: expected preview not found (docLen={DocLen}, expectedLen={ExpectedLen}).",
                    currentValue.Length,
                    expectedExistingText.Length);
                return false;
            }

            hr = valuePattern.SetValue(replacementValue);
            if (hr != 0)
            {
                logger?.LogDebug("UI Automation clean replacement SetValue failed hr={Hr}.", hr);
                return false;
            }

            try
            {
                MoveCaretToDocumentEnd(focusedElement, logger);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "UI Automation clean replacement succeeded but caret restore failed.");
            }

            logger?.LogDebug(
                "UI Automation clean replacement succeeded: docLen={DocLen} expectedLen={ExpectedLen} replacementLen={ReplacementLen} start={Start}.",
                currentValue.Length,
                expectedExistingText.Length,
                replacementText.Length,
                replaceStart);
            return true;
        }
        finally
        {
            if (valuePatternObject is not null && Marshal.IsComObject(valuePatternObject))
                Marshal.ReleaseComObject(valuePatternObject);
            if (automationObject is not null && Marshal.IsComObject(automationObject))
                Marshal.ReleaseComObject(automationObject);
        }
    }

    private static void MoveCaretToDocumentEnd(IUIAutomationElement focusedElement, ILogger? logger)
    {
        int hr = focusedElement.SetFocus();
        if (hr != 0)
            logger?.LogDebug("UI Automation caret restore SetFocus returned hr={Hr}; sending Ctrl+End anyway.", hr);

        Thread.Sleep(CaretRestoreSettleMs);
        SendCtrlEnd();
    }

    private static void SendCtrlEnd()
    {
        INPUT[] inputs =
        [
            MakeKey(VK_CONTROL, 0),
            MakeKey(VK_END, 0),
            MakeKey(VK_END, KEYEVENTF_KEYUP),
            MakeKey(VK_CONTROL, KEYEVENTF_KEYUP),
        ];

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"SendInput failed while restoring caret after UIA replacement (sent {sent}/{inputs.Length}, Win32={error}).");
        }
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

    [ComImport]
    [Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        [PreserveSig]
        int CompareElements(
            [MarshalAs(UnmanagedType.Interface)] object? el1,
            [MarshalAs(UnmanagedType.Interface)] object? el2,
            out int areSame);

        [PreserveSig]
        int CompareRuntimeIds(IntPtr runtimeId1, IntPtr runtimeId2, out int areSame);

        [PreserveSig]
        int GetRootElement([MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? root);

        [PreserveSig]
        int ElementFromHandle(nint hwnd, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? element);

        [PreserveSig]
        int ElementFromPoint(UiaPoint pt, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? element);

        [PreserveSig]
        int GetFocusedElement([MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? element);
    }

    [ComImport]
    [Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        [PreserveSig]
        int SetFocus();

        [PreserveSig]
        int GetRuntimeId(out IntPtr runtimeId);

        [PreserveSig]
        int FindFirst(int scope, [MarshalAs(UnmanagedType.Interface)] object condition, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? found);

        [PreserveSig]
        int FindAll(int scope, [MarshalAs(UnmanagedType.Interface)] object condition, [MarshalAs(UnmanagedType.Interface)] out IntPtr found);

        [PreserveSig]
        int FindFirstBuildCache(int scope, [MarshalAs(UnmanagedType.Interface)] object condition, [MarshalAs(UnmanagedType.Interface)] object cacheRequest, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? found);

        [PreserveSig]
        int FindAllBuildCache(int scope, [MarshalAs(UnmanagedType.Interface)] object condition, [MarshalAs(UnmanagedType.Interface)] object cacheRequest, [MarshalAs(UnmanagedType.Interface)] out IntPtr found);

        [PreserveSig]
        int BuildUpdatedCache([MarshalAs(UnmanagedType.Interface)] object cacheRequest, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? updatedElement);

        [PreserveSig]
        int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);

        [PreserveSig]
        int GetCurrentPropertyValueEx(int propertyId, int ignoreDefaultValue, [MarshalAs(UnmanagedType.Struct)] out object value);

        [PreserveSig]
        int GetCachedPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);

        [PreserveSig]
        int GetCachedPropertyValueEx(int propertyId, int ignoreDefaultValue, [MarshalAs(UnmanagedType.Struct)] out object value);

        [PreserveSig]
        int GetCurrentPatternAs(int patternId, in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object patternObject);

        [PreserveSig]
        int GetCachedPatternAs(int patternId, in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object patternObject);

        [PreserveSig]
        int GetCurrentPattern(int patternId, [MarshalAs(UnmanagedType.IUnknown)] out object patternObject);
    }

    [ComImport]
    [Guid("A94CD8B1-0844-4CD6-9D2D-640537AB39E9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationValuePattern
    {
        [PreserveSig]
        int SetValue([MarshalAs(UnmanagedType.BStr)] string val);

        [PreserveSig]
        int get_CurrentValue([MarshalAs(UnmanagedType.BStr)] out string? retVal);

        [PreserveSig]
        int get_CurrentIsReadOnly(out int retVal);

        [PreserveSig]
        int get_CachedValue([MarshalAs(UnmanagedType.BStr)] out string? retVal);

        [PreserveSig]
        int get_CachedIsReadOnly(out int retVal);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct UiaPoint
    {
        public readonly double X;
        public readonly double Y;
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
    private const int TsfBackspaceInterKeyDelayMs = 4;

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

    private const ushort VK_BACK      = 0x08;
    private const ushort SC_BACK      = 0x0E;   // Hardware scan code for backspace key
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    internal static Task TypeAsync(string text, CancellationToken cancellationToken, bool skipModifierRestore = false, ILogger? logger = null)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pressedModifiers = GetPressedModifierVks();
            int modCount = pressedModifiers.Count;
            int cbSize = Marshal.SizeOf<INPUT>();

            // Release held modifiers (separate batch so they're lifted before we type)
            if (modCount > 0)
            {
                var modInputs = new INPUT[modCount];
                for (int i = 0; i < modCount; i++)
                    modInputs[i] = MakeVkInput(pressedModifiers[i], KEYEVENTF_KEYUP);
                SendInput((uint)modCount, modInputs, cbSize);
            }

            // Type each character as an individual SendInput call (down + up).
            // TSF-aware apps (Windows 11 Notepad, WinUI3 TextBox) garble batched
            // KEYEVENTF_UNICODE events — they apply the last scan code to every
            // character in the batch. Sending one char at a time avoids this.
            var charPair = new INPUT[2];
            uint totalSent = 0;
            for (int i = 0; i < text.Length; i++)
            {
                ushort ch = text[i];
                charPair[0] = MakeUnicodeInput(ch, KEYEVENTF_UNICODE);
                charPair[1] = MakeUnicodeInput(ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
                totalSent += SendInput(2, charPair, cbSize);
            }

            // Optionally restore modifiers
            if (!skipModifierRestore)
            {
                var toRestore = GetModifiersToRestore(pressedModifiers);
                if (toRestore.Count > 0)
                {
                    var restoreInputs = new INPUT[toRestore.Count];
                    for (int i = 0; i < toRestore.Count; i++)
                        restoreInputs[i] = MakeVkInput(toRestore[i], 0);
                    SendInput((uint)toRestore.Count, restoreInputs, cbSize);
                }
            }

            int totalEvents = modCount + text.Length * 2;
            logger?.LogDebug("SendInput(type): mods={ModCount} chars={Chars} totalEvents={Total}", modCount, text.Length, totalEvents);
            logger?.LogDebug("SendInput(type) returned: sent={Sent}/{Total}", totalSent, text.Length * 2);
            if (totalSent == 0 && text.Length > 0)
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    error == 5
                        ? "Cannot type into an elevated (administrator) application. " +
                          "Run Hush as administrator, or switch to a non-elevated window."
                        : $"SendInput failed (sent 0/{totalEvents}, Win32 error {error}). " +
                          "The focused application may not accept simulated input.");
            }
        }, cancellationToken);
    }

    internal static Task SendBackspacesAsync(int count, CancellationToken cancellationToken, bool skipModifierRestore = false, ILogger? logger = null)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pressedModifiers = GetPressedModifierVks();

            if (count > 1 && ForegroundWindowDetector.IsTsfProblematic())
            {
                SendBackspacesOneAtATime(count, pressedModifiers, cancellationToken, skipModifierRestore, logger);
                return;
            }

            // Single atomic SendInput batch: modifier releases + backspaces + optional restores.
            int modCount = pressedModifiers.Count;
            int restoreCount = skipModifierRestore ? 0 : modCount;
            var inputs = new INPUT[modCount + count * 2 + restoreCount];
            int idx = 0;

            for (int i = 0; i < modCount; i++)
                inputs[idx++] = MakeVkInput(pressedModifiers[i], KEYEVENTF_KEYUP);

            for (int i = 0; i < count; i++)
            {
                inputs[idx++] = MakeBackspaceInput(0);
                inputs[idx++] = MakeBackspaceInput(KEYEVENTF_KEYUP);
            }

            if (!skipModifierRestore)
            {
                var toRestore = GetModifiersToRestore(pressedModifiers);
                if (toRestore.Count < modCount)
                    Array.Resize(ref inputs, modCount + count * 2 + toRestore.Count);
                for (int i = 0; i < toRestore.Count; i++)
                    inputs[idx++] = MakeVkInput(toRestore[i], 0);
            }

            logger?.LogDebug("SendInput(backspace): count={Count} mods={ModCount} totalEvents={Total}", count, modCount, idx);
            SendInput((uint)idx, inputs, Marshal.SizeOf<INPUT>());
            logger?.LogDebug("SendInput(backspace) returned");
        }, cancellationToken);
    }

    private static void SendBackspacesOneAtATime(
        int count,
        IReadOnlyList<ushort> pressedModifiers,
        CancellationToken cancellationToken,
        bool skipModifierRestore,
        ILogger? logger)
    {
        int cbSize = Marshal.SizeOf<INPUT>();

        if (pressedModifiers.Count > 0)
        {
            var modifierInputs = new INPUT[pressedModifiers.Count];
            for (int i = 0; i < pressedModifiers.Count; i++)
                modifierInputs[i] = MakeVkInput(pressedModifiers[i], KEYEVENTF_KEYUP);
            SendInput((uint)modifierInputs.Length, modifierInputs, cbSize);
        }

        var backspaceInputs = new[]
        {
            MakeBackspaceInput(0),
            MakeBackspaceInput(KEYEVENTF_KEYUP),
        };

        uint totalSent = 0;
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalSent += SendInput(2, backspaceInputs, cbSize);
            if (i + 1 < count)
                Thread.Sleep(TsfBackspaceInterKeyDelayMs);
        }

        if (!skipModifierRestore)
        {
            var toRestore = GetModifiersToRestore(pressedModifiers);
            if (toRestore.Count > 0)
            {
                var restoreInputs = new INPUT[toRestore.Count];
                for (int i = 0; i < toRestore.Count; i++)
                    restoreInputs[i] = MakeVkInput(toRestore[i], 0);
                SendInput((uint)restoreInputs.Length, restoreInputs, cbSize);
            }
        }

        logger?.LogDebug(
            "SendInput(backspace-tsf): count={Count} mods={ModCount} totalSent={Sent}/{Total}",
            count, pressedModifiers.Count, totalSent, count * 2);
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

    // SendUnicodeString was removed — all typing now goes through TypeAsync
    // which sends each character individually to avoid TSF batching issues.

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

    // Backspace is sent with both VirtualKey AND ScanCode so that TSF-aware apps
    // (WinUI3 Notepad, UWP TextBox, etc.) see a fully-formed hardware-like event
    // and don't need to infer the character from the scan code alone.
    private static INPUT MakeBackspaceInput(uint extraFlags) => new()
    {
        Type = INPUT_KEYBOARD,
        Union = new INPUTUNION
        {
            Keyboard = new KEYBDINPUT
            {
                VirtualKey = VK_BACK,
                ScanCode   = SC_BACK,
                Flags      = KEYEVENTF_SCANCODE | extraFlags,
                ExtraInfo  = (nint)WindowsInputCoordinator.InjectedExtraInfo,
            }
        }
    };

    private const ushort VK_RETURN  = 0x0D;
    private const ushort VK_CONTROL = 0x11;

    internal static Task SendKeyAsync(AutoSubmitKey key, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            int cbSize = Marshal.SizeOf<INPUT>();

            if (key == AutoSubmitKey.CtrlEnter)
            {
                INPUT[] inputs =
                [
                    MakeVkInput(VK_CONTROL, 0),
                    MakeVkInput(VK_RETURN, 0),
                    MakeVkInput(VK_RETURN, KEYEVENTF_KEYUP),
                    MakeVkInput(VK_CONTROL, KEYEVENTF_KEYUP),
                ];
                SendInput((uint)inputs.Length, inputs, cbSize);
            }
            else // AutoSubmitKey.Enter
            {
                INPUT[] inputs =
                [
                    MakeVkInput(VK_RETURN, 0),
                    MakeVkInput(VK_RETURN, KEYEVENTF_KEYUP),
                ];
                SendInput((uint)inputs.Length, inputs, cbSize);
            }
        }, cancellationToken);
    }
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
    private const ushort VK_SHIFT      = 0x10;
    private const ushort VK_HOME       = 0x24;
    private const ushort VK_LEFT       = 0x25;
    private const ushort VK_V          = 0x56;
    private const uint CF_UNICODETEXT  = 13;
    private const uint GMEM_MOVEABLE   = 0x0002;
    private const int ClipboardPasteBaseSettleMs = 120;
    private const int ClipboardPastePerCharMs = 1;
    private const int ClipboardPasteMaxSettleMs = 350;
    private const int ReplacementClipboardRestoreBaseMs = 1200;
    private const int ReplacementClipboardRestorePerCharMs = 3;
    private const int ReplacementClipboardRestoreMaxMs = 3000;
    private const int ClipboardSettleBeforePasteMs = 60;
    private const int SelectionPasteSettleBaseMs = 120;
    private const int SelectionPasteSettlePerCharMs = 2;
    private const int SelectionPasteSettleMaxMs = 900;
    private const int SelectionCharacterBatchSize = 8;
    private const int SelectionCharacterBatchDelayMs = 8;
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
                if (!TrySetClipboardText(text))
                    throw new InvalidOperationException("Unable to set clipboard text before typing into a TSF-backed target.");

                Thread.Sleep(ClipboardSettleBeforePasteMs);
                SendCtrlV();

                Thread.Sleep(CalculateClipboardRestoreDelayMs(text.Length));
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

    internal static Task ReplaceExpectedTextAsync(
        string? expectedExistingText,
        string replacementText,
        CancellationToken cancellationToken)
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
                if (!TrySetClipboardText(replacementText))
                    throw new InvalidOperationException("Unable to set clipboard text before replacing clean-mode preview.");

                Thread.Sleep(ClipboardSettleBeforePasteMs);

                int selectedCharacterCount = SelectExpectedPreviousText(expectedExistingText);
                if (selectedCharacterCount <= 0)
                {
                    SelectToStartOfCurrentLine();
                }
                else
                {
                    Thread.Sleep(CalculateSelectionPasteSettleDelayMs(selectedCharacterCount));
                }

                SendCtrlV();

                Thread.Sleep(CalculateReplacementClipboardRestoreDelayMs(
                    replacementText.Length,
                    selectedCharacterCount));
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
                    // Best effort only; preserve the original replacement exception.
                }

                tcs.TrySetException(ex);
            }
        });

        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Name = "Hush.ClipboardLineReplace";
        t.Start();
        return tcs.Task;
    }

    internal static int CountSelectionCharacters(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        return StringInfo.ParseCombiningCharacters(text).Length;
    }

    internal static int CalculateClipboardRestoreDelayMs(int textLength)
        => Math.Clamp(
            ClipboardPasteBaseSettleMs + Math.Max(0, textLength) * ClipboardPastePerCharMs,
            ClipboardPasteBaseSettleMs,
            ClipboardPasteMaxSettleMs);

    internal static int CalculateSelectionPasteSettleDelayMs(int characterCount)
        => Math.Clamp(
            SelectionPasteSettleBaseMs + Math.Max(0, characterCount) * SelectionPasteSettlePerCharMs,
            SelectionPasteSettleBaseMs,
            SelectionPasteSettleMaxMs);

    internal static int CalculateReplacementClipboardRestoreDelayMs(int replacementLength, int selectedCharacterCount)
        => Math.Clamp(
            ReplacementClipboardRestoreBaseMs
            + Math.Max(0, replacementLength) * ReplacementClipboardRestorePerCharMs
            + Math.Max(0, selectedCharacterCount),
            ReplacementClipboardRestoreBaseMs,
            ReplacementClipboardRestoreMaxMs);

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

    private static bool TrySetClipboardText(string text)
    {
        int bytes = (text.Length + 1) * 2;
        var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes);
        if (hGlobal == 0)
            return false;

        var ptr = GlobalLock(hGlobal);
        if (ptr == 0)
        {
            GlobalFree(hGlobal);
            return false;
        }

        Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
        Marshal.WriteInt16(ptr + text.Length * 2, 0);
        GlobalUnlock(hGlobal);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(0))
            {
                try
                {
                    EmptyClipboard();
                    if (SetClipboardData(CF_UNICODETEXT, hGlobal) != 0)
                        return true;

                    GlobalFree(hGlobal);
                    return false;
                }
                finally { CloseClipboard(); }
            }
            Thread.Sleep(20);
        }

        GlobalFree(hGlobal);
        return false;
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

    private static void SelectToStartOfCurrentLine()
    {
        INPUT[] inputs =
        [
            MakeKey(VK_SHIFT, 0),
            MakeKey(VK_HOME, 0),
            MakeKey(VK_HOME, KEYEVENTF_KEYUP),
            MakeKey(VK_SHIFT, KEYEVENTF_KEYUP),
        ];

        SendInputs(inputs);
    }

    private static int SelectExpectedPreviousText(string? expectedExistingText)
    {
        int characterCount = CountSelectionCharacters(expectedExistingText);
        if (characterCount <= 0)
            return 0;

        SelectPreviousCharacters(characterCount);
        return characterCount;
    }

    private static void SelectPreviousCharacters(int count)
    {
        SendInputs([MakeKey(VK_SHIFT, 0)]);
        try
        {
            int remaining = count;
            while (remaining > 0)
            {
                int batch = Math.Min(remaining, SelectionCharacterBatchSize);
                var inputs = new INPUT[batch * 2];
                int idx = 0;
                for (int i = 0; i < batch; i++)
                {
                    inputs[idx++] = MakeKey(VK_LEFT, 0);
                    inputs[idx++] = MakeKey(VK_LEFT, KEYEVENTF_KEYUP);
                }

                SendInputs(inputs);
                remaining -= batch;
                if (remaining > 0)
                    Thread.Sleep(SelectionCharacterBatchDelayMs);
            }
        }
        finally
        {
            SendInputs([MakeKey(VK_SHIFT, KEYEVENTF_KEYUP)]);
        }
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

    // macOS virtual key codes for Return and Control.
    private const ushort kVK_Return    = 0x24;
    private const ushort kVK_Control   = 0x3B;

    internal static Task SendKeyAsync(AutoSubmitKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (key == AutoSubmitKey.CtrlEnter)
        {
            nint ctrlDown = CGEventCreateKeyboardEvent(0, kVK_Control, true);
            CGEventPost(kCGHIDEventTap, ctrlDown);
            CFRelease(ctrlDown);
        }

        nint retDown = CGEventCreateKeyboardEvent(0, kVK_Return, true);
        CGEventPost(kCGHIDEventTap, retDown);
        CFRelease(retDown);

        nint retUp = CGEventCreateKeyboardEvent(0, kVK_Return, false);
        CGEventPost(kCGHIDEventTap, retUp);
        CFRelease(retUp);

        if (key == AutoSubmitKey.CtrlEnter)
        {
            nint ctrlUp = CGEventCreateKeyboardEvent(0, kVK_Control, false);
            CGEventPost(kCGHIDEventTap, ctrlUp);
            CFRelease(ctrlUp);
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

    // X11 keysyms for Return and Control_L.
    private const ulong XK_Return    = 0xFF0D;
    private const ulong XK_Control_L = 0xFFE3;

    internal static Task SendKeyAsync(AutoSubmitKey key, CancellationToken cancellationToken)
    {
        if (key == AutoSubmitKey.None) return Task.CompletedTask;

        nint display = XOpenDisplay(null);
        if (display == 0)
            throw new InvalidOperationException(
                "Cannot connect to X11 display. Ensure the DISPLAY environment variable is set.");

        try
        {
            XDisplayKeycodes(display, out _, out int maxKeycode);
            int scratch = maxKeycode;

            if (key == AutoSubmitKey.CtrlEnter)
            {
                // Map scratch-1 to Control_L if available, else use scratch.
                int ctrlSlot = Math.Max(scratch - 1, 8);
                ulong[] ctrlMapping = [XK_Control_L, XK_Control_L];
                XChangeKeyboardMapping(display, ctrlSlot, 2, ctrlMapping, 1);
                XSync(display, false);
                XTestFakeKeyEvent(display, (uint)ctrlSlot, true, 0);

                ulong[] retMapping = [XK_Return, XK_Return];
                XChangeKeyboardMapping(display, scratch, 2, retMapping, 1);
                XSync(display, false);
                XTestFakeKeyEvent(display, (uint)scratch, true, 0);
                XTestFakeKeyEvent(display, (uint)scratch, false, 0);
                XFlush(display);

                XTestFakeKeyEvent(display, (uint)ctrlSlot, false, 0);
                XFlush(display);

                ulong[] clear = [0UL, 0UL];
                XChangeKeyboardMapping(display, ctrlSlot, 2, clear, 1);
                XChangeKeyboardMapping(display, scratch, 2, clear, 1);
                XSync(display, false);
            }
            else // AutoSubmitKey.Enter
            {
                ulong[] retMapping = [XK_Return, XK_Return];
                XChangeKeyboardMapping(display, scratch, 2, retMapping, 1);
                XSync(display, false);
                XTestFakeKeyEvent(display, (uint)scratch, true, 0);
                XTestFakeKeyEvent(display, (uint)scratch, false, 0);
                XFlush(display);
                XSync(display, false);

                ulong[] clear = [0UL, 0UL];
                XChangeKeyboardMapping(display, scratch, 2, clear, 1);
                XSync(display, false);
            }
        }
        finally
        {
            XCloseDisplay(display);
        }

        return Task.CompletedTask;
    }
}
