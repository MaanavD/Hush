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
public sealed class NotepadTranslationE2ETests
{
    private const string ExpectedChineseTranslation =
        "我认为我们应该把设计评审移到周四下午，因为合作伙伴会议已经在日程上了。";

    private const string TranslationPrompt =
        "Translate the following English dictation transcript into Simplified Chinese accurately and naturally.\n\n" +
        "Rules:\n" +
        "- Remove filler words and self-corrections before translating.\n" +
        "- If the speaker says an earlier option and then corrects to a later option, translate only the corrected option.\n" +
        "- Preserve dates, meetings, and named concepts accurately.\n" +
        "- Output only the Chinese translation. No preamble, explanation, quotes, or markdown fences.\n\n" +
        "English transcript:\n{input}";

    private readonly ITestOutputHelper _output;

    public NotepadTranslationE2ETests(ITestOutputHelper output)
        => _output = output;

    [NotepadE2EFact]
    public async Task Translation_streams_raw_preview_replaces_with_chinese_output_once_in_real_notepad()
    {
        using var timeout = new CancellationTokenSource(NotepadE2EOptions.Timeout);
        var corpus = SyntheticAudioCorpus.Load();
        var testCase = corpus.GetCase(NotepadE2EOptions.CaseId);
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
        var metrics = new NotepadTranslationE2EResult
        {
            CaseId = testCase.Id,
            AudioDurationSeconds = testCase.Duration.TotalSeconds,
            ExpectedRawTranscript = testCase.ExpectedTranscript,
            ExpectedTranslation = ExpectedChineseTranslation,
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

            await using var postProcessor = await CreatePostProcessorAsync(timeout.Token);
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
            session.OnCommittedChunk += _ =>
            {
                metrics.CommittedEventCount++;
                metrics.FirstCommitLatencyMs ??= stopwatch.Elapsed.TotalMilliseconds;
            };
            session.OnPostProcessingStateChanged += isProcessing =>
            {
                if (isProcessing)
                    metrics.TranslationStartedMs = stopwatch.Elapsed.TotalMilliseconds;
                else
                    metrics.TranslationFinishedMs = stopwatch.Elapsed.TotalMilliseconds;
            };
            session.OnSessionError += sessionErrors.Add;

            notepad.Focus();
            await Task.Delay(250, timeout.Token);
            stopwatch.Restart();
            await session.StartAsync(
                testCase.Language,
                streamingCommit: true,
                postProcessingPrompt: TranslationPrompt,
                outputMode: DictationOutputMode.CleanStreamingPreview,
                cancellationToken: timeout.Token);

            await capture.Completion.WaitAsync(timeout.Token);
            metrics.AudioStreamingCompletedMs = stopwatch.Elapsed.TotalMilliseconds;
            await session.StopAsync(timeout.Token);
            metrics.SessionCompletedMs = stopwatch.Elapsed.TotalMilliseconds;
            metrics.NotepadFinalText = await notepad.WaitForStableTextAsync(TimeSpan.FromSeconds(5), timeout.Token);
            metrics.RawTranscriptSeenByPostProcessor = postProcessor.RawTranscript;
            metrics.TranslatedText = postProcessor.RewrittenText ?? string.Empty;
            metrics.MeasuredTranslationDurationMs = postProcessor.RewriteDuration.TotalMilliseconds;

            var rawComparison = TranscriptQuality.Compare(testCase.ExpectedTranscript, postProcessor.RawTranscript);
            var finalComparison = TranscriptQuality.Compare(ExpectedChineseTranslation, metrics.NotepadFinalText);
            metrics.RawWer = rawComparison.Wer;
            metrics.RawCer = rawComparison.Cer;
            metrics.TranslationWer = finalComparison.Wer;
            metrics.TranslationCer = finalComparison.Cer;
            metrics.HasSuspiciousDuplicateRun = TranscriptQuality.HasSuspiciousDuplicateRun(metrics.NotepadFinalText);
            metrics.ContainsSpinnerArtifact = TranscriptQuality.ContainsCleanModeSpinnerArtifact(metrics.NotepadFinalText);
            metrics.ContainsEnglishPreviewText = ContainsEnglishPreviewText(metrics.NotepadFinalText);
            metrics.ContainsChineseText = metrics.NotepadFinalText.Any(IsCjkUnifiedIdeograph);
            metrics.ExpectedTranslationOccurrenceCount = CountOccurrences(metrics.NotepadFinalText, ExpectedChineseTranslation);

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
            Assert.True(metrics.ContainsChineseText, $"Final Notepad text does not contain Chinese output: {metrics.NotepadFinalText}");
            Assert.InRange(finalComparison.Cer, 0, NotepadE2EOptions.MaxTranslationCer);
            Assert.InRange(postProcessor.RewriteDuration, TimeSpan.Zero, NotepadE2EOptions.MaxTranslationDuration);
            Assert.False(metrics.HasSuspiciousDuplicateRun, $"Final Notepad text has duplicate/overlap artifacts: {metrics.NotepadFinalText}");
            Assert.False(metrics.ContainsSpinnerArtifact, $"Final Notepad text still contains clean-mode spinner artifacts: {metrics.NotepadFinalText}");
            Assert.False(metrics.ContainsEnglishPreviewText, $"Final Notepad text still contains raw English preview text: {metrics.NotepadFinalText}");
            if (NotepadE2EOptions.UseDeterministicPostProcessor)
            {
                Assert.Equal(ExpectedChineseTranslation, metrics.NotepadFinalText);
                Assert.Equal(1, metrics.ExpectedTranslationOccurrenceCount);
            }
        }
        finally
        {
            capture.Dispose();
        }
    }

