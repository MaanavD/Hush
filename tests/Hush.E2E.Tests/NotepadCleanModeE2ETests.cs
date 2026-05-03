using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Hush.Core.Configuration;
using Hush.Core.Output;
using Hush.Core.PostProcessing;
using Hush.Core.Session;
using Hush.Core.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Hush.E2E.Tests;

[SupportedOSPlatform("windows")]
public sealed class NotepadCleanModeE2ETests
{
    private const string ExpectedCleanFillersCorrection =
        "I think we should move the design review to Thursday afternoon because the partner meeting is already on the calendar.";

    private readonly ITestOutputHelper _output;

    public NotepadCleanModeE2ETests(ITestOutputHelper output)
        => _output = output;

    [NotepadE2EFact]
    public async Task Clean_mode_streams_audio_replaces_preview_once_in_real_notepad()
    {
        using var timeout = new CancellationTokenSource(NotepadE2EOptions.Timeout);
        var corpus = SyntheticAudioCorpus.Load();
        var testCase = corpus.GetCase(NotepadE2EOptions.CaseId);
        var expectedClean = GetExpectedCleanText(testCase.Id);
        var wav = WavPcmFile.Read(corpus.GetAudioPath(testCase));

        await using var notepad = await NotepadTestApp.LaunchAsync(timeout.Token);
        await using var engine = new TranscriptionEngine(new NullLogger<TranscriptionEngine>())
        {
            UnloadTimeout = ModelUnloadTimeout.Never,
        };

        var capture = new WavFileAudioCaptureService(wav, NotepadE2EOptions.DelayScale);
        var textOutput = new NotepadTargetedTextOutputService(
            notepad,
            new KeystrokeTypingService(
                new NullLogger<KeystrokeTypingService>(),
                useClipboardFallback: false));
        var metrics = new NotepadE2EResult
        {
            CaseId = testCase.Id,
            AudioDurationSeconds = testCase.Duration.TotalSeconds,
            ExpectedRawTranscript = testCase.ExpectedTranscript,
            ExpectedCleanTranscript = expectedClean,
            PostProcessorMode = NotepadE2EOptions.UseDeterministicPostProcessor ? "deterministic" : "real-foundry",
        };
        var stopwatch = Stopwatch.StartNew();
        var sessionErrors = new List<Exception>();

        try
        {
            notepad.Focus();
            await engine.InitializeAsync(
                AudioE2EOptions.ModelAlias,
                downloadHardwareEPs: false,
                cancellationToken: timeout.Token);

            await using var postProcessor = await CreatePostProcessorAsync(expectedClean, timeout.Token);
            await using var session = new DictationSession(
                engine,
                capture,
                textOutput,
                new NullLogger<DictationSession>(),
                postProcessor: postProcessor);

            session.OnInterimText += text =>
            {
                metrics.InterimEventCount++;
                metrics.LastInterimText = text;
                metrics.FirstInterimLatencyMs ??= stopwatch.Elapsed.TotalMilliseconds;
            };
            session.OnCommittedChunk += text =>
            {
                metrics.CommittedEventCount++;
                metrics.FirstCommitLatencyMs ??= stopwatch.Elapsed.TotalMilliseconds;
            };
            session.OnPostProcessingStateChanged += isProcessing =>
            {
                if (isProcessing)
                    metrics.PostProcessingStartedMs = stopwatch.Elapsed.TotalMilliseconds;
                else
                    metrics.PostProcessingFinishedMs = stopwatch.Elapsed.TotalMilliseconds;
            };
            session.OnSessionError += sessionErrors.Add;

            notepad.Focus();
            await Task.Delay(250, timeout.Token);
            stopwatch.Restart();
            await session.StartAsync(
                testCase.Language,
                streamingCommit: true,
                postProcessingPrompt: HushSettings.BuiltInPrompts.First(p => p.Id == "clean-dictation").Prompt,
                outputMode: DictationOutputMode.CleanStreamingPreview,
                cancellationToken: timeout.Token);

            await capture.Completion.WaitAsync(timeout.Token);
            metrics.AudioStreamingCompletedMs = stopwatch.Elapsed.TotalMilliseconds;
            await session.StopAsync(timeout.Token);
            metrics.SessionCompletedMs = stopwatch.Elapsed.TotalMilliseconds;
            metrics.NotepadFinalText = await notepad.WaitForStableTextAsync(TimeSpan.FromSeconds(5), timeout.Token);
            metrics.RawTranscriptSeenByPostProcessor = postProcessor.RawTranscript;
            metrics.RewrittenText = postProcessor.RewrittenText ?? string.Empty;
            metrics.MeasuredRewriteDurationMs = postProcessor.RewriteDuration.TotalMilliseconds;

            var rawComparison = TranscriptQuality.Compare(testCase.ExpectedTranscript, postProcessor.RawTranscript);
            var finalComparison = TranscriptQuality.Compare(expectedClean, metrics.NotepadFinalText);
            metrics.RawWer = rawComparison.Wer;
            metrics.RawCer = rawComparison.Cer;
            metrics.FinalWer = finalComparison.Wer;
            metrics.FinalCer = finalComparison.Cer;
            metrics.HasSuspiciousDuplicateRun = TranscriptQuality.HasSuspiciousDuplicateRun(metrics.NotepadFinalText);
            metrics.FinalOverlapRate = TranscriptQuality.WordOverlapRate(expectedClean, metrics.NotepadFinalText);
            var artifactAnalysis = TranscriptQuality.AnalyzeRenderedArtifacts(expectedClean, metrics.NotepadFinalText);
            metrics.ContainsSpinnerArtifact = artifactAnalysis.ContainsSpinnerArtifact;
            metrics.ContainsContaminationArtifact = artifactAnalysis.ContainsContaminationArtifact;
            metrics.ContaminationArtifactReason = artifactAnalysis.ContaminationArtifactReason;
            metrics.ContainsRawPreviewFiller = ContainsRawPreviewFiller(metrics.NotepadFinalText);
            metrics.ExpectedCleanOccurrenceCount = CountOccurrences(metrics.NotepadFinalText, expectedClean);

            WriteMetrics(metrics);

            Assert.Empty(sessionErrors);
            Assert.NotNull(metrics.FirstInterimLatencyMs);
            Assert.NotNull(metrics.FirstCommitLatencyMs);
            Assert.InRange(
                TimeSpan.FromMilliseconds(metrics.FirstInterimLatencyMs.Value),
                TimeSpan.Zero,
                NotepadE2EOptions.MaxFirstInterimLatency);
            Assert.InRange(
                TimeSpan.FromMilliseconds(metrics.FirstCommitLatencyMs.Value),
                TimeSpan.Zero,
                NotepadE2EOptions.MaxFirstCommitLatency);
            Assert.InRange(rawComparison.Wer, 0, NotepadE2EOptions.MaxRawWer);
            Assert.InRange(finalComparison.Wer, 0, NotepadE2EOptions.MaxFinalWer);
            Assert.InRange(finalComparison.Cer, 0, NotepadE2EOptions.MaxFinalCer);
            Assert.InRange(postProcessor.RewriteDuration, TimeSpan.Zero, NotepadE2EOptions.MaxRewriteDuration);
            Assert.False(metrics.HasSuspiciousDuplicateRun, $"Final Notepad text has duplicate/overlap artifacts: {metrics.NotepadFinalText}");
            Assert.False(metrics.ContainsSpinnerArtifact, $"Final Notepad text still contains clean-mode spinner artifacts: {metrics.NotepadFinalText}");
            Assert.False(
                metrics.ContainsContaminationArtifact,
                $"Final Notepad text contains likely foreign/stale output contamination ({metrics.ContaminationArtifactReason}): {metrics.NotepadFinalText}");
            Assert.False(metrics.ContainsRawPreviewFiller, $"Final Notepad text still contains raw preview filler text: {metrics.NotepadFinalText}");
            if (NotepadE2EOptions.UseDeterministicPostProcessor)
            {
                Assert.Equal(expectedClean, metrics.NotepadFinalText);
                Assert.Equal(1, metrics.ExpectedCleanOccurrenceCount);
            }
        }
        finally
        {
            capture.Dispose();
        }
    }

