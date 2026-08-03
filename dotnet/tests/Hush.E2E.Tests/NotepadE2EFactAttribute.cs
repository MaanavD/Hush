using System.Diagnostics;

namespace Hush.E2E.Tests;

internal sealed class NotepadE2EFactAttribute : FactAttribute
{
    public NotepadE2EFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Notepad E2E tests require Windows.";
            return;
        }

        if (!NotepadE2EOptions.RunNotepadE2E)
        {
            Skip = "Set HUSH_RUN_NOTEPAD_E2E=1 to run Windows Notepad E2E tests.";
            return;
        }

        if (OperatingSystem.IsWindows() && WindowsInteractiveDesktop.IsLocked())
            Skip = "Windows desktop is locked; unlock the session to run real Notepad E2E tests.";
    }
}

internal sealed class NotepadReportE2EFactAttribute : FactAttribute
{
    public NotepadReportE2EFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Notepad report E2E tests require Windows.";
            return;
        }

        if (!NotepadE2EOptions.RunNotepadE2E || !NotepadE2EOptions.RunAllAudioReport)
        {
            Skip = "Set HUSH_RUN_NOTEPAD_E2E=1 and HUSH_RUN_NOTEPAD_E2E_REPORT_ALL=1 to run the all-audio Notepad report.";
            return;
        }
    }
}

internal sealed class NotepadStandardReportE2EFactAttribute : FactAttribute
{
    public NotepadStandardReportE2EFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Notepad standard report E2E tests require Windows.";
            return;
        }

        if (!NotepadE2EOptions.RunNotepadE2E || !NotepadE2EOptions.RunStandardAllAudioReport)
        {
            Skip = "Set HUSH_RUN_NOTEPAD_E2E=1 and HUSH_RUN_NOTEPAD_E2E_REPORT_STANDARD=1 to run the standard all-audio Notepad report.";
            return;
        }
    }
}

internal static class WindowsInteractiveDesktop
{
    public static bool IsLocked()
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0)
            return false;

        _ = GetWindowThreadProcessId(foreground, out uint processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, "LockApp", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
}
