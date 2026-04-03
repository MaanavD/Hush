using Hush.Core.Configuration;

namespace Hush.Core.Tests;

public sealed class HushSettingsTests
{
    [Fact]
    public void DefaultSettings_HaveExpectedValues()
    {
        var settings = new HushSettings();

        Assert.Equal("Ctrl+Shift+H", settings.Hotkey);
        Assert.Equal("en", settings.Language);
        Assert.Equal("whisper-tiny", settings.TranscriptionModel);
        Assert.True(settings.PartialsInOverlay);
        Assert.True(settings.TypeCommittedTextOnly);
        Assert.False(settings.ClipboardFallback);
        Assert.False(settings.AutoStart);
    }
}