    private static async Task<IMeasuredPostProcessor> CreatePostProcessorAsync(CancellationToken cancellationToken)
    {
        if (NotepadE2EOptions.UseDeterministicPostProcessor)
        {
            return new MeasuredDeterministicPostProcessor(
                ExpectedChineseTranslation,
                NotepadE2EOptions.SyntheticRewriteDelay);
        }

        var real = new FoundryPostProcessingService(new NullLogger<FoundryPostProcessingService>());
        var measured = new MeasuredPostProcessor(real);
        await measured.InitializeAsync(NotepadE2EOptions.PostProcessingModel, cancellationToken);
        Assert.True(measured.IsReady, $"Post-processing model '{NotepadE2EOptions.PostProcessingModel}' was not ready.");
        return measured;
    }

    private static bool ContainsEnglishPreviewText(string text)
    {
        var comparison = TranscriptQuality.Compare(string.Empty, text);
        var normalized = comparison.ActualNormalized;
        return normalized.Contains(" um ", StringComparison.Ordinal) ||
               normalized.StartsWith("um ", StringComparison.Ordinal) ||
               normalized.Contains(" design review ", StringComparison.Ordinal) ||
               normalized.Contains(" thursday ", StringComparison.Ordinal) ||
               normalized.Contains(" partner meeting ", StringComparison.Ordinal);
    }

    private static bool IsCjkUnifiedIdeograph(char c) =>
        c is >= '\u4e00' and <= '\u9fff';

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

    private void WriteMetrics(NotepadTranslationE2EResult metrics)
    {
        var json = JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true });
        var resultsDir = Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(resultsDir);
        var resultPath = Path.Combine(resultsDir, "notepad-translation-e2e-result.json");
        File.WriteAllText(resultPath, json);
        _output.WriteLine(json);
        _output.WriteLine($"Result file: {resultPath}");
    }

    private sealed class NotepadTranslationE2EResult
    {
        public string CaseId { get; set; } = string.Empty;

        public double AudioDurationSeconds { get; set; }

        public string ExpectedRawTranscript { get; set; } = string.Empty;

        public string ExpectedTranslation { get; set; } = string.Empty;

        public string PostProcessorMode { get; set; } = string.Empty;

        public string RawTranscriptSeenByPostProcessor { get; set; } = string.Empty;

        public string TranslatedText { get; set; } = string.Empty;

        public string NotepadFinalText { get; set; } = string.Empty;

        public int InterimEventCount { get; set; }

        public int CommittedEventCount { get; set; }

        public string LastInterimText { get; set; } = string.Empty;

        public double? FirstInterimLatencyMs { get; set; }

        public double? FirstCommitLatencyMs { get; set; }

        public double AudioStreamingCompletedMs { get; set; }

        public double? TranslationStartedMs { get; set; }

        public double? TranslationFinishedMs { get; set; }

        public double MeasuredTranslationDurationMs { get; set; }

        public double SessionCompletedMs { get; set; }

        public double RawWer { get; set; }

        public double RawCer { get; set; }

        public double TranslationWer { get; set; }

        public double TranslationCer { get; set; }

        public bool HasSuspiciousDuplicateRun { get; set; }

        public bool ContainsSpinnerArtifact { get; set; }

        public bool ContainsEnglishPreviewText { get; set; }

        public bool ContainsChineseText { get; set; }

        public int ExpectedTranslationOccurrenceCount { get; set; }
    }
}
