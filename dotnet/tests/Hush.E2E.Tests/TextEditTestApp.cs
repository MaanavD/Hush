using System.Diagnostics;
using System.Runtime.Versioning;

namespace Hush.E2E.Tests;

[SupportedOSPlatform("macos")]
internal sealed class TextEditTestApp : IAsyncDisposable
{
    private const string FileNamePrefix = "HushTextEditE2E_";
    private static readonly TimeSpan DocumentOpenTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StableTextPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly TextEditPreferenceScope _preferenceScope;
    private readonly string _documentName;
    private bool _disposed;

    private TextEditTestApp(string filePath, TextEditPreferenceScope preferenceScope)
    {
        FilePath = filePath;
        _documentName = Path.GetFileName(filePath);
        _preferenceScope = preferenceScope;
    }

    public string FilePath { get; }

    public static async Task<TextEditTestApp> LaunchAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("TextEdit E2E tests require macOS.");

        var preferenceScope = await TextEditPreferenceScope.ApplyAsync(cancellationToken);
        var filePath = Path.Combine(Path.GetTempPath(), $"{FileNamePrefix}{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(filePath, string.Empty, cancellationToken);

        try
        {
            await RunProcessAsync(
                "open",
                ["-a", "TextEdit", filePath],
                cancellationToken);

            var app = new TextEditTestApp(filePath, preferenceScope);
            await app.WaitForDocumentAsync(cancellationToken);
            await app.FocusAsync(cancellationToken);
            return app;
        }
        catch
        {
            await preferenceScope.DisposeAsync();
            try { File.Delete(filePath); } catch { }
            throw;
        }
    }

    public void Focus()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("TextEdit focus requires macOS.");

        RunAppleScript(
            """
            on run argv
                tell application "TextEdit"
                    activate
                end tell
            end run
            """,
            []);
    }

    public Task FocusAsync(CancellationToken cancellationToken) =>
        RunAppleScriptAsync(
            """
            on run argv
                tell application "TextEdit"
                    activate
                end tell
            end run
            """,
            [],
            cancellationToken);

    public async Task<string> ReadTextAsync(CancellationToken cancellationToken)
    {
        return await RunAppleScriptAsync(
            """
            on run argv
                set targetName to item 1 of argv
                tell application "TextEdit"
                    if not (exists document targetName) then error "TextEdit document not found: " & targetName
                    return text of document targetName
                end tell
            end run
            """,
            [_documentName],
            cancellationToken);
    }

    public Task SetTextAsync(string text, CancellationToken cancellationToken) =>
        RunAppleScriptAsync(
            """
            on run argv
                set targetName to item 1 of argv
                set targetText to item 2 of argv
                tell application "TextEdit"
                    if not (exists document targetName) then error "TextEdit document not found: " & targetName
                    set text of document targetName to targetText
                end tell
            end run
            """,
            [_documentName, text],
            cancellationToken);

    public async Task<string> WaitForStableTextAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        string previous = await ReadTextAsync(timeoutCts.Token);
        while (true)
        {
            await Task.Delay(StableTextPollInterval, timeoutCts.Token);
            var current = await ReadTextAsync(timeoutCts.Token);
            if (string.Equals(current, previous, StringComparison.Ordinal))
                return current;

            previous = current;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            await RunAppleScriptAsync(
                """
                on run argv
                    set targetName to item 1 of argv
                    tell application "TextEdit"
                        if exists document targetName then close document targetName saving no
                    end tell
                end run
                """,
                [_documentName],
                CancellationToken.None);
        }
        catch
        {
            // Best-effort test cleanup.
        }

        try { File.Delete(FilePath); } catch { }
        await _preferenceScope.DisposeAsync();
    }

    private async Task WaitForDocumentAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + DocumentOpenTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await RunAppleScriptAsync(
                """
                on run argv
                    set targetName to item 1 of argv
                    tell application "TextEdit"
                        activate
                        if exists document targetName then return "1"
                    end tell
                    return "0"
                end run
                """,
                [_documentName],
                cancellationToken);
            if (string.Equals(result.Trim(), "1", StringComparison.Ordinal))
                return;

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for TextEdit to open '{_documentName}'.");
    }

    private static string RunAppleScript(string script, IReadOnlyList<string> arguments)
    {
        var task = RunAppleScriptAsync(script, arguments, CancellationToken.None);
        return task.GetAwaiter().GetResult();
    }

    private static Task<string> RunAppleScriptAsync(
        string script,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        RunProcessAsync("osascript", BuildAppleScriptArguments(script, arguments), cancellationToken);

    private static string[] BuildAppleScriptArguments(string script, IReadOnlyList<string> arguments)
    {
        var processArguments = new List<string> { "-e", script, "--" };
        processArguments.AddRange(arguments);
        return processArguments.ToArray();
    }

    private static async Task<string> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start '{fileName}'.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"'{fileName}' exited with code {process.ExitCode}: {stderr.Trim()}");

        return stdout.TrimEnd('\r', '\n');
    }

    private sealed class TextEditPreferenceScope : IAsyncDisposable
    {
        private readonly string? _previousRichTextValue;
        private bool _disposed;

        private TextEditPreferenceScope(string? previousRichTextValue)
            => _previousRichTextValue = previousRichTextValue;

        public static async Task<TextEditPreferenceScope> ApplyAsync(CancellationToken cancellationToken)
        {
            var previous = await ReadDefaultAsync("com.apple.TextEdit", "RichText", cancellationToken);
            await WriteDefaultAsync("com.apple.TextEdit", "RichText", "false", cancellationToken);
            return new TextEditPreferenceScope(previous);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                if (_previousRichTextValue is null)
                    await DeleteDefaultAsync("com.apple.TextEdit", "RichText", CancellationToken.None);
                else
                    await WriteDefaultAsync("com.apple.TextEdit", "RichText", IsTruthyDefault(_previousRichTextValue) ? "true" : "false", CancellationToken.None);
            }
            catch
            {
                // Best-effort preference restoration.
            }
        }

        private static async Task<string?> ReadDefaultAsync(string domain, string key, CancellationToken cancellationToken)
        {
            try
            {
                return await RunProcessAsync("defaults", ["read", domain, key], cancellationToken);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static Task WriteDefaultAsync(string domain, string key, string value, CancellationToken cancellationToken) =>
            RunProcessAsync("defaults", ["write", domain, key, "-bool", value], cancellationToken);

        private static Task DeleteDefaultAsync(string domain, string key, CancellationToken cancellationToken) =>
            RunProcessAsync("defaults", ["delete", domain, key], cancellationToken);

        private static bool IsTruthyDefault(string value) =>
            value.Trim() is "1" or "true" or "TRUE" or "YES" or "yes";
    }
}
