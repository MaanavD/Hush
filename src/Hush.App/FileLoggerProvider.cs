// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace Hush.App;

/// <summary>
/// Minimal file-based logger provider that writes structured log entries to
/// <c>~/.hush/hush.log</c>. Rotates when the file exceeds 5 MB.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly object _lock = new();
    private const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB

    public FileLoggerProvider(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(_path, categoryName, _lock);

    public void Dispose() { }

    private sealed class FileLogger(string path, string category, object fileLock) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {category}: {formatter(state, exception)}";
            if (exception is not null)
                line += Environment.NewLine + exception;

            lock (fileLock)
            {
                try
                {
                    // Rotate if oversized
                    if (File.Exists(path))
                    {
                        var info = new FileInfo(path);
                        if (info.Length > MaxFileSizeBytes)
                        {
                            var rotated = path + ".1";
                            File.Move(path, rotated, overwrite: true);
                        }
                    }

                    File.AppendAllText(path, line + Environment.NewLine);
                }
                catch
                {
                    // Best-effort; never crash the app because of logging.
                }
            }
        }
    }
}
