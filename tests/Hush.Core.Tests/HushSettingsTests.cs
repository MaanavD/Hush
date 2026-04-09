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
}