    private static async Task<IMeasuredPostProcessor> CreatePostProcessorAsync(
        string expectedClean,
        CancellationToken cancellationToken)
    {
        if (NotepadE2EOptions.UseDeterministicPostProcessor)
        {
            return new MeasuredDeterministicPostProcessor(
                expectedClean,
                NotepadE2EOptions.SyntheticRewriteDelay);
        }

        var real = new FoundryPostProcessingService(new NullLogger<FoundryPostProcessingService>());
        var measured = new MeasuredPostProcessor(real);
        await measured.InitializeAsync(NotepadE2EOptions.PostProcessingModel, cancellationToken);
        Assert.True(measured.IsReady, $"Post-processing model '{NotepadE2EOptions.PostProcessingModel}' was not ready.");
        return measured;
    }

    private static string GetExpectedCleanText(string caseId) =>
        string.Equals(caseId, "en_rt_003_fillers_correction", StringComparison.OrdinalIgnoreCase)
            ? ExpectedCleanFillersCorrection
            : throw new InvalidOperationException(
                $"No clean-mode Notepad expectation is defined for '{caseId}'. Set HUSH_NOTEPAD_E2E_CASE to a supported case or add an expectation.");

    private static bool ContainsRawPreviewFiller(string text)
    {
        var comparison = TranscriptQuality.Compare(string.Empty, text);
        var normalized = comparison.ActualNormalized;
        return normalized.Contains(" um ", StringComparison.Ordinal) ||
               normalized.StartsWith("um ", StringComparison.Ordinal) ||
               normalized.Contains(" uh ", StringComparison.Ordinal) ||
               normalized.Contains(" thursday morning ", StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private void WriteMetrics(NotepadE2EResult metrics)
    {
        var json = JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true });
        var resultsDir = Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(resultsDir);
        var resultPath = Path.Combine(resultsDir, "notepad-clean-mode-e2e-result.json");
        File.WriteAllText(resultPath, json);
        _output.WriteLine(json);
        _output.WriteLine($"Result file: {resultPath}");
    }

