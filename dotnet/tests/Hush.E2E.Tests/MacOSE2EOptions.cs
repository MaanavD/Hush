using System.Globalization;

namespace Hush.E2E.Tests;

internal static class MacOSE2EOptions
{
    public static bool RunMacOSE2E => IsTruthy(Environment.GetEnvironmentVariable("HUSH_RUN_MACOS_E2E"));

    public static string CaseId =>
        Environment.GetEnvironmentVariable("HUSH_MACOS_E2E_CASE")?.Trim() is { Length: > 0 } value
            ? value
            : "en_rt_003_fillers_correction";

    public static double DelayScale => GetDouble("HUSH_MACOS_E2E_DELAY_SCALE", AudioE2EOptions.DelayScale);

    public static TimeSpan Timeout => TimeSpan.FromMinutes(GetDouble("HUSH_MACOS_E2E_TIMEOUT_MINUTES", 10.0));

    public static double MaxWer => GetDouble("HUSH_MACOS_E2E_MAX_WER", AudioE2EOptions.MaxWer);

    public static double MaxCer => GetDouble("HUSH_MACOS_E2E_MAX_CER", AudioE2EOptions.MaxCer);

    public static TimeSpan MaxFirstInterimLatency =>
        TimeSpan.FromSeconds(GetDouble("HUSH_MACOS_E2E_MAX_FIRST_INTERIM_SECONDS", 30.0));

    public static TimeSpan MaxFirstCommitLatency =>
        TimeSpan.FromSeconds(GetDouble("HUSH_MACOS_E2E_MAX_FIRST_COMMIT_SECONDS", 30.0));

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

