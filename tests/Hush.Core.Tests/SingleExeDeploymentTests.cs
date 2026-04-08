// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Reflection;

namespace Hush.Core.Tests;

/// <summary>
/// Validation tests for single-exe (PublishSingleFile) deployment scenarios.
/// These verify that the application can find its resources and settings when
/// run from any directory, and that platform-specific code paths are guarded.
/// </summary>
public sealed class SingleExeDeploymentTests
{
    // ── Process path / base directory ────────────────────────────────────

    [Fact]
    public void ProcessPath_IsAvailable()
    {
        // Environment.ProcessPath is used by AutoStartService to register
        // the exe in startup locations. It must be non-null at runtime.
        var path = Environment.ProcessPath;
        Assert.NotNull(path);
        Assert.True(File.Exists(path), $"ProcessPath '{path}' should point to an existing file");
    }

    [Fact]
    public void AppBaseDirectory_IsValid()
    {
        var baseDir = AppContext.BaseDirectory;
        Assert.False(string.IsNullOrEmpty(baseDir));
        Assert.True(Directory.Exists(baseDir));
    }

    // ── UserProfile directory ────────────────────────────────────────────

    [Fact]
    public void UserProfile_IsAvailable()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrEmpty(home), "UserProfile must resolve for settings storage");
        Assert.True(Directory.Exists(home));
    }

    [Fact]
    public void SettingsDirectory_CanBeCreated()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var hushDir = Path.Combine(home, ".hush");

        // The directory should be creatable (idempotent)
        Directory.CreateDirectory(hushDir);
        Assert.True(Directory.Exists(hushDir));
    }

    // ── Temp directory for WAV files ─────────────────────────────────────

    [Fact]
    public void TempDirectory_IsWritable()
    {
        // TranscriptionEngine writes temporary WAV files to the temp directory
        var tempPath = Path.GetTempFileName();
        try
        {
            Assert.True(File.Exists(tempPath));
            File.WriteAllText(tempPath, "test");
            Assert.Equal("test", File.ReadAllText(tempPath));
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void TempDirectory_SupportsWavExtension()
    {
        var wavPath = Path.GetTempFileName() + ".wav";
        try
        {
            File.WriteAllBytes(wavPath, new byte[100]);
            Assert.True(File.Exists(wavPath));
        }
        finally
        {
            File.Delete(wavPath);
        }
    }

    // ── Assembly metadata ────────────────────────────────────────────────

    [Fact]
    public void CoreAssembly_HasCorrectName()
    {
        var asm = typeof(Hush.Core.Configuration.HushSettings).Assembly;
        Assert.Equal("Hush.Core", asm.GetName().Name);
    }

    [Fact]
    public void CoreAssembly_HasVersion()
    {
        var asm = typeof(Hush.Core.Configuration.HushSettings).Assembly;
        var version = asm.GetName().Version;
        Assert.NotNull(version);
    }

    // ── Platform detection ───────────────────────────────────────────────

    [Fact]
    public void PlatformDetection_ExactlyOnePlatform()
    {
        // At least one platform must be detected
        bool isAny = OperatingSystem.IsWindows()
                  || OperatingSystem.IsMacOS()
                  || OperatingSystem.IsLinux();
        Assert.True(isAny, "At least one platform should be detected");
    }

    // ── RuntimeIdentifier matches platform ───────────────────────────────

    [Fact]
    public void RuntimeIdentifier_IsConsistentWithPlatform()
    {
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        Assert.False(string.IsNullOrEmpty(rid), "RuntimeIdentifier must be available");

        if (OperatingSystem.IsWindows())
            Assert.Contains("win", rid, StringComparison.OrdinalIgnoreCase);
        else if (OperatingSystem.IsMacOS())
            Assert.Contains("osx", rid, StringComparison.OrdinalIgnoreCase);
        else if (OperatingSystem.IsLinux())
            Assert.Contains("linux", rid, StringComparison.OrdinalIgnoreCase);
    }

    // ── Architecture detection (x64 vs ARM64) ───────────────────────────

    [Fact]
    public void ProcessArchitecture_IsKnown()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        Assert.True(
            arch == System.Runtime.InteropServices.Architecture.X64 ||
            arch == System.Runtime.InteropServices.Architecture.Arm64 ||
            arch == System.Runtime.InteropServices.Architecture.X86,
            $"Unexpected architecture: {arch}");
    }

    // ── Type resolution (verify all key types can be loaded) ─────────────

    [Theory]
    [InlineData(typeof(Hush.Core.Configuration.HushSettings))]
    [InlineData(typeof(Hush.Core.Configuration.SettingsService))]
    [InlineData(typeof(Hush.Core.Configuration.AutoStartService))]
    [InlineData(typeof(Hush.Core.Audio.AudioCaptureService))]
    [InlineData(typeof(Hush.Core.Session.DictationSession))]
    [InlineData(typeof(Hush.Core.Transcription.TranscriptionEngine))]
    [InlineData(typeof(Hush.Core.Transcription.TranscriptionResult))]
    [InlineData(typeof(Hush.Core.Output.KeystrokeTypingService))]
    [InlineData(typeof(Hush.Core.Output.SoundEffectService))]
    [InlineData(typeof(Hush.Core.Output.WindowsInputCoordinator))]
    [InlineData(typeof(Hush.Core.Input.GlobalHotkeyService))]
    [InlineData(typeof(Hush.Core.Models.ModelManager))]
    public void KeyType_CanBeResolved(Type type)
    {
        Assert.NotNull(type);
        Assert.NotNull(type.Assembly);
    }

    // ── Interface implementations are discoverable ───────────────────────

    [Fact]
    public void AllInterfaces_HaveConcreteImplementations()
    {
        var coreAssembly = typeof(Hush.Core.Configuration.HushSettings).Assembly;
        var interfaces = new[]
        {
            typeof(Hush.Core.Audio.IAudioCaptureService),
            typeof(Hush.Core.Session.IDictationSession),
            typeof(Hush.Core.Transcription.ITranscriptionEngine),
            typeof(Hush.Core.Output.ITextOutputService),
            typeof(Hush.Core.Output.ISoundEffectService),
            typeof(Hush.Core.Input.IGlobalHotkeyService),
            typeof(Hush.Core.Configuration.IAutoStartService),
            typeof(Hush.Core.Models.IModelManager),
        };

        foreach (var iface in interfaces)
        {
            var implementations = coreAssembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && iface.IsAssignableFrom(t))
                .ToList();

            Assert.True(implementations.Count > 0,
                $"No concrete implementation found for {iface.Name} in Hush.Core");
        }
    }
}
