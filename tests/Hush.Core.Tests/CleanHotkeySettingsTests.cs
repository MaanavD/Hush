// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Text.Json;
using Hush.Core.Configuration;

namespace Hush.Core.Tests;

/// <summary>
/// Tests for <see cref="HushSettings.CleanHotkey"/>, <see cref="HushSettings.PostProcessingEnabled"/>,
/// and <see cref="HushSettings.PostProcessingModel"/> covering defaults and serialization.
/// </summary>
public sealed class CleanHotkeySettingsTests
{
    [Fact]
    public void DefaultCleanHotkey_IsCtrlH()
    {
        var settings = new HushSettings();

        Assert.Equal("Ctrl+Alt+H", settings.CleanHotkey);
    }

    [Fact]
    public void CleanHotkey_RoundTripsInJson()
    {
        var settings = new HushSettings { CleanHotkey = "Ctrl+Shift+H" };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<HushSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal("Ctrl+Shift+H", restored!.CleanHotkey);
    }

    [Fact]
    public void CleanHotkey_DefaultPreservedAfterRoundTrip()
    {
        var settings = new HushSettings();

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<HushSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal("Ctrl+Alt+H", restored!.CleanHotkey);
    }

    [Fact]
    public void CleanHotkey_IsIndependentOfRawHotkey()
    {
        var settings = new HushSettings();

        // Raw defaults to Ctrl+H, clean to Ctrl+Alt+H on every platform.
        Assert.Equal("Ctrl+H", settings.Hotkey);
        Assert.Equal("Ctrl+Alt+H", settings.CleanHotkey);
        Assert.NotEqual(settings.Hotkey, settings.CleanHotkey);
    }

    [Fact]
    public void PostProcessingEnabled_DefaultIsTrue()
    {
        var settings = new HushSettings();

        Assert.True(settings.PostProcessingEnabled);
    }

    [Fact]
    public void PostProcessingModel_DefaultIsQwen()
    {
        var settings = new HushSettings();

        Assert.Equal("qwen3-0.6b", settings.PostProcessingModel);
    }

    [Fact]
    public void PostProcessingSettings_RoundTripInJson()
    {
        var settings = new HushSettings
        {
            PostProcessingEnabled = false,
            PostProcessingModel = "phi-3-mini"
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<HushSettings>(json);

        Assert.NotNull(restored);
        Assert.False(restored!.PostProcessingEnabled);
        Assert.Equal("phi-3-mini", restored.PostProcessingModel);
    }
}
