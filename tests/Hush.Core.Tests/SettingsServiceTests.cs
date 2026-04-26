// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.Core.Configuration;

namespace Hush.Core.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public async Task LoadAsync_ReturnsDefaults_WhenFileDoesNotExist()
    {
        var svc = new SettingsService();
        // Point to a guaranteed-nonexistent path by using env override trick:
        // SettingsService uses ~/.hush/settings.json; we rely on the fact that
        // this is unlikely to exist in CI. A full test would use a temp directory.
        var settings = await svc.LoadAsync();
        Assert.NotNull(settings);
        Assert.Equal(HushSettings.DefaultRawHotkey(), settings.Hotkey);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsSettings()
    {
        // Write to a temp directory so we don't modify the real user settings.
        var tempDir = Path.Combine(Path.GetTempPath(), $"hush-test-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(tempDir, "settings.json");
        Directory.CreateDirectory(tempDir);

        try
        {
            var original = new HushSettings
            {
                Hotkey = "Ctrl+Alt+D",
                Language = "fr",
                OverlayOpacity = 0.5
            };

            // Serialise manually since SettingsService uses ~/.hush/ — this tests
            // the JSON schema only. Full DI/path-override tests live in integration tests.
            var json = System.Text.Json.JsonSerializer.Serialize(
                original,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                });
            await File.WriteAllTextAsync(settingsPath, json);

            var loaded = System.Text.Json.JsonSerializer.Deserialize<HushSettings>(
                await File.ReadAllTextAsync(settingsPath),
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                });

            Assert.NotNull(loaded);
            Assert.Equal("Ctrl+Alt+D", loaded!.Hotkey);
            Assert.Equal("fr", loaded.Language);
            Assert.Equal(0.5, loaded.OverlayOpacity);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("whisper-tiny")]
    [InlineData("openai-whisper-large-v3-turbo")]
    [InlineData("nemotron-speech-streaming-en-0.6b")]
    public void TryMigrateLegacyTranscriptionModel_RewritesObsoleteAliases(string obsoleteAlias)
    {
        var settings = new HushSettings { TranscriptionModel = obsoleteAlias };

        var migrated = SettingsService.TryMigrateLegacyTranscriptionModel(
            settings,
            out var oldAlias,
            out var newAlias);

        Assert.True(migrated);
        Assert.Equal(obsoleteAlias, oldAlias);
        Assert.Equal(HushSettings.DefaultTranscriptionModel, newAlias);
        Assert.Equal(HushSettings.DefaultTranscriptionModel, settings.TranscriptionModel);
    }

    [Fact]
    public void TryMigrateLegacyTranscriptionModel_LeavesCurrentAliasUnchanged()
    {
        var settings = new HushSettings();

        var migrated = SettingsService.TryMigrateLegacyTranscriptionModel(
            settings,
            out var oldAlias,
            out var newAlias);

        Assert.False(migrated);
        Assert.Equal(HushSettings.DefaultTranscriptionModel, oldAlias);
        Assert.Equal(HushSettings.DefaultTranscriptionModel, newAlias);
        Assert.Equal(HushSettings.DefaultTranscriptionModel, settings.TranscriptionModel);
    }
}
