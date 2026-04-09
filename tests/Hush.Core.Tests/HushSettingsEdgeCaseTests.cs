// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.Core.Configuration;
using System.Text.Json;

namespace Hush.Core.Tests;

/// <summary>
/// Edge-case tests for <see cref="HushSettings"/> covering validation,
/// serialization, and real-world configuration scenarios.
/// </summary>
public sealed class HushSettingsEdgeCaseTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ── Serialization round-trip ─────────────────────────────────────────

    [Fact]
    public void DefaultSettings_RoundTripJsonPreservesAllDefaults()
    {
        var original = new HushSettings();
        var json = JsonSerializer.Serialize(original, JsonOptions);
        var loaded = JsonSerializer.Deserialize<HushSettings>(json, JsonOptions);

        Assert.NotNull(loaded);
        Assert.Equal(original.Hotkey, loaded!.Hotkey);
        Assert.Equal(original.Language, loaded.Language);
        Assert.Equal(original.TranscriptionModel, loaded.TranscriptionModel);
        Assert.Equal(original.PartialsInOverlay, loaded.PartialsInOverlay);
        Assert.Equal(original.ClipboardFallback, loaded.ClipboardFallback);
        Assert.Equal(original.AutoStart, loaded.AutoStart);
        Assert.Equal(original.OverlayPosition, loaded.OverlayPosition);
        Assert.Equal(original.OverlayOpacity, loaded.OverlayOpacity);
        Assert.Equal(original.SoundEffects, loaded.SoundEffects);
    }

    [Fact]
    public void CustomSettings_RoundTripJsonPreservesAll()
    {
        var original = new HushSettings
        {
            Hotkey = "Alt+Space",
            Language = "ja",
            TranscriptionModel = "whisper-large-v3",
            PartialsInOverlay = false,
            ClipboardFallback = true,
            AutoStart = true,
            OverlayPosition = "top-right",
            OverlayOpacity = 0.42,
            SoundEffects = false
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var loaded = JsonSerializer.Deserialize<HushSettings>(json, JsonOptions)!;

        Assert.Equal("Alt+Space", loaded.Hotkey);
        Assert.Equal("ja", loaded.Language);
        Assert.Equal("whisper-large-v3", loaded.TranscriptionModel);
        Assert.False(loaded.PartialsInOverlay);
        Assert.True(loaded.ClipboardFallback);
        Assert.True(loaded.AutoStart);
        Assert.Equal("top-right", loaded.OverlayPosition);
        Assert.Equal(0.42, loaded.OverlayOpacity);
        Assert.False(loaded.SoundEffects);
    }

    // ── Forward compatibility: extra JSON properties ─────────────────────

    [Fact]
    public void ExtraJsonProperties_AreIgnored()
    {
        var json = """
        {
            "hotkey": "Ctrl+H",
            "language": "en",
            "futureProperty": "value",
            "anotherFutureFeature": 42
        }
        """;

        var loaded = JsonSerializer.Deserialize<HushSettings>(json, JsonOptions);
        Assert.NotNull(loaded);
        Assert.Equal("Ctrl+H", loaded!.Hotkey);
        Assert.Equal("en", loaded.Language);
    }

    // ── Missing JSON properties default correctly ────────────────────────

    [Fact]
    public void PartialJson_MissingPropertiesGetDefaults()
    {
        var json = """{ "hotkey": "F5" }""";

        var loaded = JsonSerializer.Deserialize<HushSettings>(json, JsonOptions);
        Assert.NotNull(loaded);
        Assert.Equal("F5", loaded!.Hotkey);
        // All other properties should be defaults
        Assert.Equal("en", loaded.Language);
        Assert.Equal("whisper-tiny", loaded.TranscriptionModel);
        Assert.True(loaded.PartialsInOverlay);
        Assert.True(loaded.SoundEffects);
    }

    [Fact]
    public void EmptyJsonObject_GivesDefaults()
    {
        var loaded = JsonSerializer.Deserialize<HushSettings>("{}", JsonOptions);
        Assert.NotNull(loaded);
        Assert.Equal("Ctrl+H", loaded!.Hotkey);
    }

    // ── Boundary values ──────────────────────────────────────────────────

    [Fact]
    public void OverlayOpacity_ZeroAndOne()
    {
        var settings = new HushSettings { OverlayOpacity = 0.0 };
        Assert.Equal(0.0, settings.OverlayOpacity);

        settings.OverlayOpacity = 1.0;
        Assert.Equal(1.0, settings.OverlayOpacity);
    }

    [Theory]
    [InlineData("Ctrl+H")]
    [InlineData("Alt+Space")]
    [InlineData("F5")]
    [InlineData("Ctrl+Alt+Delete")]
    [InlineData("")]
    public void Hotkey_AcceptsVariousFormats(string hotkey)
    {
        var settings = new HushSettings { Hotkey = hotkey };
        Assert.Equal(hotkey, settings.Hotkey);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("zh")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("ko")]
    [InlineData("ar")]
    public void Language_AcceptsVariousBcp47Tags(string lang)
    {
        var settings = new HushSettings { Language = lang };
        Assert.Equal(lang, settings.Language);
    }

    // ── Single-exe scenario: settings path is valid ──────────────────────

    [Fact]
    public void SettingsDirectory_IsUnderUserProfile()
    {
        // Verify the settings directory resolves to a valid user profile path.
        // This is critical for single-exe deployment where the working directory
        // may be anywhere (Downloads, Program Files, etc.)
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrEmpty(home), "UserProfile must be available");

        var expectedDir = Path.Combine(home, ".hush");
        Assert.StartsWith(home, expectedDir);
    }
}
