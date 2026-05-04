using System.Text;
using System.Text.RegularExpressions;

namespace Hush.E2E.Tests;

internal static partial class TranscriptQuality
{
    private const double LowOverlapContaminationThreshold = 0.25;

    public static TranscriptComparison Compare(string expected, string actual)
    {
        var expectedText = NormalizeText(expected);
        var actualText = NormalizeText(actual);
        var expectedWords = Tokenize(expectedText);
        var actualWords = Tokenize(actualText);

        var wordDistance = Levenshtein(expectedWords, actualWords);
        var charDistance = Levenshtein(expectedText.AsSpan(), actualText.AsSpan());

        return new TranscriptComparison(
            expectedText,
            actualText,
            expectedWords,
            actualWords,
            expectedWords.Length == 0 ? 0 : wordDistance / (double)expectedWords.Length,
            expectedText.Length == 0 ? 0 : charDistance / (double)expectedText.Length);
    }

    public static bool HasSuspiciousDuplicateRun(string text)
    {
        var words = Tokenize(NormalizeText(text));
        if (words.Length < 5)
            return false;

        for (int i = 0; i + 3 < words.Length; i++)
        {
            if (words[i] == words[i + 1] && words[i] == words[i + 2])
                return true;

            if (i + 5 < words.Length &&
                words[i] == words[i + 2] &&
                words[i + 1] == words[i + 3] &&
                words[i] == words[i + 4] &&
                words[i + 1] == words[i + 5])
            {
                return true;
            }
        }

        return false;
    }

    public static double WordOverlapRate(string expected, string actual)
    {
        var expectedWords = Tokenize(NormalizeText(expected));
        var actualWords = Tokenize(NormalizeText(actual));
        if (expectedWords.Length == 0)
            return actualWords.Length == 0 ? 1 : 0;

        return LongestCommonSubsequenceLength(expectedWords, actualWords) / (double)expectedWords.Length;
    }

    public static double RenderedToExpectedTokenRatio(string expected, string actual)
    {
        var expectedWords = Tokenize(NormalizeText(expected));
        var actualWords = Tokenize(NormalizeText(actual));
        return expectedWords.Length == 0
            ? actualWords.Length == 0 ? 1 : double.PositiveInfinity
            : actualWords.Length / (double)expectedWords.Length;
    }

    public static int CountTokens(string text) => Tokenize(NormalizeText(text)).Length;

    public static TextArtifactAnalysis AnalyzeRenderedArtifacts(string expected, string actual)
    {
        var overlapRate = WordOverlapRate(expected, actual);
        var contaminationReason = GetContaminationArtifactReason(expected, actual, overlapRate);
        return new TextArtifactAnalysis(
            ContainsCleanModeSpinnerArtifact(actual),
            contaminationReason is not null,
            contaminationReason ?? string.Empty);
    }

    public static bool ContainsCleanModeSpinnerArtifact(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (!IsSpinnerFrame(ch))
                continue;

            if (IsWholeLineSpinnerFrame(text, i))
                return true;

            if (IsTrailingSpinnerFrame(text, i))
                return true;

            if ((ch == '|' || ch == '\u2014') && IsStandaloneSpinnerFrame(text, i))
                return true;
        }

