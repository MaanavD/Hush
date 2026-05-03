namespace Hush.E2E.Tests;

internal enum NotepadStandardCommittedTextOperationKind
{
    TypeText,
    Backspace,
    ReplaceText
}

internal sealed record NotepadStandardRenderTiming(string Text, double StartedAtMs, double CompletedAtMs);

internal sealed record NotepadStandardCommittedTextOperation(
    NotepadStandardCommittedTextOperationKind Kind,
    string Text,
    int BackspaceCount = 0);

internal static class NotepadStandardReportMetrics
{
    public static string ReconstructCommittedText(IReadOnlyList<NotepadStandardCommittedTextOperation> operations)
    {
        var text = new System.Text.StringBuilder();

        foreach (var operation in operations)
        {
            switch (operation.Kind)
            {
                case NotepadStandardCommittedTextOperationKind.TypeText:
                    text.Append(operation.Text);
                    break;
                case NotepadStandardCommittedTextOperationKind.Backspace:
                    ApplyBackspaces(text, operation.BackspaceCount);
                    break;
                case NotepadStandardCommittedTextOperationKind.ReplaceText:
                    ApplyBackspaces(text, operation.BackspaceCount);
                    text.Append(operation.Text);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operations), operation.Kind, "Unknown committed text operation kind.");
            }
        }

        return text.ToString();
    }

    public static double CalculateAverageEstimatedAudioTokenE2ELatency(
        IReadOnlyList<NotepadStandardRenderTiming> typeEvents,
        string expectedRawTranscript,
        TimeSpan audioDuration)
    {
        int expectedTokenCount = Math.Max(1, TranscriptQuality.CountTokens(expectedRawTranscript));
        int tokenOrdinal = 0;
        var latencies = new List<double>();

        foreach (var renderEvent in typeEvents)
        {
            int renderedTokens = TranscriptQuality.CountTokens(renderEvent.Text);
            for (int i = 0; i < renderedTokens; i++)
            {
                tokenOrdinal++;
                var estimatedVoiceInputMs = audioDuration.TotalMilliseconds
                    * Math.Min(tokenOrdinal, expectedTokenCount)
                    / expectedTokenCount;
                latencies.Add(Math.Max(0, renderEvent.CompletedAtMs - estimatedVoiceInputMs));
            }
        }

        return latencies.Count == 0 ? 0 : latencies.Average();
    }

    public static double CalculateAverageRenderOutputDuration(IReadOnlyList<NotepadStandardRenderTiming> typeEvents) =>
        typeEvents.Count == 0 ? 0 : typeEvents.Average(renderEvent => Math.Max(0, renderEvent.CompletedAtMs - renderEvent.StartedAtMs));

    public static double CalculateAverageInterCommitInterval(IReadOnlyList<NotepadStandardRenderTiming> typeEvents) =>
        CalculateAverageInterval(typeEvents.Select(renderEvent => renderEvent.StartedAtMs).ToArray());

    public static double CalculateAverageInterRenderInterval(IReadOnlyList<NotepadStandardRenderTiming> typeEvents) =>
        CalculateAverageInterval(typeEvents.Select(renderEvent => renderEvent.CompletedAtMs).ToArray());

    private static void ApplyBackspaces(System.Text.StringBuilder text, int count)
    {
        if (count <= 0 || text.Length == 0)
            return;

        text.Length = Math.Max(0, text.Length - count);
    }

    public static double CalculateAverageInterval(IReadOnlyList<double> timestamps)
    {
        if (timestamps.Count < 2)
            return 0;

        var intervals = new double[timestamps.Count - 1];
        for (int i = 1; i < timestamps.Count; i++)
            intervals[i - 1] = Math.Max(0, timestamps[i] - timestamps[i - 1]);

        return intervals.Average();
    }
}
