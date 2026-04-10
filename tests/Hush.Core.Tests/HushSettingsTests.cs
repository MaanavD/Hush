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
}
