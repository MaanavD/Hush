// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hush.Core.Configuration;

/// <summary>
/// Loads and saves <see cref="HushSettings"/> to <c>~/.hush/settings.json</c>.
/// </summary>
public sealed class SettingsService
{
    private static readonly string SettingsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hush");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDirectory, "settings.json");

    private readonly ILogger<SettingsService> _logger;

    public SettingsService(ILogger<SettingsService>? logger = null)
    {
        _logger = logger ?? NullLogger<SettingsService>.Instance;
    }

    /// <summary>
    /// Loads settings from disk. Returns defaults if the file does not exist.
    /// </summary>
    public async Task<HushSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsPath))
        {
            _logger.LogInformation("Settings file not found; using defaults.");
            return new HushSettings();
        }

        try
        {
            await using var stream = File.OpenRead(SettingsPath);
            var settings = await System.Text.Json.JsonSerializer.DeserializeAsync(
                stream,
                SettingsJsonContext.Default.HushSettings,
                cancellationToken);
            settings ??= new HushSettings();
            MigrateLegacyTranscriptionModel(settings);
            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings from '{Path}'; using defaults.", SettingsPath);
            return new HushSettings();
        }
    }

    /// <summary>
    /// An intermediate Nemotron build pinned the concrete CPU variant id
    /// instead of the public catalog alias. Stale aliases are upgraded to
    /// the current Nemotron default so startup warmup can resolve the model.
    /// </summary>
    private void MigrateLegacyTranscriptionModel(HushSettings settings)
    {
        if (TryMigrateLegacyTranscriptionModel(settings, out var oldAlias, out var newAlias))
        {
            _logger.LogInformation(
                "Migrating legacy transcription model alias '{Old}' to '{New}'.",
                oldAlias, newAlias);
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> and rewrites <see cref="HushSettings.TranscriptionModel"/>
    /// to the current default when the saved alias references an obsolete
    /// transcription model. Public for testing.
    /// </summary>
    internal static bool TryMigrateLegacyTranscriptionModel(HushSettings settings, out string oldAlias, out string newAlias)
    {
        oldAlias = settings.TranscriptionModel ?? string.Empty;
        newAlias = oldAlias;
        if (string.IsNullOrWhiteSpace(oldAlias)) return false;
        if (string.Equals(oldAlias, HushSettings.DefaultTranscriptionModel, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(oldAlias, HushSettings.LegacyTranscriptionModelVariant, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        newAlias = HushSettings.DefaultTranscriptionModel;
        settings.TranscriptionModel = newAlias;
        return true;
    }

    /// <summary>
    /// Persists <paramref name="settings"/> to disk atomically.
    /// </summary>
    public async Task SaveAsync(HushSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(SettingsDirectory);

        // Write to a temp file then move for atomicity.
        var tmp = SettingsPath + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await System.Text.Json.JsonSerializer.SerializeAsync(
                stream,
                settings,
                SettingsJsonContext.Default.HushSettings,
                cancellationToken);
        }

        File.Move(tmp, SettingsPath, overwrite: true);
        _logger.LogInformation("Settings saved to '{Path}'.", SettingsPath);
    }
}
