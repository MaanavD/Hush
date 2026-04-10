// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.Core.Configuration;

namespace Hush.Core.Tests;

public sealed class HushSettingsTests
{
    [Fact]
    public void DefaultSettings_HaveExpectedValues()
    {
        var settings = new HushSettings();

        Assert.Equal("Ctrl+H", settings.Hotkey);
        Assert.Equal("en", settings.Language);
        Assert.Equal("whisper-tiny", settings.TranscriptionModel);
        Assert.True(settings.PartialsInOverlay);
        Assert.False(settings.ClipboardFallback);
        Assert.False(settings.AutoStart);
    }

    [Fact]
    public void DefaultSettings_HasNoneAutoSubmitKey()
    {
        var settings = new HushSettings();
        Assert.Equal(AutoSubmitKey.None, settings.AutoSubmitKey);
    }

    [Fact]
    public void DefaultSettings_HasMin5ModelUnloadTimeout()
    {
        var settings = new HushSettings();
        Assert.Equal(ModelUnloadTimeout.Min5, settings.ModelUnloadTimeout);
    }

    [Fact]
    public void DefaultSettings_HasEmptyCustomSubstitutions()
    {
        var settings = new HushSettings();
        Assert.NotNull(settings.CustomSubstitutions);
        Assert.Empty(settings.CustomSubstitutions);
    }

    [Fact]
    public void DefaultSettings_HasExpectedPostProcessingDefaults()
    {
        var settings = new HushSettings();

        Assert.True(settings.PostProcessingEnabled);
        Assert.Equal("qwen3-0.6b", settings.PostProcessingModel);
        Assert.NotNull(settings.PostProcessingPrompts);
        Assert.Empty(settings.PostProcessingPrompts);
        Assert.Null(settings.ActivePostProcessingPromptId);
        Assert.Equal(3, HushSettings.BuiltInPrompts.Count);
    }

    [Fact]
    public void GetActivePrompt_DefaultSettings_ReturnsFirstBuiltIn()
    {
        var settings = new HushSettings();

        var prompt = settings.GetActivePrompt();

        Assert.Equal(HushSettings.BuiltInPrompts[0].Id, prompt.Id);
        Assert.True(prompt.IsBuiltIn);
    }
}
