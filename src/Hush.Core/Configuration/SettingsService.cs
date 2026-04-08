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
            return settings ?? new HushSettings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings from '{Path}'; using defaults.", SettingsPath);
            return new HushSettings();
        }
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
