// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hush.Core.Output;

/// <summary>
/// Captures the foreground window at the start of a dictation session and
/// verifies that it still holds keyboard focus before each output operation.
/// Used by the spinner/cleanse path to avoid leaking typed characters into a
/// window the user has clicked into while the LLM is rewriting (or mid-spinner).
/// <para>
/// On non-Windows platforms the guard is a no-op — focus loss during an
/// active dictation session is a Windows-specific failure mode of the
/// <c>SendInput</c>-based typer (which always delivers to the current
/// foreground window).
/// </para>
/// </summary>
public static class TargetWindowGuard
{
    /// <summary>Opaque handle returned by <see cref="Capture"/>. Zero means "no guard".</summary>
    public readonly record struct Handle(nint Hwnd)
    {
        public bool IsEmpty => Hwnd == 0;
        public static Handle None => new(0);
    }

    /// <summary>
    /// Captures the currently-focused window so that subsequent output can be
    /// verified against it. Returns <see cref="Handle.None"/> on non-Windows
    /// platforms or when no window has focus.
    /// </summary>
    public static Handle Capture()
    {
        if (!OperatingSystem.IsWindows())
            return Handle.None;

        return new Handle(WindowsApi.GetForegroundWindow());
    }

    /// <summary>
    /// Returns <see langword="true"/> when the captured window still holds focus
    /// (or when the handle is empty, i.e. no guard is active). Non-Windows
    /// platforms always return <see langword="true"/>.
    /// </summary>
    public static bool IsStillForeground(Handle handle)
    {
        if (handle.IsEmpty || !OperatingSystem.IsWindows())
            return true;

        return WindowsApi.GetForegroundWindow() == handle.Hwnd;
    }

    /// <summary>
    /// If the captured window no longer has focus, attempts to restore it via
    /// <c>SetForegroundWindow</c>. Returns <see langword="true"/> when the
    /// target window is confirmed foreground after the call (either it never
    /// moved or the restore succeeded). Returns <see langword="false"/> when
    /// restoration failed — callers should skip typing in that case.
    /// </summary>
    public static bool TryEnsureForeground(Handle handle)
    {
        if (handle.IsEmpty || !OperatingSystem.IsWindows())
            return true;

        return TryEnsureForegroundWindows(handle.Hwnd);
    }

    [SupportedOSPlatform("windows")]
    private static bool TryEnsureForegroundWindows(nint hwnd)
    {
        if (WindowsApi.GetForegroundWindow() == hwnd)
            return true;

        if (!WindowsApi.IsWindow(hwnd))
            return false;

        // Windows enforces focus-stealing restrictions on SetForegroundWindow.
        // The standard bypass is to temporarily attach our input thread to the
        // thread that owns the current foreground window: while attached, the
        // two threads share an input queue and SetForegroundWindow is allowed
        // to switch focus. This is a well-known trick documented in MSDN and
        // used by every serious automation / AT tool on Windows.
        uint currentThread = WindowsApi.GetCurrentThreadId();
        nint fgWindow = WindowsApi.GetForegroundWindow();
        uint fgThread = fgWindow == 0
            ? 0
            : WindowsApi.GetWindowThreadProcessId(fgWindow, out _);

        uint targetThread = WindowsApi.GetWindowThreadProcessId(hwnd, out _);
        if (targetThread == 0)
            return false;

        bool attachedFg = false;
        bool attachedTarget = false;
        try
        {
            if (fgThread != 0 && fgThread != currentThread)
                attachedFg = WindowsApi.AttachThreadInput(currentThread, fgThread, true);
            if (targetThread != currentThread && targetThread != fgThread)
                attachedTarget = WindowsApi.AttachThreadInput(currentThread, targetThread, true);

            // Some windows are minimised when the user clicks the taskbar of
            // a different app; restore first so SetForegroundWindow can
            // actually bring them forward.
            if (WindowsApi.IsIconic(hwnd))
                WindowsApi.ShowWindow(hwnd, SW_RESTORE);

            WindowsApi.BringWindowToTop(hwnd);
            WindowsApi.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attachedFg)
                WindowsApi.AttachThreadInput(currentThread, fgThread, false);
            if (attachedTarget)
                WindowsApi.AttachThreadInput(currentThread, targetThread, false);
        }

        return WindowsApi.GetForegroundWindow() == hwnd;
    }

    private const int SW_RESTORE = 9;

    private static class WindowsApi
    {
        [DllImport("user32.dll")]
        internal static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(nint hWnd);

        [DllImport("user32.dll")]
        internal static extern bool IsWindow(nint hWnd);

        [DllImport("user32.dll")]
        internal static extern bool IsIconic(nint hWnd);

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(nint hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        internal static extern bool BringWindowToTop(nint hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

        [DllImport("user32.dll")]
        internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();
    }
}
