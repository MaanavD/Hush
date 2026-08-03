using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hush.Core.Output;

namespace Hush.E2E.Tests;

[SupportedOSPlatform("windows")]
internal sealed class NotepadTestApp : IAsyncDisposable, INotepadEditorTextSink
{
    private const int SW_RESTORE = 9;
    private const int UIA_ValuePatternId = 10002;
    private const int UIA_ProcessIdPropertyId = 30002;
    private const int UIA_IsValuePatternAvailablePropertyId = 30043;
    private const int TreeScopeDescendants = 4;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_A = 0x41;
    private const ushort VK_C = 0x43;
    private const ushort VK_N = 0x4E;
    private const ushort VK_V = 0x56;
    private const ushort VK_W = 0x57;
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private static readonly nint HWND_TOPMOST = new(-1);
    private static readonly nint HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private static readonly Guid CUIAutomationGuid = new("ff48dba4-60ef-4201-aa87-54103eef594e");
    private static readonly TimeSpan SetValueVerificationTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ClipboardVerificationTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan EditorVerificationPollInterval = TimeSpan.FromMilliseconds(75);

    private Process? _process;
    private nint _mainWindowHandle;

    private NotepadTestApp(Process? process, string filePath)
    {
        _process = process;
        FilePath = filePath;
    }

    public string FilePath { get; }

    public nint MainWindowHandle
    {
        get
        {
            if (_mainWindowHandle != 0)
                return _mainWindowHandle;

            var process = NotepadProcess;
            process.Refresh();
            return process.MainWindowHandle;
        }
    }

    private Process NotepadProcess =>
        _process ?? throw new InvalidOperationException("This Notepad test app is file-backed and has no interactive Notepad process.");

    public static async Task<NotepadTestApp> LaunchAsync(
        CancellationToken cancellationToken,
        bool requireInteractiveWindow = true)
    {
        NotepadPersistedStateCleanup.DeleteHushOwnedPersistedState();

        var filePath = Path.Combine(
            Path.GetTempPath(),
            $"{NotepadPersistedStateCleanup.HushTestFileNamePrefix}{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(filePath, string.Empty, cancellationToken);

        if (!requireInteractiveWindow)
            return new NotepadTestApp(process: null, filePath);

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "notepad.exe",
            Arguments = $"\"{filePath}\"",
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("Failed to start notepad.exe.");

        var app = new NotepadTestApp(process, filePath);
        if (requireInteractiveWindow)
        {
            await app.WaitForMainWindowAsync(cancellationToken);
            app.Focus();
            await Task.Delay(500, cancellationToken);
        }
        return app;
    }

    public void Focus()
    {
        var hwnd = MainWindowHandle;
        if (hwnd == 0)
            throw new InvalidOperationException("Notepad main window was not available.");

        var handle = new TargetWindowGuard.Handle(hwnd);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            ShowWindow(hwnd, SW_RESTORE);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
            SendAltKeyPress();
            TargetWindowGuard.TryEnsureForeground(handle);
            SetForegroundWindow(hwnd);
            SwitchToThisWindow(hwnd, true);
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
            TrySetWindowFocusWithUia(hwnd, NotepadProcess.Id);
            if (IsForegroundOwnedByNotepad(hwnd))
                return;

            Thread.Sleep(100);
        }

        throw new InvalidOperationException("Notepad could not be focused for E2E input.");
    }

