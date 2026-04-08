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

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance is already running.
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

        // Also register a DLL import resolver for assemblies that P/Invoke "onnxruntime"
        // so that late-bound loads also resolve from our directory.
        NativeLibrary.SetDllImportResolver(
            typeof(Microsoft.ML.OnnxRuntime.OrtEnv).Assembly,
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
}