        return false;
    }

    public static string? GetContaminationArtifactReason(
        string expected,
        string actual,
        double? overlapRate = null)
    {
        if (string.IsNullOrWhiteSpace(actual))
            return null;

        foreach (Match match in UrlRegex().Matches(actual))
        {
            var url = TrimUrl(match.Value);
            if (url.Length > 0 && !expected.Contains(url, StringComparison.OrdinalIgnoreCase))
                return "foreign-url";
        }

        var expectedTokenCount = CountTokens(expected);
        var actualTokenCount = CountTokens(actual);
        var overlap = overlapRate ?? WordOverlapRate(expected, actual);
        if (expectedTokenCount >= 4 &&
            actualTokenCount >= 4 &&
            overlap < LowOverlapContaminationThreshold)
        {
            return "very-low-overlap";
        }

        return null;
    }

    public static string NormalizeText(string text)
    {
        var lower = text.ToLowerInvariant();
        var cleaned = NonWordOrSpaceRegex().Replace(lower, " ");
        return WhiteSpaceRegex().Replace(cleaned, " ").Trim();
    }

    private static string[] Tokenize(string normalizedText) =>
        string.IsNullOrWhiteSpace(normalizedText)
            ? Array.Empty<string>()
            : normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static int Levenshtein(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        var previous = Enumerable.Range(0, actual.Count + 1).ToArray();
        var current = new int[actual.Count + 1];

        for (int i = 1; i <= expected.Count; i++)
        {
            current[0] = i;
            for (int j = 1; j <= actual.Count; j++)
            {
                var substitutionCost = expected[i - 1] == actual[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[actual.Count];
    }

    private static int Levenshtein(ReadOnlySpan<char> expected, ReadOnlySpan<char> actual)
    {
        var previous = Enumerable.Range(0, actual.Length + 1).ToArray();
        var current = new int[actual.Length + 1];

        for (int i = 1; i <= expected.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= actual.Length; j++)
            {
                var substitutionCost = expected[i - 1] == actual[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[actual.Length];
    }

    private static int LongestCommonSubsequenceLength(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        var previous = new int[actual.Count + 1];
        var current = new int[actual.Count + 1];

        for (int i = 1; i <= expected.Count; i++)
        {
            for (int j = 1; j <= actual.Count; j++)
            {
                current[j] = expected[i - 1] == actual[j - 1]
                    ? previous[j - 1] + 1
                    : Math.Max(previous[j], current[j - 1]);
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        return previous[actual.Count];
    }

    private static bool IsSpinnerFrame(char ch) =>
        ch is '|' or '/' or '\\' or '\u2014';

    private static bool IsWholeLineSpinnerFrame(string text, int index)
    {
        int lineStart = index;
        while (lineStart > 0 && text[lineStart - 1] is not '\r' and not '\n')
            lineStart--;

        int lineEnd = index + 1;
        while (lineEnd < text.Length && text[lineEnd] is not '\r' and not '\n')
            lineEnd++;

        return text[lineStart..lineEnd].Trim().Length == 1;
    }

    private static bool IsTrailingSpinnerFrame(string text, int index)
    {
        if (text.AsSpan(index + 1).Trim().Length != 0)
            return false;

        var ch = text[index];
        if (ch is '/' or '\\')
            return !IsUrlOrPathToken(GetTokenAround(text, index));

        return ch is '|' or '\u2014';
    }

    private static bool IsStandaloneSpinnerFrame(string text, int index)
    {
        bool leftBoundary = index == 0 ||
            char.IsWhiteSpace(text[index - 1]) ||
            text[index - 1] is '(' or '[' or '{' or '"' or '\'';
        bool rightBoundary = index + 1 >= text.Length ||
            char.IsWhiteSpace(text[index + 1]) ||
            text[index + 1] is ')' or ']' or '}' or '"' or '\'' or '.' or ',' or ';' or ':';

        return leftBoundary && rightBoundary;
    }

    private static string GetTokenAround(string text, int index)
    {
        int start = index;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
            start--;

        int end = index + 1;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
            end++;

        return text[start..end];
    }

    private static bool IsUrlOrPathToken(string token)
    {
        var trimmed = TrimUrl(token);
        if (UrlRegex().IsMatch(trimmed) || WindowsDrivePathRegex().IsMatch(trimmed))
            return true;

        int slashCount = trimmed.Count(ch => ch is '/' or '\\');
        return slashCount >= 2;
    }

    private static string TrimUrl(string value) =>
        value.Trim().TrimEnd('.', ',', ';', ':', ')', ']', '}', '"', '\'');

    [GeneratedRegex(@"[^\p{L}\p{N}\s]+")]
    private static partial Regex NonWordOrSpaceRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpaceRegex();

    [GeneratedRegex(@"\b(?:https?://|www\.)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^[a-z]:[\\/]", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsDrivePathRegex();
}

internal sealed record TranscriptComparison(
    string ExpectedNormalized,
    string ActualNormalized,
    IReadOnlyList<string> ExpectedWords,
    IReadOnlyList<string> ActualWords,
    double Wer,
    double Cer);

internal sealed record TextArtifactAnalysis(
    bool ContainsSpinnerArtifact,
    bool ContainsContaminationArtifact,
    string ContaminationArtifactReason);
