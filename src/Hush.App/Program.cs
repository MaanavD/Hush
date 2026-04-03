using System.Threading;
using Avalonia;
using Avalonia.ReactiveUI;

namespace Hush.App;

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
}
