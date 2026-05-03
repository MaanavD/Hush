using System.Globalization;

namespace Hush.E2E.Tests;

internal static class NotepadE2EOptions
{
    private static readonly char[] CaseIdSeparators = { ',', ';', '\r', '\n' };

    public static bool RunNotepadE2E => IsTruthy(Environment.GetEnvironmentVariable("HUSH_RUN_NOTEPAD_E2E"));

    public static bool RunAllAudioReport =>
        IsTruthy(Environment.GetEnvironmentVariable("HUSH_RUN_NOTEPAD_E2E_REPORT_ALL"));

    public static bool RunStandardAllAudioReport =>
        IsTruthy(Environment.GetEnvironmentVariable("HUSH_RUN_NOTEPAD_E2E_REPORT_STANDARD"));

    public static string CaseId =>
        Environment.GetEnvironmentVariable("HUSH_NOTEPAD_E2E_CASE")?.Trim() is { Length: > 0 } value
            ? value
            : "en_rt_003_fillers_correction";

    public static IReadOnlyList<string> ReportCaseIds =>
        ParseReportCaseIds(Environment.GetEnvironmentVariable("HUSH_NOTEPAD_E2E_CASES"));

    public static double DelayScale => GetDouble("HUSH_NOTEPAD_E2E_DELAY_SCALE", 0.0);

    public static int LiveAudioPushQueueCapacity => GetInt("HUSH_NOTEPAD_E2E_PUSH_QUEUE_CAPACITY", 12);

    public static int AudioChunkDurationMilliseconds => GetInt("HUSH_NOTEPAD_E2E_AUDIO_CHUNK_MS", 50);

    public static TimeSpan Timeout => TimeSpan.FromMinutes(GetDouble("HUSH_NOTEPAD_E2E_TIMEOUT_MINUTES", 10.0));

    public static double MaxRawWer => GetDouble("HUSH_NOTEPAD_E2E_MAX_RAW_WER", AudioE2EOptions.MaxWer);

    public static double MaxFinalWer => GetDouble("HUSH_NOTEPAD_E2E_MAX_FINAL_WER", 0.15);

    public static double MaxFinalCer => GetDouble("HUSH_NOTEPAD_E2E_MAX_FINAL_CER", 0.08);

    public static TimeSpan MaxRewriteDuration => TimeSpan.FromSeconds(GetDouble("HUSH_NOTEPAD_E2E_MAX_REWRITE_SECONDS", 30.0));

    public static double MaxTranslationCer => GetDouble("HUSH_NOTEPAD_E2E_MAX_TRANSLATION_CER", 0.50);

    public static TimeSpan MaxTranslationDuration => TimeSpan.FromSeconds(GetDouble("HUSH_NOTEPAD_E2E_MAX_TRANSLATION_SECONDS", 30.0));

    public static TimeSpan MaxFirstInterimLatency => TimeSpan.FromSeconds(GetDouble("HUSH_NOTEPAD_E2E_MAX_FIRST_INTERIM_SECONDS", 30.0));

    public static TimeSpan MaxFirstCommitLatency => TimeSpan.FromSeconds(GetDouble("HUSH_NOTEPAD_E2E_MAX_FIRST_COMMIT_SECONDS", 30.0));

    public static TimeSpan SyntheticRewriteDelay => TimeSpan.FromMilliseconds(GetDouble("HUSH_NOTEPAD_E2E_REWRITE_DELAY_MS", 0.0));

    public static bool UseDeterministicPostProcessor =>
        IsTruthy(Environment.GetEnvironmentVariable("HUSH_NOTEPAD_E2E_DETERMINISTIC_POST_PROCESSOR"));

    public static string PostProcessingModel =>
        Environment.GetEnvironmentVariable("HUSH_NOTEPAD_E2E_POST_MODEL")?.Trim() is { Length: > 0 } value
            ? value
            : "qwen3-0.6b";

    internal static IReadOnlyList<string> ParseReportCaseIds(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return Array.Empty<string>();

        return configured
            .Split(CaseIdSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<SyntheticAudioCase> FilterReportCases(IReadOnlyList<SyntheticAudioCase> cases) =>
        FilterReportCases(cases, ReportCaseIds);

    internal static IReadOnlyList<SyntheticAudioCase> FilterReportCases(
        IReadOnlyList<SyntheticAudioCase> cases,
        IReadOnlyList<string> requestedIds)
    {
        if (requestedIds.Count == 0)
            return cases;

        var casesById = cases.ToDictionary(testCase => testCase.Id, StringComparer.OrdinalIgnoreCase);
        var missingIds = requestedIds
            .Where(id => !casesById.ContainsKey(id))
            .ToArray();

        Assert.True(
            missingIds.Length == 0,
            $"HUSH_NOTEPAD_E2E_CASES requested unknown case(s): {string.Join(", ", missingIds)}. " +
            $"Available cases: {string.Join(", ", cases.Select(testCase => testCase.Id).OrderBy(id => id, StringComparer.Ordinal))}.");

        return requestedIds
            .Select(id => casesById[id])
            .ToArray();
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

    private static int GetInt(string name, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;
    }
}
