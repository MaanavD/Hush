namespace Hush.E2E.Tests;

public sealed class NotepadStandardReportMetricsTests
{
    [Fact]
    public void ReconstructCommittedText_AppliesBackspacesBeforeReplacementDeltas()
    {
        var operations = new[]
        {
            new NotepadStandardCommittedTextOperation(
                NotepadStandardCommittedTextOperationKind.TypeText,
                "hello wurld"),
            new NotepadStandardCommittedTextOperation(
                NotepadStandardCommittedTextOperationKind.Backspace,
                string.Empty,
                BackspaceCount: 5),
            new NotepadStandardCommittedTextOperation(
                NotepadStandardCommittedTextOperationKind.TypeText,
                "world"),
        };

        var reconstructed = NotepadStandardReportMetrics.ReconstructCommittedText(operations);

        Assert.Equal("hello world", reconstructed);
        Assert.NotEqual(string.Concat(operations.Where(operation => operation.Kind == NotepadStandardCommittedTextOperationKind.TypeText).Select(operation => operation.Text)), reconstructed);
    }

    [Fact]
    public void ReconstructCommittedText_AppliesMultipleCorrectionBackspaces()
    {
        var operations = new[]
        {
            new NotepadStandardCommittedTextOperation(
                NotepadStandardCommittedTextOperationKind.TypeText,
                "it is rain"),
            new NotepadStandardCommittedTextOperation(
                NotepadStandardCommittedTextOperationKind.Backspace,
                string.Empty,
                BackspaceCount: 4),
            new NotepadStandardCommittedTextOperation(
                NotepadStandardCommittedTextOperationKind.TypeText,
                "snow"),
            new NotepadStandardCommittedTextOperation(
                NotepadStandardCommittedTextOperationKind.Backspace,
                string.Empty,
                BackspaceCount: 4),
            new NotepadStandardCommittedTextOperation(
                NotepadStandardCommittedTextOperationKind.TypeText,
                "sunny"),
        };

        var reconstructed = NotepadStandardReportMetrics.ReconstructCommittedText(operations);

        Assert.Equal("it is sunny", reconstructed);
    }

    [Fact]
    public void ReconstructCommittedText_ClampsBackspacesAndAppliesReplaceOperations()
    {
        var operations = new[]
        {
            new NotepadStandardCommittedTextOperation(
                NotepadStandardCommittedTextOperationKind.TypeText,
                "abcd"),
            new NotepadStandardCommittedTextOperation(
                NotepadStandardCommittedTextOperationKind.Backspace,
                string.Empty,
                BackspaceCount: 10),
            new NotepadStandardCommittedTextOperation(
                NotepadStandardCommittedTextOperationKind.ReplaceText,
                "fixed",
                BackspaceCount: 2),
        };

        var reconstructed = NotepadStandardReportMetrics.ReconstructCommittedText(operations);

        Assert.Equal("fixed", reconstructed);
    }

    [Fact]
    public void CalculateAverageRenderOutputDuration_UsesRenderCompletionMinusCommitStart()
    {
        var events = new[]
        {
            new NotepadStandardRenderTiming("hello ", 1_000, 1_120),
            new NotepadStandardRenderTiming("world", 1_500, 1_680),
        };

        var average = NotepadStandardReportMetrics.CalculateAverageRenderOutputDuration(events);

        Assert.Equal(150, average);
    }

    [Fact]
    public void CalculateAverageInterEventIntervals_UseCommitStartsAndRenderCompletionsSeparately()
    {
        var events = new[]
        {
            new NotepadStandardRenderTiming("one ", 1_000, 1_050),
            new NotepadStandardRenderTiming("two ", 1_400, 1_520),
            new NotepadStandardRenderTiming("three", 2_100, 2_270),
        };

        var interCommit = NotepadStandardReportMetrics.CalculateAverageInterCommitInterval(events);
        var interRender = NotepadStandardReportMetrics.CalculateAverageInterRenderInterval(events);

        Assert.Equal(550, interCommit);
        Assert.Equal(610, interRender);
    }

    [Fact]
    public void CalculateAverageInterval_UsesCommittedAvailabilityEventsIndependentlyFromPreviewRender()
    {
        var commitAvailabilityMs = new[] { 900d, 1_250d, 2_000d };

        var average = NotepadStandardReportMetrics.CalculateAverageInterval(commitAvailabilityMs);

        Assert.Equal(550, average);
    }

    [Fact]
    public void CalculateAverageEstimatedAudioTokenE2ELatency_UsesEstimatedTokenAudioPosition()
    {
        var events = new[]
        {
            new NotepadStandardRenderTiming("alpha beta", 1_000, 3_000),
        };

        var average = NotepadStandardReportMetrics.CalculateAverageEstimatedAudioTokenE2ELatency(
            events,
            "alpha beta gamma delta",
            TimeSpan.FromSeconds(4));

        Assert.Equal(1_500, average);
    }

    [Fact]
    public void CalculateAverageEstimatedAudioTokenE2ELatency_CanComparePreviewRenderAndCommitAvailability()
    {
        var previewEvents = new[]
        {
            new NotepadStandardRenderTiming("alpha", 400, 500),
            new NotepadStandardRenderTiming(" beta", 1_000, 1_100),
        };
        var commitAvailabilityEvents = new[]
        {
            new NotepadStandardRenderTiming("alpha beta", 2_500, 2_500),
        };

        var previewAverage = NotepadStandardReportMetrics.CalculateAverageEstimatedAudioTokenE2ELatency(
            previewEvents,
            "alpha beta",
            TimeSpan.FromSeconds(2));
        var commitAverage = NotepadStandardReportMetrics.CalculateAverageEstimatedAudioTokenE2ELatency(
            commitAvailabilityEvents,
            "alpha beta",
            TimeSpan.FromSeconds(2));

        Assert.Equal(0, previewAverage);
        Assert.Equal(1_000, commitAverage);
    }
}
