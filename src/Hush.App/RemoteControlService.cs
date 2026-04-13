// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.IO.Pipes;
using System.Net.Sockets;
using Hush.App.ViewModels;

namespace Hush.App;

/// <summary>
/// Listens on a named pipe / Unix socket and dispatches remote-control
/// commands to the running Hush instance from external processes (CLI flags,
/// Wayland hotkey daemons, stream decks, etc.).
/// </summary>
/// <remarks>
/// IPC format: single-line text command terminated by a newline.
/// Supported commands: <c>toggle</c>, <c>toggle-clean</c>, <c>cancel</c>, <c>copy-last</c>.
/// </remarks>
public sealed class RemoteControlService : IAsyncDisposable
{
    /// <summary>Pipe name used on Windows (<c>\\.\pipe\HushRemoteControl</c>).</summary>
    internal const string WindowsPipeName = "HushRemoteControl";

    /// <summary>
    /// Unix socket path used on macOS/Linux.
    /// Prefers <c>$XDG_RUNTIME_DIR</c> (per-user, managed by systemd-logind on Linux)
    /// so that other users on the same machine cannot send commands to this instance.
    /// Falls back to <c>~/.local/share/hush/</c> on systems without XDG_RUNTIME_DIR.
    /// </summary>
    internal static string UnixSocketPath => GetUnixSocketPath();

    private static string GetUnixSocketPath()
    {
        var xdgRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(xdgRuntime))
            return Path.Combine(xdgRuntime, "hush-remote.sock");

        // Fallback: per-user directory under home (still safer than /tmp).
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "hush");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "hush-remote.sock");
    }

    private readonly MainViewModel _mainVm;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;

    public RemoteControlService(MainViewModel mainVm)
        => _mainVm = mainVm;

    /// <summary>
    /// Starts the background pipe/socket listener. Returns immediately;
    /// the listener runs on a background task.
    /// </summary>
    public Task StartListeningAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listenerTask = Task.Run(() => ListenAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                await ListenWindowsAsync(ct).ConfigureAwait(false);
            else
                await ListenUnixAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Hush] RemoteControl listener error: {ex.Message}");
        }
    }

    private async Task ListenWindowsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(
                WindowsPipeName,
                PipeDirection.In,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

            try
            {
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is not null)
                    Dispatch(line.Trim());
            }
            catch { /* client disconnected or bad data — continue looping */ }
        }
    }

    private async Task ListenUnixAsync(CancellationToken ct)
    {
        var socketPath = UnixSocketPath;

        // Remove a stale socket from a previous crash.
        // Using a per-user directory (XDG_RUNTIME_DIR or ~/.local/share/hush)
        // means we own any file there, so deletion is safe.
        if (File.Exists(socketPath))
            File.Delete(socketPath);

        using var serverSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        serverSocket.Bind(new UnixDomainSocketEndPoint(socketPath));

        // Restrict access to the current user only (rwx------).
        // Supported on Linux and macOS; no-op on other platforms.
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                File.SetUnixFileMode(socketPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch { /* best effort — not all file systems support Unix permissions */ }
        }

        serverSocket.Listen(8);

        ct.Register(() => serverSocket.Close());

        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try { client = await serverSocket.AcceptAsync(ct).ConfigureAwait(false); }
            catch { break; }

            _ = Task.Run(async () =>
            {
                try
                {
                    using (client)
                    {
                        using var ns = new NetworkStream(client);
                        using var reader = new StreamReader(ns);
                        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                        if (line is not null)
                            Dispatch(line.Trim());
                    }
                }
                catch { /* ignore */ }
            }, ct);
        }
    }

    private void Dispatch(string command)
    {
        switch (command.ToLowerInvariant())
        {
            case "toggle":       Toggle(); break;
            case "toggle-clean": ToggleClean(); break;
            case "cancel":       Cancel(); break;
            case "copy-last":    CopyLast(); break;
            default:
                Console.Error.WriteLine($"[Hush] Unknown remote command: '{command}'");
                break;
        }
    }

    /// <summary>Toggles the raw dictation session (mirrors OnHotkeyPressed / OnHotkeyReleased).</summary>
    public void Toggle()
    {
        if (_mainVm.IsListening)
            _mainVm.TriggerHotkeyReleased();
        else
            _mainVm.TriggerHotkeyPressed();
    }

    /// <summary>Toggles the clean-mode dictation session (mirrors OnCleanHotkeyPressed / OnCleanHotkeyReleased).</summary>
    public void ToggleClean()
    {
        if (_mainVm.IsListening)
            _mainVm.TriggerCleanHotkeyReleased();
        else
            _mainVm.TriggerCleanHotkeyPressed();
    }

    /// <summary>Stops the active session if any.</summary>
    public void Cancel() => _mainVm.CancelSession();

    /// <summary>Copies the last transcript text to the system clipboard.</summary>
    public void CopyLast()
    {
        var text = _mainVm.LastTranscript;
        if (string.IsNullOrEmpty(text)) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var lifetime = Avalonia.Application.Current?.ApplicationLifetime
                    as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                var window = lifetime?.Windows.FirstOrDefault();
                if (window is not null)
                {
                    var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(window)?.Clipboard;
                    if (clipboard is not null)
                        await clipboard.SetTextAsync(text);
                }
            }
            catch { /* best effort */ }
        });
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_listenerTask is not null)
        {
            try { await _listenerTask.ConfigureAwait(false); }
            catch { /* ignore */ }
        }
        _cts?.Dispose();

        if (!OperatingSystem.IsWindows() && File.Exists(UnixSocketPath))
        {
            try { File.Delete(UnixSocketPath); }
            catch { /* best effort */ }
        }    }
}
