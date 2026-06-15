using System.Globalization;

namespace Hush.E2E.Tests;

internal static class AudioE2EOptions
{
    public const string DefaultModelAlias = "nemotron-speech-streaming-en-0.6b-generic-cpu";

    public static bool RunAudioE2E => IsTruthy(Environment.GetEnvironmentVariable("HUSH_RUN_AUDIO_E2E"));

    public static string ModelAlias =>
        Environment.GetEnvironmentVariable("HUSH_AUDIO_E2E_MODEL")?.Trim() is { Length: > 0 } value
            ? value
            : DefaultModelAlias;

    public static double MaxWer => GetDouble("HUSH_AUDIO_E2E_MAX_WER", 0.55);

    public static double MaxCer => GetDouble("HUSH_AUDIO_E2E_MAX_CER", 0.35);

    public static double DelayScale => GetDouble("HUSH_AUDIO_E2E_DELAY_SCALE", 0.0);

    public static TimeSpan Timeout => TimeSpan.FromMinutes(GetDouble("HUSH_AUDIO_E2E_TIMEOUT_MINUTES", 10.0));

    public static IReadOnlyList<string> CaseIds
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("HUSH_AUDIO_E2E_CASES");
            if (string.IsNullOrWhiteSpace(configured))
            {
                return new[]
                {
                    "en_rt_003_fillers_correction",
                    "en_rt_011_endpoint_pause",
                    "en_rt_022_longer_stream",
                    "en_rt_030_noisy_fast",
                };
            }

            return configured
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private static bool IsTruthy(string? value) =>
        value is not null &&
        (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));

    private static double GetDouble(string name, double fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }
}
