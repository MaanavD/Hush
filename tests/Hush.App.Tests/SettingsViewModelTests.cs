// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.App.ViewModels;
using Hush.Core.Configuration;

namespace Hush.App.Tests;

/// <summary>
/// Tests for <see cref="SettingsViewModel"/> ensuring proper two-way binding
/// between the VM and the backing <see cref="HushSettings"/> object.
/// </summary>
public sealed class SettingsViewModelTests
{
    [Fact]
    public void Constructor_PopulatesFromSettings()
    {
        var settings = new HushSettings
        {
            Hotkey = "Alt+Space",
            Language = "ja",
            TranscriptionModel = "whisper-large-v3",
            PartialsInOverlay = false,
            OverlayPosition = "top-right",
            OverlayOpacity = 0.42,
            SoundEffects = false,
            AutoStart = true
        };

        var vm = new SettingsViewModel(settings);

        Assert.Equal("Alt+Space", vm.Hotkey);
        Assert.Equal("ja", vm.Language);
        Assert.Equal("whisper-large-v3", vm.TranscriptionModel);
        Assert.False(vm.PartialsInOverlay);
        Assert.Equal("top-right", vm.OverlayPosition);
        Assert.Equal(0.42, vm.OverlayOpacity);
        Assert.False(vm.SoundEffects);
        Assert.True(vm.AutoStart);
    }

    [Fact]
    public void Apply_CopiesVmStateBackToSettings()
    {
        var settings = new HushSettings();
        var vm = new SettingsViewModel(settings);

        // Modify VM properties
        vm.Hotkey = "F5";
        vm.Language = "fr";
        vm.TranscriptionModel = "whisper-small";
        vm.PartialsInOverlay = false;
        vm.OverlayPosition = "top-left";
        vm.OverlayOpacity = 0.7;
        vm.SoundEffects = false;
        vm.AutoStart = true;

        // Apply back to settings
        vm.Apply();

        Assert.Equal("F5", settings.Hotkey);
        Assert.Equal("fr", settings.Language);
        Assert.Equal("whisper-small", settings.TranscriptionModel);
        Assert.False(settings.PartialsInOverlay);
        Assert.Equal("top-left", settings.OverlayPosition);
        Assert.Equal(0.7, settings.OverlayOpacity);
        Assert.False(settings.SoundEffects);
        Assert.True(settings.AutoStart);
    }

    [Fact]
    public void Apply_DoesNotModifySettings_IfVmUnchanged()
    {
        var settings = new HushSettings();
        var originalHotkey = settings.Hotkey;
        var vm = new SettingsViewModel(settings);

        vm.Apply();

        Assert.Equal(originalHotkey, settings.Hotkey);
    }

    // ── PropertyChanged notifications ────────────────────────────────────

    [Fact]
    public void PropertyChanged_Raised_OnHotkeyChange()
    {
        var vm = new SettingsViewModel(new HushSettings());
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        vm.Hotkey = "F12";

        Assert.Contains("Hotkey", raised);
    }

    [Fact]
    public void PropertyChanged_Raised_OnAllProperties()
    {
        var vm = new SettingsViewModel(new HushSettings());
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        vm.Hotkey = "X";
        vm.Language = "de";
        vm.TranscriptionModel = "whisper-base";
        vm.PartialsInOverlay = false;
        vm.OverlayPosition = "center";
        vm.OverlayOpacity = 0.1;
        vm.SoundEffects = false;
        vm.AutoStart = true;

        Assert.Contains("Hotkey", raised);
        Assert.Contains("Language", raised);
        Assert.Contains("TranscriptionModel", raised);
        Assert.Contains("PartialsInOverlay", raised);
        Assert.Contains("OverlayPosition", raised);
        Assert.Contains("OverlayOpacity", raised);
        Assert.Contains("SoundEffects", raised);
        Assert.Contains("AutoStart", raised);
    }

    // ── Edge cases ───────────────────────────────────────────────────────

    [Fact]
    public void EmptyHotkey_IsHandled()
    {
        var settings = new HushSettings { Hotkey = "" };
        var vm = new SettingsViewModel(settings);
        Assert.Equal("", vm.Hotkey);

        vm.Apply();
        Assert.Equal("", settings.Hotkey);
    }

    [Fact]
    public void OpacityBoundaryValues_ArePreserved()
    {
        var vm = new SettingsViewModel(new HushSettings());

        vm.OverlayOpacity = 0.0;
        Assert.Equal(0.0, vm.OverlayOpacity);

        vm.OverlayOpacity = 1.0;
        Assert.Equal(1.0, vm.OverlayOpacity);
    }
}