    private bool IsForegroundOwnedByNotepad(nint expectedTopLevelHwnd)
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0)
            return false;

        if (foreground == expectedTopLevelHwnd)
            return true;

        var process = NotepadProcess;
        process.Refresh();
        _ = GetWindowThreadProcessId(foreground, out uint foregroundProcessId);
        return !process.HasExited && foregroundProcessId == (uint)process.Id;
    }

    public async Task<string> WaitForStableTextAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        string previous = string.Empty;
        while (true)
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            Focus();
            var current = ReadText();
            if (current == previous)
            {
                await Task.Delay(200, timeoutCts.Token);
                var next = ReadText();
                if (next == current)
                    return current;

                previous = next;
            }
            else
            {
                previous = current;
            }

            await Task.Delay(100, timeoutCts.Token);
        }
    }

    public Task<string> ReadFileTextAsync(CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(FilePath, cancellationToken);

    public Task WriteFileTextAsync(string text, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(FilePath, text, cancellationToken);

    public async Task SetEditorTextAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Focus();
        Exception? uiaWriteFailure = null;

        try
        {
            WriteWindowText(text, NotepadProcess.Id, MainWindowHandle);
            var uiaVerification = await WaitForEditorTextMatchAsync(
                text,
                SetValueVerificationTimeout,
                cancellationToken);
            if (uiaVerification.Matches)
                return;
        }
        catch (InvalidOperationException ex)
        {
            uiaWriteFailure = ex;
        }

        WriteTextViaClipboard(text);
        var clipboardVerification = await WaitForEditorTextMatchAsync(
            text,
            ClipboardVerificationTimeout,
            cancellationToken);
        if (clipboardVerification.Matches)
            return;

        var expectedLength = NotepadEditorTextVerification.NormalizeLineEndings(text).Length;
        var actualLength = NotepadEditorTextVerification.NormalizeLineEndings(clipboardVerification.Actual).Length;
        var message =
            $"Notepad editor SetValue verification failed (expectedLen={expectedLength}, actualLen={actualLength}).";
        if (uiaWriteFailure is not null)
        {
            throw new InvalidOperationException(message, uiaWriteFailure);
        }

        throw new InvalidOperationException(message);
    }

    public Task<bool> TrySetEditorTextBestEffortAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var hwnd = MainWindowHandle;
            return Task.FromResult(hwnd != 0 && TryWriteWindowTextBestEffort(text, NotepadProcess.Id, hwnd));
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private async Task<(bool Matches, string Actual)> WaitForEditorTextMatchAsync(
        string expected,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        string actual = string.Empty;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Focus();
            actual = ReadText();
            if (NotepadEditorTextVerification.Matches(actual, expected))
                return (true, actual);

            if (DateTimeOffset.UtcNow >= deadline)
                return (false, actual);

            await Task.Delay(EditorVerificationPollInterval, cancellationToken);
        }
    }

    private async Task WaitForMainWindowAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        var fileName = Path.GetFileName(FilePath);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryCaptureWindowFromProcess(NotepadProcess, fileName))
                return;

            foreach (var process in Process.GetProcessesByName("Notepad"))
            {
                using (process)
                {
                    if (TryCaptureWindowFromProcess(process, fileName))
                    {
                        _process = Process.GetProcessById(process.Id);
                        return;
                    }
                }
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException("Timed out waiting for Notepad main window.");
    }

    private bool TryCaptureWindowFromProcess(Process process, string? expectedTitleFragment = null)
    {
        process.Refresh();
        if (process.HasExited || process.MainWindowHandle == 0)
            return false;

        if (expectedTitleFragment is not null &&
            !process.MainWindowTitle.Contains(expectedTitleFragment, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _mainWindowHandle = process.MainWindowHandle;
        return true;
    }

    private string ReadText()
    {
        try
        {
            return ReadWindowText(NotepadProcess.Id, MainWindowHandle);
        }
        catch (InvalidOperationException)
        {
            return ReadTextViaClipboard();
        }
    }

    private static string ReadWindowText(int expectedProcessId, nint hwnd)
    {
        string? value = null;
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                value = ReadWindowTextOnStaThread(expectedProcessId, hwnd);
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name = "Hush.NotepadE2E.UIAWindowRead";
        thread.Start();
        thread.Join();

        if (exception is not null)
            throw exception;

        return value ?? string.Empty;
    }

    private static string ReadFocusedText(int expectedProcessId)
    {
        string? value = null;
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                value = ReadFocusedTextOnStaThread(expectedProcessId);
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name = "Hush.NotepadE2E.UIARead";
        thread.Start();
        thread.Join();

        if (exception is not null)
            throw exception;

        return value ?? string.Empty;
    }

    private static void WriteFocusedText(string text, int expectedProcessId)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                WriteFocusedTextOnStaThread(text, expectedProcessId);
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name = "Hush.NotepadE2E.UIAWrite";
        thread.Start();
        thread.Join();

        if (exception is not null)
            throw exception;
    }

    private static void WriteWindowText(string text, int expectedProcessId, nint hwnd)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                WriteWindowTextOnStaThread(text, expectedProcessId, hwnd);
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name = "Hush.NotepadE2E.UIAWindowWrite";
        thread.Start();
        thread.Join();

        if (exception is not null)
            throw exception;
    }

    private static bool TryWriteWindowTextBestEffort(string text, int expectedProcessId, nint hwnd)
    {
        bool succeeded = false;
        var thread = new Thread(() =>
        {
            try
            {
                succeeded = TryWriteWindowTextBestEffortOnStaThread(text, expectedProcessId, hwnd);
            }
            catch
            {
                succeeded = false;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name = "Hush.NotepadE2E.UIAPreviewWrite";
        thread.Start();
        thread.Join();

        return succeeded;
    }

    private void FocusEditor()
    {
        Exception? exception = null;
        var hwnd = MainWindowHandle;
        var processId = NotepadProcess.Id;
        var thread = new Thread(() =>
        {
            try
            {
                FocusEditorOnStaThread(processId, hwnd);
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name = "Hush.NotepadE2E.UIAEditorFocus";
        thread.Start();
        thread.Join();

        if (exception is not null)
            throw exception;
    }

    private void WriteTextViaClipboard(string text)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                string? savedText = TryGetClipboardText();
                try
                {
                    if (!TrySetClipboardText(text))
                        throw new InvalidOperationException("Failed to stage replacement text on the clipboard.");

                    var stagedText = TryGetClipboardText();
                    if (!string.Equals(stagedText, text, StringComparison.Ordinal))
                        throw new InvalidOperationException("Clipboard verification failed before Notepad paste.");

                    Focus();
                    FocusEditor();
                    EnsureForegroundWindowBelongsToLaunchedNotepad("selecting Notepad text");
                    SendCtrlChord(VK_A);
                    Thread.Sleep(120);
                    FocusEditor();
                    EnsureForegroundWindowBelongsToLaunchedNotepad("pasting Notepad replacement text");
                    stagedText = TryGetClipboardText();
                    if (!string.Equals(stagedText, text, StringComparison.Ordinal))
                        throw new InvalidOperationException("Clipboard contents changed before Notepad paste.");

                    SendCtrlChord(VK_V);
                    Thread.Sleep(Math.Clamp(200 + text.Length * 2, 500, 1500));
                }
                finally
                {
                    TrySetClipboardText(savedText ?? string.Empty);
                }
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name = "Hush.NotepadE2E.ClipboardWrite";
        thread.Start();
        thread.Join();

        if (exception is not null)
            throw exception;
    }

    private string ReadTextViaClipboard()
    {
        string? value = null;
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                string? savedText = TryGetClipboardText();
                try
                {
                    var sentinel = $"HUSH_NOTEPAD_E2E_CLIPBOARD_SENTINEL_{Guid.NewGuid():N}";
                    if (!TrySetClipboardText(sentinel))
                        throw new InvalidOperationException("Failed to stage clipboard sentinel before reading Notepad text.");

                    Focus();
                    FocusEditor();
                    EnsureForegroundWindowBelongsToLaunchedNotepad("copying Notepad text");
                    SendCtrlChord(VK_A);
                    Thread.Sleep(120);
                    FocusEditor();
                    EnsureForegroundWindowBelongsToLaunchedNotepad("copying Notepad text");
                    SendCtrlChord(VK_C);
                    Thread.Sleep(250);
                    value = TryGetClipboardText() ?? string.Empty;
                    if (string.Equals(value, sentinel, StringComparison.Ordinal))
                        throw new InvalidOperationException("Clipboard readback did not change after copying Notepad text.");
                }
                finally
                {
                    TrySetClipboardText(savedText ?? string.Empty);
                }
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name = "Hush.NotepadE2E.ClipboardRead";
        thread.Start();
        thread.Join();

        if (exception is not null)
            throw exception;

        return value ?? string.Empty;
    }

    private static string ReadFocusedTextOnStaThread(int expectedProcessId)
    {
        Type? automationType = Type.GetTypeFromCLSID(CUIAutomationGuid);
        if (automationType is null)
            throw new InvalidOperationException("UI Automation COM type is unavailable.");

        object? automationObject = null;
        object? valuePatternObject = null;
        try
        {
            automationObject = Activator.CreateInstance(automationType);
            if (automationObject is not IUIAutomation automation)
                throw new InvalidOperationException("Failed to create UI Automation object.");

            int hr = automation.GetFocusedElement(out var focusedElement);
            if (hr != 0 || focusedElement is null)
                throw new InvalidOperationException($"UI Automation GetFocusedElement failed with HRESULT {hr}.");

            hr = focusedElement.GetCurrentPattern(UIA_ValuePatternId, out valuePatternObject);
            if (hr != 0 || valuePatternObject is not IUIAutomationValuePattern valuePattern)
                throw new InvalidOperationException($"Focused Notepad element does not expose ValuePattern; HRESULT {hr}.");

            EnsureFocusedElementBelongsToProcess(focusedElement, expectedProcessId);

            hr = valuePattern.get_CurrentValue(out string? currentValue);
            if (hr != 0)
                throw new InvalidOperationException($"UI Automation ValuePattern.CurrentValue failed with HRESULT {hr}.");

            return currentValue ?? string.Empty;
        }
        finally
        {
            if (valuePatternObject is not null && Marshal.IsComObject(valuePatternObject))
                Marshal.ReleaseComObject(valuePatternObject);
            if (automationObject is not null && Marshal.IsComObject(automationObject))
                Marshal.ReleaseComObject(automationObject);
        }
    }

    private static string ReadWindowTextOnStaThread(int expectedProcessId, nint hwnd)
    {
        Type? automationType = Type.GetTypeFromCLSID(CUIAutomationGuid);
        if (automationType is null)
            throw new InvalidOperationException("UI Automation COM type is unavailable.");

        object? automationObject = null;
        IUIAutomationElement? editorElement = null;
        try
        {
            automationObject = Activator.CreateInstance(automationType);
            if (automationObject is not IUIAutomation automation)
                throw new InvalidOperationException("Failed to create UI Automation object.");

            if (TryResolveEditorElement(automation, hwnd, expectedProcessId, out editorElement, out var resolveFailure))
            {
                try
                {
                    if (editorElement is null)
                        throw new InvalidOperationException("Resolved Notepad editor was unexpectedly null.");

                    if (TryReadValueFromElement(editorElement, expectedProcessId, out var value, out _))
                        return value;
                }
                finally
                {
                    if (editorElement is not null && Marshal.IsComObject(editorElement))
                        Marshal.ReleaseComObject(editorElement);
                    editorElement = null;
                }
            }

            if (IsForegroundOwnedByProcess(expectedProcessId))
            {
                int hr = automation.GetFocusedElement(out var focusedElement);
                try
                {
                    if (hr == 0 &&
                        focusedElement is not null &&
                        TryReadValueFromElement(focusedElement, expectedProcessId, out var value, out _))
                    {
                        return value;
                    }
                }
                finally
                {
                    if (focusedElement is not null && Marshal.IsComObject(focusedElement))
                        Marshal.ReleaseComObject(focusedElement);
                }
            }

            throw new InvalidOperationException(
                $"Unable to resolve Notepad editor ValuePattern from the launched window. {resolveFailure}");
        }
        finally
        {
            if (editorElement is not null && Marshal.IsComObject(editorElement))
                Marshal.ReleaseComObject(editorElement);
            if (automationObject is not null && Marshal.IsComObject(automationObject))
                Marshal.ReleaseComObject(automationObject);
        }
    }

    private static void WriteWindowTextOnStaThread(string text, int expectedProcessId, nint hwnd)
    {
        Type? automationType = Type.GetTypeFromCLSID(CUIAutomationGuid);
        if (automationType is null)
            throw new InvalidOperationException("UI Automation COM type is unavailable.");

        object? automationObject = null;
        object? valuePatternObject = null;
        IUIAutomationElement? editorElement = null;
        try
        {
            automationObject = Activator.CreateInstance(automationType);
            if (automationObject is not IUIAutomation automation)
                throw new InvalidOperationException("Failed to create UI Automation object.");

            if (!TryResolveEditorElement(automation, hwnd, expectedProcessId, out editorElement, out var resolveFailure))
            {
                throw new InvalidOperationException(
                    $"Unable to resolve Notepad editor ValuePattern from the launched window for SetValue. {resolveFailure}");
            }
            if (editorElement is null)
                throw new InvalidOperationException("Resolved Notepad editor was unexpectedly null.");

            int hr = editorElement.GetCurrentPattern(UIA_ValuePatternId, out valuePatternObject);
            if (hr != 0 || valuePatternObject is not IUIAutomationValuePattern valuePattern)
                throw new InvalidOperationException($"Resolved Notepad editor does not expose ValuePattern; HRESULT {hr}.");

            hr = valuePattern.get_CurrentIsReadOnly(out int isReadOnly);
            if (hr != 0 || isReadOnly != 0)
                throw new InvalidOperationException($"Resolved Notepad editor is read-only; HRESULT {hr}, readOnly={isReadOnly}.");

            hr = valuePattern.SetValue(text);
            if (hr != 0)
                throw new InvalidOperationException($"UI Automation ValuePattern.SetValue failed with HRESULT {hr}.");

            hr = valuePattern.get_CurrentValue(out string? verifiedValue);
            if (hr != 0 || !NotepadEditorTextVerification.Matches(verifiedValue ?? string.Empty, text))
            {
                throw new InvalidOperationException(
                    $"UI Automation ValuePattern.SetValue immediate verification failed with HRESULT {hr}.");
            }
        }
        finally
        {
            if (valuePatternObject is not null && Marshal.IsComObject(valuePatternObject))
                Marshal.ReleaseComObject(valuePatternObject);
            if (editorElement is not null && Marshal.IsComObject(editorElement))
                Marshal.ReleaseComObject(editorElement);
            if (automationObject is not null && Marshal.IsComObject(automationObject))
                Marshal.ReleaseComObject(automationObject);
        }
    }

    private static bool TryWriteWindowTextBestEffortOnStaThread(string text, int expectedProcessId, nint hwnd)
    {
        Type? automationType = Type.GetTypeFromCLSID(CUIAutomationGuid);
        if (automationType is null)
            return false;

        object? automationObject = null;
        object? valuePatternObject = null;
        IUIAutomationElement? editorElement = null;
        try
        {
            automationObject = Activator.CreateInstance(automationType);
            if (automationObject is not IUIAutomation automation)
                return false;

            if (!TryResolveEditorElement(automation, hwnd, expectedProcessId, out editorElement, out _)
                || editorElement is null)
            {
                return false;
            }

            int hr = editorElement.GetCurrentPattern(UIA_ValuePatternId, out valuePatternObject);
            if (hr != 0 || valuePatternObject is not IUIAutomationValuePattern valuePattern)
                return false;

            hr = valuePattern.get_CurrentIsReadOnly(out int isReadOnly);
            if (hr != 0 || isReadOnly != 0)
                return false;

            return valuePattern.SetValue(text) == 0;
        }
        finally
        {
            if (valuePatternObject is not null && Marshal.IsComObject(valuePatternObject))
                Marshal.ReleaseComObject(valuePatternObject);
            if (editorElement is not null && Marshal.IsComObject(editorElement))
                Marshal.ReleaseComObject(editorElement);
            if (automationObject is not null && Marshal.IsComObject(automationObject))
                Marshal.ReleaseComObject(automationObject);
        }
    }

    private static void FocusEditorOnStaThread(int expectedProcessId, nint hwnd)
    {
        Type? automationType = Type.GetTypeFromCLSID(CUIAutomationGuid);
        if (automationType is null)
            throw new InvalidOperationException("UI Automation COM type is unavailable.");

        object? automationObject = null;
        IUIAutomationElement? editorElement = null;
        try
        {
            automationObject = Activator.CreateInstance(automationType);
            if (automationObject is not IUIAutomation automation)
                throw new InvalidOperationException("Failed to create UI Automation object.");

            if (!TryResolveEditorElement(automation, hwnd, expectedProcessId, out editorElement, out var resolveFailure))
                throw new InvalidOperationException($"Unable to focus launched Notepad editor. {resolveFailure}");
            if (editorElement is null)
                throw new InvalidOperationException("Resolved Notepad editor was unexpectedly null.");

            int hr = editorElement.SetFocus();
            if (hr != 0)
                throw new InvalidOperationException($"UI Automation editor SetFocus failed with HRESULT {hr}.");
        }
        finally
        {
            if (editorElement is not null && Marshal.IsComObject(editorElement))
                Marshal.ReleaseComObject(editorElement);
            if (automationObject is not null && Marshal.IsComObject(automationObject))
                Marshal.ReleaseComObject(automationObject);
        }
    }

    private static void WriteFocusedTextOnStaThread(string text, int expectedProcessId)
    {
        Type? automationType = Type.GetTypeFromCLSID(CUIAutomationGuid);
        if (automationType is null)
            throw new InvalidOperationException("UI Automation COM type is unavailable.");

        object? automationObject = null;
        object? valuePatternObject = null;
        try
        {
            automationObject = Activator.CreateInstance(automationType);
            if (automationObject is not IUIAutomation automation)
                throw new InvalidOperationException("Failed to create UI Automation object.");

            int hr = automation.GetFocusedElement(out var focusedElement);
            if (hr != 0 || focusedElement is null)
                throw new InvalidOperationException($"UI Automation GetFocusedElement failed with HRESULT {hr}.");

            hr = focusedElement.GetCurrentPattern(UIA_ValuePatternId, out valuePatternObject);
            if (hr != 0 || valuePatternObject is not IUIAutomationValuePattern valuePattern)
                throw new InvalidOperationException($"Focused Notepad element does not expose ValuePattern; HRESULT {hr}.");

            EnsureFocusedElementBelongsToProcess(focusedElement, expectedProcessId);

            hr = valuePattern.get_CurrentIsReadOnly(out int isReadOnly);
            if (hr != 0 || isReadOnly != 0)
                throw new InvalidOperationException($"Focused Notepad element is read-only; HRESULT {hr}, readOnly={isReadOnly}.");

            hr = valuePattern.SetValue(text);
            if (hr != 0)
                throw new InvalidOperationException($"UI Automation ValuePattern.SetValue failed with HRESULT {hr}.");
        }
        finally
        {
            if (valuePatternObject is not null && Marshal.IsComObject(valuePatternObject))
                Marshal.ReleaseComObject(valuePatternObject);
            if (automationObject is not null && Marshal.IsComObject(automationObject))
                Marshal.ReleaseComObject(automationObject);
        }
    }

    private static void EnsureFocusedElementBelongsToProcess(IUIAutomationElement focusedElement, int expectedProcessId)
    {
        var focusedProcessId = GetElementProcessId(focusedElement);

        if (focusedProcessId != expectedProcessId)
        {
            throw new InvalidOperationException(
                $"Focused UI Automation element belongs to process {focusedProcessId}, expected Notepad process {expectedProcessId}.");
        }
    }

    private static int GetElementProcessId(IUIAutomationElement element)
    {
        int hr = element.GetCurrentPropertyValue(UIA_ProcessIdPropertyId, out object processValue);
        if (hr != 0)
            throw new InvalidOperationException($"UI Automation ProcessId lookup failed with HRESULT {hr}.");

        return processValue switch
        {
            int intValue => intValue,
            uint uintValue => unchecked((int)uintValue),
            _ => -1
        };
    }

    private static bool TryElementBelongsToProcess(IUIAutomationElement element, int expectedProcessId)
    {
        try
        {
            return GetElementProcessId(element) == expectedProcessId;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryResolveEditorElement(
        IUIAutomation automation,
        nint topLevelHwnd,
        int expectedProcessId,
        out IUIAutomationElement? editorElement,
        out string failureReason)
    {
        editorElement = null;
        var failures = new List<string>();

        foreach (var candidateHwnd in EnumerateCandidateWindowHandles(topLevelHwnd).Distinct())
        {
            int hr = automation.ElementFromHandle(candidateHwnd, out var rootElement);
            if (hr != 0 || rootElement is null)
            {
                failures.Add($"ElementFromHandle(0x{candidateHwnd.ToInt64():X}) hr={hr}");
                continue;
            }

            try
            {
                if (TryElementHasWritableValuePattern(rootElement, expectedProcessId, out _))
                {
                    editorElement = rootElement;
                    failureReason = string.Empty;
                    return true;
                }

                if (TryFindWritableValueDescendant(
                        automation,
                        rootElement,
                        expectedProcessId,
                        out editorElement,
                        out var descendantFailure))
                {
                    failureReason = string.Empty;
                    return true;
                }

                failures.Add($"hwnd=0x{candidateHwnd.ToInt64():X}: {descendantFailure}");
            }
            finally
            {
                if (!ReferenceEquals(editorElement, rootElement) && Marshal.IsComObject(rootElement))
                    Marshal.ReleaseComObject(rootElement);
            }
        }

        failureReason = failures.Count == 0
            ? "No candidate Notepad window handles were available."
            : string.Join("; ", failures.Take(6));
        return false;
    }

    private static bool TryFindWritableValueDescendant(
        IUIAutomation automation,
        IUIAutomationElement rootElement,
        int expectedProcessId,
        out IUIAutomationElement? editorElement,
        out string failureReason)
    {
        editorElement = null;
        failureReason = "no writable ValuePattern descendant";
        object? valueCondition = null;
        try
        {
            int hr = automation.CreatePropertyCondition(
                UIA_IsValuePatternAvailablePropertyId,
                true,
                out valueCondition);
            if (hr != 0 || valueCondition is null)
            {
                failureReason = $"CreatePropertyCondition(ValuePatternAvailable) hr={hr}";
                return false;
            }

            hr = rootElement.FindFirst(TreeScopeDescendants, valueCondition, out var foundElement);
            if (hr != 0 || foundElement is null)
            {
                failureReason = $"FindFirst(ValuePattern descendant) hr={hr}";
                return false;
            }

            if (TryElementHasWritableValuePattern(foundElement, expectedProcessId, out failureReason))
            {
                editorElement = foundElement;
                return true;
            }

            if (Marshal.IsComObject(foundElement))
                Marshal.ReleaseComObject(foundElement);
            return false;
        }
        finally
        {
            if (valueCondition is not null && Marshal.IsComObject(valueCondition))
                Marshal.ReleaseComObject(valueCondition);
        }
    }

    private static bool TryElementHasWritableValuePattern(
        IUIAutomationElement element,
        int expectedProcessId,
        out string failureReason)
    {
        failureReason = string.Empty;
        object? valuePatternObject = null;
        try
        {
            if (!TryElementBelongsToProcess(element, expectedProcessId))
            {
                failureReason = "candidate belongs to a different process";
                return false;
            }

            int hr = element.GetCurrentPattern(UIA_ValuePatternId, out valuePatternObject);
            if (hr != 0 || valuePatternObject is not IUIAutomationValuePattern valuePattern)
            {
                failureReason = $"candidate lacks ValuePattern hr={hr}";
                return false;
            }

            hr = valuePattern.get_CurrentIsReadOnly(out int isReadOnly);
            if (hr != 0 || isReadOnly != 0)
            {
                failureReason = $"candidate ValuePattern readOnly={isReadOnly} hr={hr}";
                return false;
            }

            return true;
        }
        finally
        {
            if (valuePatternObject is not null && Marshal.IsComObject(valuePatternObject))
                Marshal.ReleaseComObject(valuePatternObject);
        }
    }

    private static bool TryReadValueFromWindowHandle(
        IUIAutomation automation,
        nint hwnd,
        int expectedProcessId,
        out string value)
    {
        value = string.Empty;
        int hr = automation.ElementFromHandle(hwnd, out var element);
        try
        {
            return hr == 0 &&
                   element is not null &&
                   TryReadValueFromElement(element, expectedProcessId, out value, out _);
        }
        finally
        {
            if (element is not null && Marshal.IsComObject(element))
                Marshal.ReleaseComObject(element);
        }
    }

    private static bool TryReadValueFromElement(
        IUIAutomationElement element,
        int expectedProcessId,
        out string value,
        out int hresult)
    {
        value = string.Empty;
        hresult = 0;
        object? valuePatternObject = null;
        try
        {
            if (!TryElementBelongsToProcess(element, expectedProcessId))
                return false;

            hresult = element.GetCurrentPattern(UIA_ValuePatternId, out valuePatternObject);
            if (hresult != 0 || valuePatternObject is not IUIAutomationValuePattern valuePattern)
                return false;

            hresult = valuePattern.get_CurrentValue(out string? currentValue);
            if (hresult != 0)
                return false;

            value = currentValue ?? string.Empty;
            return true;
        }
        finally
        {
            if (valuePatternObject is not null && Marshal.IsComObject(valuePatternObject))
                Marshal.ReleaseComObject(valuePatternObject);
        }
    }

    private static IReadOnlyList<nint> EnumerateCandidateWindowHandles(nint topLevelHwnd)
    {
        var handles = new List<nint>();
        if (topLevelHwnd == 0)
            return handles;

        handles.Add(topLevelHwnd);
        EnumWindowsProc callback = (childHwnd, _) =>
        {
            handles.Add(childHwnd);
            return true;
        };
        EnumChildWindows(topLevelHwnd, callback, 0);
        return handles;
    }

    private void EnsureForegroundWindowBelongsToLaunchedNotepad(string operation)
    {
        var process = NotepadProcess;
        if (!IsForegroundOwnedByProcess(process.Id))
        {
            var foreground = GetForegroundWindow();
            _ = GetWindowThreadProcessId(foreground, out uint foregroundProcessId);
            throw new InvalidOperationException(
                $"Cannot continue {operation}: foreground process {foregroundProcessId} is not launched Notepad process {process.Id}.");
        }
    }

    private static bool IsForegroundOwnedByProcess(int expectedProcessId)
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0)
            return false;

        _ = GetWindowThreadProcessId(foreground, out uint foregroundProcessId);
        return foregroundProcessId == (uint)expectedProcessId;
    }

    private static void TrySetWindowFocusWithUia(nint hwnd, int expectedProcessId)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            object? automationObject = null;
            IUIAutomationElement? editorElement = null;
            IUIAutomationElement? windowElement = null;
            try
            {
                Type? automationType = Type.GetTypeFromCLSID(CUIAutomationGuid);
                if (automationType is null)
                    return;

                automationObject = Activator.CreateInstance(automationType);
                if (automationObject is not IUIAutomation automation)
                    return;

                if (TryResolveEditorElement(automation, hwnd, expectedProcessId, out editorElement, out _))
                {
                    editorElement?.SetFocus();
                    return;
                }

                int hr = automation.ElementFromHandle(hwnd, out windowElement);
                if (hr == 0)
                    windowElement?.SetFocus();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                if (editorElement is not null && Marshal.IsComObject(editorElement))
                    Marshal.ReleaseComObject(editorElement);
                if (windowElement is not null && Marshal.IsComObject(windowElement))
                    Marshal.ReleaseComObject(windowElement);
                if (automationObject is not null && Marshal.IsComObject(automationObject))
                    Marshal.ReleaseComObject(automationObject);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name = "Hush.NotepadE2E.UIAFocus";
        thread.Start();
        thread.Join();

        if (exception is not null)
            throw exception;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            var process = _process;
            if (process is not null && !process.HasExited)
                await TryCloseActiveTestTabWithoutSavingAsync();
        }
        finally
        {
            _process?.Dispose();
            try
            {
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
            }
            catch
            {
                // Best-effort cleanup of a temp file owned by this test.
            }

            NotepadPersistedStateCleanup.DeleteHushOwnedPersistedState();
        }
    }

    private async Task<bool> TryCloseActiveTestTabWithoutSavingAsync()
    {
        var fileName = Path.GetFileName(FilePath);
        var process = NotepadProcess;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (process.HasExited)
                return true;

            try
            {
                Focus();

                process.Refresh();
                if (!process.MainWindowTitle.Contains(fileName, StringComparison.OrdinalIgnoreCase))
                    return true;

                SendCtrlChord(VK_W);
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            await Task.Delay(300);

            if (await WaitUntilActiveTabIsNotThisFileAsync(fileName, TimeSpan.FromMilliseconds(600)))
                return true;

            try
            {
                SendKey(VK_N);
                await Task.Delay(100);
                SendKey(VK_RETURN);
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            if (await WaitUntilActiveTabIsNotThisFileAsync(fileName, TimeSpan.FromSeconds(2)))
                return true;
        }

        return process.HasExited;
    }

    private async Task<bool> WaitUntilActiveTabIsNotThisFileAsync(string fileName, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var process = NotepadProcess;
            if (process.HasExited)
                return true;

            process.Refresh();
            if (!process.MainWindowTitle.Contains(fileName, StringComparison.OrdinalIgnoreCase))
                return true;

            await Task.Delay(100);
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(nint hWnd, bool turnOn);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("user32.dll")]
    private static extern nint GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint hMem);

    private static void SendCtrlChord(ushort key)
    {
        INPUT[] inputs =
        [
            MakeKey(VK_CONTROL, 0),
            MakeKey(key, 0),
            MakeKey(key, KEYEVENTF_KEYUP),
            MakeKey(VK_CONTROL, KEYEVENTF_KEYUP),
        ];

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"SendInput failed while reading Notepad text (sent {sent}/{inputs.Length}, Win32={error}).");
        }
    }

    private static void SendKey(ushort key)
    {
        INPUT[] inputs =
        [
            MakeKey(key, 0),
            MakeKey(key, KEYEVENTF_KEYUP),
        ];

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"SendInput failed while sending key {key} (sent {sent}/{inputs.Length}, Win32={error}).");
        }
    }

    private static void SendAltKeyPress()
    {
        INPUT[] inputs =
        [
            MakeKey(VK_MENU, 0),
            MakeKey(VK_MENU, KEYEVENTF_KEYUP),
        ];

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
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
                Time = 0,
                ExtraInfo = 0,
            },
        },
    };

    private static string? TryGetClipboardText()
    {
        for (int attempt = 0; attempt < 25; attempt++)
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

            Thread.Sleep(40);
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

        for (int attempt = 0; attempt < 25; attempt++)
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

            Thread.Sleep(40);
        }

        GlobalFree(hGlobal);
        return false;
    }

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

        [PreserveSig]
        int GetRootElementBuildCache(
            [MarshalAs(UnmanagedType.Interface)] object cacheRequest,
            [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? root);

        [PreserveSig]
        int ElementFromHandleBuildCache(
            nint hwnd,
            [MarshalAs(UnmanagedType.Interface)] object cacheRequest,
            [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? element);

        [PreserveSig]
        int ElementFromPointBuildCache(
            UiaPoint pt,
            [MarshalAs(UnmanagedType.Interface)] object cacheRequest,
            [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? element);

        [PreserveSig]
        int GetFocusedElementBuildCache(
            [MarshalAs(UnmanagedType.Interface)] object cacheRequest,
            [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement? element);

        [PreserveSig]
        int CreateTreeWalker(
            [MarshalAs(UnmanagedType.Interface)] object condition,
            [MarshalAs(UnmanagedType.Interface)] out object treeWalker);

        [PreserveSig]
        int get_ControlViewWalker([MarshalAs(UnmanagedType.Interface)] out object treeWalker);

        [PreserveSig]
        int get_ContentViewWalker([MarshalAs(UnmanagedType.Interface)] out object treeWalker);

        [PreserveSig]
        int get_RawViewWalker([MarshalAs(UnmanagedType.Interface)] out object treeWalker);

        [PreserveSig]
        int get_RawViewCondition([MarshalAs(UnmanagedType.Interface)] out object condition);

        [PreserveSig]
        int get_ControlViewCondition([MarshalAs(UnmanagedType.Interface)] out object condition);

        [PreserveSig]
        int get_ContentViewCondition([MarshalAs(UnmanagedType.Interface)] out object condition);

        [PreserveSig]
        int CreateCacheRequest([MarshalAs(UnmanagedType.Interface)] out object cacheRequest);

        [PreserveSig]
        int CreateTrueCondition([MarshalAs(UnmanagedType.Interface)] out object condition);

        [PreserveSig]
        int CreateFalseCondition([MarshalAs(UnmanagedType.Interface)] out object condition);

        [PreserveSig]
        int CreatePropertyCondition(
            int propertyId,
            [MarshalAs(UnmanagedType.Struct)] object value,
            [MarshalAs(UnmanagedType.Interface)] out object condition);
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
