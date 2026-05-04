// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.App.ViewModels;
using Hush.Core.Configuration;
using Hush.Core.Models;

namespace Hush.App.Tests;

/// <summary>
/// Tests for <see cref="SettingsViewModel"/> ensuring proper two-way binding
/// between the VM and the backing <see cref="HushSettings"/> object.
/// </summary>
public sealed class SettingsViewModelTests
{
    private sealed class StubLanguageModelCatalogService : ILanguageModelCatalogService
    {
        private readonly IReadOnlyList<LanguageModelCatalogItem> _models;

        public StubLanguageModelCatalogService(params LanguageModelCatalogItem[] models)
        {
            _models = models;
        }

        public Task<IReadOnlyList<LanguageModelCatalogItem>> ListSmallLanguageModelsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(_models);
    }

    [Fact]
    public void Constructor_PopulatesFromSettings()
    {
        var settings = new HushSettings
        {
            Hotkey = "Alt+Space",
            Language = "ja",
            TranscriptionModel = "nemotron-speech-streaming-en-0.6b",
            PartialsInOverlay = false,
            OverlayPosition = "top-right",
            OverlayOpacity = 0.42,
            SoundEffects = false,
            AutoStart = true
        };

        var vm = new SettingsViewModel(settings);

        Assert.Equal("Alt+Space", vm.Hotkey);
        Assert.Equal("ja", vm.Language);
        Assert.Equal("nemotron-speech-streaming-en-0.6b", vm.TranscriptionModel);
        Assert.False(vm.PartialsInOverlay);
        Assert.Equal("top-right", vm.OverlayPosition);
        Assert.Equal(0.42, vm.OverlayOpacity);
        Assert.False(vm.SoundEffects);
        Assert.True(vm.AutoStart);
        Assert.Equal("qwen3-0.6b", vm.SelectedPostProcessingLanguageModel?.Alias);
    }

    [Fact]
    public async Task RefreshLanguageModelsAsync_LoadsSmallLanguageModelOptions()
    {
        var settings = new HushSettings { PostProcessingModel = "qwen3-0.6b" };
        var catalog = new StubLanguageModelCatalogService(
            new LanguageModelCatalogItem("qwen3-0.6b", "qwen3-0.6b-generic-cpu", 0.6, 593, true, "reasoning"),
            new LanguageModelCatalogItem("qwen3-4b", "qwen3-4b-generic-cpu", 4.0, 2763, false, "reasoning"));
        var vm = new SettingsViewModel(settings, catalog);

        await vm.RefreshLanguageModelsAsync();

        Assert.Equal(2, vm.AvailableLanguageModels.Count);
        Assert.Contains(vm.AvailableLanguageModels, model => model.Alias == "qwen3-4b");
        Assert.Equal("qwen3-0.6b", vm.SelectedPostProcessingLanguageModel?.Alias);
        Assert.Equal("2 language models available.", vm.LanguageModelCatalogStatus);
    }

    [Fact]
    public void SelectedPostProcessingLanguageModel_UpdatesPostProcessingModel()
    {
        var settings = new HushSettings();
        var vm = new SettingsViewModel(settings);
        var option = new LanguageModelOptionViewModel("qwen3-4b", "4B - 2,763 MB", IsCatalogModel: true);
        vm.AvailableLanguageModels.Add(option);

        vm.SelectedPostProcessingLanguageModel = option;
        vm.Apply();

        Assert.Equal("qwen3-4b", vm.PostProcessingModel);
        Assert.Equal("qwen3-4b", settings.PostProcessingModel);
    }

    [Fact]
    public void Apply_CopiesVmStateBackToSettings()
    {
        var settings = new HushSettings();
        var vm = new SettingsViewModel(settings);

        // Modify VM properties
        vm.Hotkey = "F5";
        vm.Language = "fr";
        vm.TranscriptionModel = "nemotron-speech-streaming-en-0.6b";
        vm.PartialsInOverlay = false;
        vm.OverlayPosition = "top-left";
        vm.OverlayOpacity = 0.7;
        vm.SoundEffects = false;
        vm.AutoStart = true;

        // Apply back to settings
        vm.Apply();

        Assert.Equal("F5", settings.Hotkey);
        Assert.Equal("fr", settings.Language);
        Assert.Equal("nemotron-speech-streaming-en-0.6b", settings.TranscriptionModel);
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
        vm.TranscriptionModel = "nemotron-speech-streaming-en-0.6b-alt";
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