    private sealed class NotepadE2EResult
    {
        public string CaseId { get; set; } = string.Empty;

        public double AudioDurationSeconds { get; set; }

        public string ExpectedRawTranscript { get; set; } = string.Empty;

        public string ExpectedCleanTranscript { get; set; } = string.Empty;

        public string PostProcessorMode { get; set; } = string.Empty;

        public string RawTranscriptSeenByPostProcessor { get; set; } = string.Empty;

        public string RewrittenText { get; set; } = string.Empty;

        public string NotepadFinalText { get; set; } = string.Empty;

        public int InterimEventCount { get; set; }

        public int CommittedEventCount { get; set; }

        public string LastInterimText { get; set; } = string.Empty;

        public double? FirstInterimLatencyMs { get; set; }

        public double? FirstCommitLatencyMs { get; set; }

        public double AudioStreamingCompletedMs { get; set; }

        public double? PostProcessingStartedMs { get; set; }

        public double? PostProcessingFinishedMs { get; set; }

        public double MeasuredRewriteDurationMs { get; set; }

        public double SessionCompletedMs { get; set; }

        public double RawWer { get; set; }

        public double RawCer { get; set; }

        public double FinalWer { get; set; }

        public double FinalCer { get; set; }

        public double FinalOverlapRate { get; set; }

        public bool HasSuspiciousDuplicateRun { get; set; }

        public bool ContainsSpinnerArtifact { get; set; }

        public bool ContainsContaminationArtifact { get; set; }

        public string ContaminationArtifactReason { get; set; } = string.Empty;

        public bool ContainsRawPreviewFiller { get; set; }

        public int ExpectedCleanOccurrenceCount { get; set; }
    }
}
