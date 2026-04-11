// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.ReactiveUI;

namespace Hush.App;

/// <summary>Application bootstrap: enforces single-instance via a named mutex, pre-loads bundled ONNX Runtime native libraries, and launches the Avalonia desktop lifetime.</summary>
internal static class Program
{
    private const string MutexName = "Global\\Hush_SingleInstance_B8F2A1D0";

    // Avalonia configuration; don't remove or modify.
    [STAThread]
    public static void Main(string[] args)
    {
        // Required for PublishSingleFile: the Windows App Runtime needs to know
        // where native DLLs were extracted to.
        Environment.SetEnvironmentVariable(
            "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY",
            AppContext.BaseDirectory);

        // Ensure our bundled onnxruntime.dll is loaded instead of the Windows
        // inbox copy (System32\onnxruntime.dll is ORT 1.17; the SDK needs 1.24).
        // Must run before any ORT type is touched.
        PreloadOnnxRuntime();

        // Parse CLI flags before acquiring the mutex so we can send commands
        // to an already-running instance if one is present.
        string? remoteCommand = ParseRemoteCommand(args);

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);

        if (remoteCommand is not null)
        {
            if (!createdNew)
            {
                // Another instance is running — send it the command via IPC.
                bool sent = TrySendRemoteCommand(remoteCommand);
                if (!sent)
                    Console.Error.WriteLine("[Hush] Warning: Could not send command to running instance.");
                return; // exit 0 regardless; failures are best-effort
            }
            else
            {
                // No running instance to receive the command.
                Console.Error.WriteLine("Hush is not running.");
                mutex.ReleaseMutex();
                Environment.Exit(1);
                return;
            }
        }

        if (!createdNew)
        {
            // Another instance is already running and no remote command was given.
            Console.Error.WriteLine("Hush is already running.");
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseReactiveUI()
            .LogToTrace();

    /// <summary>
    /// Maps a CLI flag to the IPC command string, or returns <see langword="null"/>
    /// if no recognised flag is present.
    /// </summary>
    private static string? ParseRemoteCommand(string[] args)
    {
        foreach (var arg in args)
        {
            switch (arg.ToLowerInvariant())
            {
                case "--toggle":       return "toggle";
                case "--toggle-clean": return "toggle-clean";
                case "--cancel":       return "cancel";
                case "--copy-last":    return "copy-last";
            }
        }
        return null;
    }

    /// <summary>
    /// Sends a command string to the running Hush instance via named pipe (Windows)
    /// or Unix domain socket (macOS/Linux). Returns <see langword="true"/> on success.
    /// </summary>
    private static bool TrySendRemoteCommand(string command)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var client = new System.IO.Pipes.NamedPipeClientStream(
                    ".", RemoteControlService.WindowsPipeName,
                    System.IO.Pipes.PipeDirection.Out,
                    System.IO.Pipes.PipeOptions.None);
                client.Connect(2000);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(command);
            }
            else
            {
                using var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.Unix,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Unspecified);
                socket.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(RemoteControlService.UnixSocketPath));
                using var ns = new System.Net.Sockets.NetworkStream(socket);
                using var writer = new StreamWriter(ns) { AutoFlush = true };
                writer.WriteLine(command);
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Hush] Warning: failed to send remote command '{command}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Pre-load <c>onnxruntime.dll</c> from the app's native library directory
    /// so that the Windows DLL loader doesn't pick up the inbox System32 copy
    /// (which is ORT 1.17 and lacks APIs the SDK's managed wrapper requires).
    /// For single-file apps, native DLLs are extracted to a temp directory
    /// separate from <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    private static void PreloadOnnxRuntime()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Search directories where the native libs may live:
        // 1. AppContext.BaseDirectory — works for non-single-file and dotnet run
        // 2. Single-file extraction dir — native DLLs are extracted here at launch
        //    (found via DOTNET_BUNDLE_EXTRACT_BASE_DIR or the default temp path)
        var searchDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AppContext.BaseDirectory
        };

        // For single-file self-extracting apps, native DLLs land in a temp folder.
        // Find it by probing known extraction paths.
        var extractBase = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
        if (string.IsNullOrEmpty(extractBase))
            extractBase = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp", ".net", "Hush.App");

        if (Directory.Exists(extractBase))
        {
            // The extraction directory has a hash-named subdirectory
            var subDirs = Directory.GetDirectories(extractBase);
            foreach (var sub in subDirs)
            {
                if (File.Exists(Path.Combine(sub, "onnxruntime.dll")))
                {
                    searchDirs.Add(sub);
                    break;
                }
            }
        }

        string[] nativeLibs = ["onnxruntime", "onnxruntime-genai", "onnxruntime_providers_shared"];

        foreach (string dir in searchDirs)
        {
            foreach (string lib in nativeLibs)
            {
                string path = Path.Combine(dir, $"{lib}.dll");
                if (File.Exists(path))
                    NativeLibrary.Load(path);
            }
        }

        // Also register a DLL import resolver for the OnnxRuntime managed assembly so
        // that late-bound P/Invoke calls also resolve from our directory.
        // Resolve the assembly by name at runtime to avoid a compile-time package
        // reference on Microsoft.ML.OnnxRuntime in Hush.App.csproj.
        try
        {
            var ort = System.Reflection.Assembly.Load("Microsoft.ML.OnnxRuntime");
            NativeLibrary.SetDllImportResolver(
                ort,
                (libraryName, assembly, searchPath) =>
                {
                    foreach (string dir in searchDirs)
                    {
                        string candidate = Path.Combine(dir, $"{libraryName}.dll");
                        if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                            return handle;
                    }
                    return IntPtr.Zero;
                });
        }
        catch (Exception)
        {
            // OnnxRuntime assembly not loaded yet — the eager pre-load above is sufficient.
        }
    }
}
