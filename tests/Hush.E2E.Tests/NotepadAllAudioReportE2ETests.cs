using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
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
public sealed class NotepadAllAudioReportE2ETests
{
    private const string CleanDictationPromptId = "clean-dictation";
    private const string ExpectedCleanFillersCorrection =
        "I think we should move the design review to Thursday afternoon because the partner meeting is already on the calendar.";

    private readonly ITestOutputHelper _output;

    public NotepadAllAudioReportE2ETests(ITestOutputHelper output)
        => _output = output;

    [NotepadReportE2EFact]
    public async Task All_synthetic_audio_files_generate_notepad_accuracy_latency_and_overlap_report()
    {
        using var timeout = new CancellationTokenSource(NotepadE2EOptions.Timeout);
        var corpus = SyntheticAudioCorpus.Load();
        var reportCases = NotepadE2EOptions.FilterReportCases(corpus.Cases);
        var results = new List<NotepadAudioReportRow>();
        var lockedDesktop = WindowsInteractiveDesktop.IsLocked();

        foreach (var testCase in reportCases.OrderBy(testCase => testCase.Id, StringComparer.Ordinal))
        {
            var result = await RunCaseAsync(corpus, testCase, lockedDesktop, timeout.Token);
            results.Add(result);
            _output.WriteLine(
                $"{result.CaseId}: rawWER={result.RawWer:P1}, finalWER={result.FinalWer:P1}, " +
                $"avgPreviewTokenLatency={result.AveragePreviewTokenRenderLatencyMs:F0}ms, " +
                $"avgFinalTokenLatency={result.AverageFinalTokenRenderLatencyMs:F0}ms, " +
                $"overlap={result.PostProcessingRenderOverlapRate:P1}");
        }

        var report = NotepadAllAudioReport.Create(results);
        WriteReport(report);

        Assert.All(results, result => Assert.True(result.Completed, result.ErrorMessage));
    }

    private static async Task<NotepadAudioReportRow> RunCaseAsync(
        SyntheticAudioCorpus corpus,
        SyntheticAudioCase testCase,
        bool lockedDesktop,
        CancellationToken cancellationToken)
    {
        var expectedRenderedText = GetExpectedRenderedText(testCase);
        var wav = WavPcmFile.Read(corpus.GetAudioPath(testCase));
        var stopwatch = Stopwatch.StartNew();
        var sessionErrors = new List<Exception>();
        var row = new NotepadAudioReportRow
        {
            CaseId = testCase.Id,
            Tags = string.Join(";", testCase.Tags),
            AudioDurationSeconds = testCase.Duration.TotalSeconds,
            ExpectedRawTranscript = testCase.ExpectedTranscript,
            ExpectedRenderedText = expectedRenderedText,
            PostProcessorMode = "deterministic-expected-render",
            DelayScale = NotepadE2EOptions.DelayScale,
            DesktopLocked = lockedDesktop,
            RenderBackend = lockedDesktop ? "locked-file-backed-notepad-document" : "interactive-notepad-sendinput",
        };

        try
        {
            await using var notepad = await NotepadTestApp.LaunchAsync(
                cancellationToken,
                requireInteractiveWindow: !lockedDesktop);
            await using var engine = new TranscriptionEngine(new NullLogger<TranscriptionEngine>())
            {
                UnloadTimeout = ModelUnloadTimeout.Never,
            };

            var capture = new WavFileAudioCaptureService(wav, NotepadE2EOptions.DelayScale);
            ITextOutputService innerOutput = lockedDesktop
                ? new LockedNotepadDocumentOutputService(notepad)
                : new NotepadTargetedTextOutputService(
                    notepad,
                    new KeystrokeTypingService(
                        new NullLogger<KeystrokeTypingService>(),
                        useClipboardFallback: false));
            var measuredOutput = new MeasuringTextOutputService(innerOutput, stopwatch);
            await using var postProcessor = new MeasuredDeterministicPostProcessor(
                expectedRenderedText,
                NotepadE2EOptions.SyntheticRewriteDelay);
            await using var session = new DictationSession(
                engine,
                capture,
                measuredOutput,
                new NullLogger<DictationSession>(),
                postProcessor: postProcessor);

            session.OnInterimText += text =>
            {
                row.InterimEventCount++;
                row.LastInterimText = text;
                row.FirstInterimLatencyMs ??= stopwatch.Elapsed.TotalMilliseconds;
            };
            session.OnCommittedChunk += text =>
            {
                row.CommittedEventCount++;
                row.FirstCommitLatencyMs ??= stopwatch.Elapsed.TotalMilliseconds;
            };
            session.OnPostProcessingStateChanged += isProcessing =>
            {
                if (isProcessing)
                    row.PostProcessingStartedMs = stopwatch.Elapsed.TotalMilliseconds;
                else
                    row.PostProcessingFinishedMs = stopwatch.Elapsed.TotalMilliseconds;
            };
            session.OnSessionError += sessionErrors.Add;

            if (!lockedDesktop)
                notepad.Focus();
            await engine.InitializeAsync(
                AudioE2EOptions.ModelAlias,
                downloadHardwareEPs: false,
                cancellationToken: cancellationToken);

            await Task.Delay(250, cancellationToken);
            if (!lockedDesktop)
                notepad.Focus();
            stopwatch.Restart();
            await session.StartAsync(
                testCase.Language,
                streamingCommit: true,
                postProcessingPrompt: HushSettings.BuiltInPrompts.First(p => p.Id == CleanDictationPromptId).Prompt,
                outputMode: DictationOutputMode.CleanStreamingPreview,
                cancellationToken: cancellationToken);

            await capture.Completion.WaitAsync(cancellationToken);
            row.AudioStreamingCompletedMs = stopwatch.Elapsed.TotalMilliseconds;
            await session.StopAsync(cancellationToken);
            row.SessionCompletedMs = stopwatch.Elapsed.TotalMilliseconds;
            row.NotepadFinalText = lockedDesktop
                ? await notepad.ReadFileTextAsync(cancellationToken)
                : await notepad.WaitForStableTextAsync(TimeSpan.FromSeconds(5), cancellationToken);
            row.RawTranscriptSeenByPostProcessor = postProcessor.RawTranscript;
            row.RewrittenText = postProcessor.RewrittenText ?? string.Empty;
            row.PostProcessingDurationMs = postProcessor.RewriteDuration.TotalMilliseconds;
            row.PreviewRenderEventCount = measuredOutput.PreviewEvents.Count;
            row.FirstPreviewRenderLatencyMs = measuredOutput.PreviewEvents.FirstOrDefault()?.CompletedAtMs;
            row.FirstPreviewRenderBackpressureMs = row.FirstInterimLatencyMs.HasValue && row.FirstPreviewRenderLatencyMs.HasValue
                ? Math.Max(0, row.FirstPreviewRenderLatencyMs.Value - row.FirstInterimLatencyMs.Value)
                : null;
            row.FinalReplaceRenderMs = measuredOutput.ReplaceEvents.LastOrDefault()?.CompletedAtMs;

            var rawComparison = TranscriptQuality.Compare(testCase.ExpectedTranscript, postProcessor.RawTranscript);
            var finalComparison = TranscriptQuality.Compare(expectedRenderedText, row.NotepadFinalText);
            row.RawWer = rawComparison.Wer;
            row.RawCer = rawComparison.Cer;
            row.FinalWer = finalComparison.Wer;
            row.FinalCer = finalComparison.Cer;
            row.ExpectedRenderedTokenCount = TranscriptQuality.CountTokens(expectedRenderedText);
            row.NotepadRenderedTokenCount = TranscriptQuality.CountTokens(row.NotepadFinalText);
            row.PostProcessingRenderOverlapRate = TranscriptQuality.WordOverlapRate(expectedRenderedText, row.NotepadFinalText);
            row.RenderedToExpectedTokenRatio = TranscriptQuality.RenderedToExpectedTokenRatio(expectedRenderedText, row.NotepadFinalText);
            row.HasSuspiciousDuplicateRun = TranscriptQuality.HasSuspiciousDuplicateRun(row.NotepadFinalText);
            var artifactAnalysis = TranscriptQuality.AnalyzeRenderedArtifacts(expectedRenderedText, row.NotepadFinalText);
            row.ContainsSpinnerArtifact = artifactAnalysis.ContainsSpinnerArtifact;
            row.ContainsContaminationArtifact = artifactAnalysis.ContainsContaminationArtifact;
            row.ContaminationArtifactReason = artifactAnalysis.ContaminationArtifactReason;
            row.AveragePreviewTokenRenderLatencyMs = CalculateAveragePreviewTokenLatency(
                measuredOutput.PreviewEvents,
                testCase.ExpectedTranscript,
                testCase.Duration);
            row.AverageFinalTokenRenderLatencyMs = CalculateAverageFinalTokenLatency(
                measuredOutput.ReplaceEvents.LastOrDefault()?.CompletedAtMs,
                expectedRenderedText,
                testCase.Duration);
            row.Completed = sessionErrors.Count == 0;
            row.ErrorMessage = string.Join(" | ", sessionErrors.Select(error => error.Message));
        }
        catch (Exception ex)
        {
            row.Completed = false;
            row.ErrorMessage = ex.ToString();
        }

        return row;
    }

    private static string GetExpectedRenderedText(SyntheticAudioCase testCase) =>
        string.Equals(testCase.Id, "en_rt_003_fillers_correction", StringComparison.OrdinalIgnoreCase)
            ? ExpectedCleanFillersCorrection
            : testCase.ExpectedTranscript;

    private static double CalculateAveragePreviewTokenLatency(
        IReadOnlyList<TextRenderEvent> typeEvents,
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

    private static double CalculateAverageFinalTokenLatency(
        double? finalRenderCompletedMs,
        string expectedRenderedText,
        TimeSpan audioDuration)
    {
        if (finalRenderCompletedMs is null)
            return 0;

        int expectedTokenCount = Math.Max(1, TranscriptQuality.CountTokens(expectedRenderedText));
        var latencies = new double[expectedTokenCount];
        for (int i = 0; i < expectedTokenCount; i++)
        {
            var estimatedVoiceInputMs = audioDuration.TotalMilliseconds * (i + 1) / expectedTokenCount;
            latencies[i] = Math.Max(0, finalRenderCompletedMs.Value - estimatedVoiceInputMs);
        }

        return latencies.Average();
    }

    private void WriteReport(NotepadAllAudioReport report)
    {
        var resultsDir = Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(resultsDir);

        var jsonPath = Path.Combine(resultsDir, "notepad-all-audio-report.json");
        var csvPath = Path.Combine(resultsDir, "notepad-all-audio-report.csv");
        var markdownPath = Path.Combine(resultsDir, "notepad-all-audio-report.md");

        File.WriteAllText(
            jsonPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(csvPath, BuildCsv(report.Rows));
        File.WriteAllText(markdownPath, BuildMarkdown(report));

        _output.WriteLine($"JSON report: {jsonPath}");
        _output.WriteLine($"CSV report: {csvPath}");
        _output.WriteLine($"Markdown report: {markdownPath}");
    }

    private static string BuildCsv(IReadOnlyList<NotepadAudioReportRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "case_id,tags,audio_seconds,raw_wer,final_wer,avg_preview_token_latency_ms,avg_final_token_latency_ms,first_interim_latency_ms,first_preview_render_latency_ms,first_preview_render_backpressure_ms,postprocessing_ms,render_overlap_rate,rendered_to_expected_token_ratio,expected_tokens,rendered_tokens,desktop_locked,render_backend,duplicate_artifact,spinner_artifact,contamination_artifact,contamination_reason,completed,error");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(
                ',',
                Csv(row.CaseId),
                Csv(row.Tags),
                Number(row.AudioDurationSeconds),
                Number(row.RawWer),
                Number(row.FinalWer),
                Number(row.AveragePreviewTokenRenderLatencyMs),
                Number(row.AverageFinalTokenRenderLatencyMs),
                NullableNumber(row.FirstInterimLatencyMs),
                NullableNumber(row.FirstPreviewRenderLatencyMs),
                NullableNumber(row.FirstPreviewRenderBackpressureMs),
                Number(row.PostProcessingDurationMs),
                Number(row.PostProcessingRenderOverlapRate),
                Number(row.RenderedToExpectedTokenRatio),
                row.ExpectedRenderedTokenCount.ToString(CultureInfo.InvariantCulture),
                row.NotepadRenderedTokenCount.ToString(CultureInfo.InvariantCulture),
                row.DesktopLocked.ToString(CultureInfo.InvariantCulture),
                Csv(row.RenderBackend),
                row.HasSuspiciousDuplicateRun.ToString(CultureInfo.InvariantCulture),
                row.ContainsSpinnerArtifact.ToString(CultureInfo.InvariantCulture),
                row.ContainsContaminationArtifact.ToString(CultureInfo.InvariantCulture),
                Csv(row.ContaminationArtifactReason),
                row.Completed.ToString(CultureInfo.InvariantCulture),
                Csv(row.ErrorMessage)));
        }

        return sb.ToString();
    }

    private static string BuildMarkdown(NotepadAllAudioReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Notepad all-audio E2E report");
        sb.AppendLine();
        sb.AppendLine($"- Cases: {report.TotalCases}");
        sb.AppendLine($"- Completed: {report.CompletedCases}");
        sb.AppendLine($"- Average raw WER: {report.AverageRawWer:P2}");
        sb.AppendLine($"- Average final WER: {report.AverageFinalWer:P2}");
        sb.AppendLine($"- Average preview token render latency: {report.AveragePreviewTokenRenderLatencyMs:F0} ms");
        sb.AppendLine($"- Average final token render latency: {report.AverageFinalTokenRenderLatencyMs:F0} ms");
        sb.AppendLine($"- Average ASR first-interim latency: {report.AverageFirstInterimLatencyMs:F0} ms");
        sb.AppendLine($"- Average first preview render latency: {report.AverageFirstPreviewRenderLatencyMs:F0} ms");
        sb.AppendLine($"- Average first preview render backpressure: {report.AverageFirstPreviewRenderBackpressureMs:F0} ms");
        sb.AppendLine($"- Average post-processing render overlap: {report.AveragePostProcessingRenderOverlapRate:P2}");
        sb.AppendLine($"- Average rendered/expected token ratio: {report.AverageRenderedToExpectedTokenRatio:P2}");
        sb.AppendLine($"- Spinner artifact cases: {report.SpinnerArtifactCases}");
        sb.AppendLine($"- Contamination artifact cases: {report.ContaminationArtifactCases}");
        sb.AppendLine($"- Render backend: {string.Join(", ", report.Rows.Select(row => row.RenderBackend).Distinct(StringComparer.Ordinal))}");
        sb.AppendLine();
        sb.AppendLine("| Case | Raw WER | Final WER | Preview token latency | ASR first interim | Preview render delay | Final token latency | Overlap | Rendered/expected | Artifacts | Backend | Completed |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- | --- |");
        foreach (var row in report.Rows)
        {
            var artifacts = BuildArtifactSummary(row);
            sb.AppendLine(
                $"| {row.CaseId} | {row.RawWer:P1} | {row.FinalWer:P1} | {row.AveragePreviewTokenRenderLatencyMs:F0} ms | " +
                $"{FormatNullableMs(row.FirstInterimLatencyMs)} | {FormatNullableMs(row.FirstPreviewRenderBackpressureMs)} | " +
                $"{row.AverageFinalTokenRenderLatencyMs:F0} ms | {row.PostProcessingRenderOverlapRate:P1} | " +
                $"{row.RenderedToExpectedTokenRatio:P1} | {artifacts} | {row.RenderBackend} | {row.Completed} |");
        }

        return sb.ToString();
    }

    private static string BuildArtifactSummary(NotepadAudioReportRow row)
    {
        var artifacts = new List<string>();
        if (row.HasSuspiciousDuplicateRun)
            artifacts.Add("duplicate");
        if (row.ContainsSpinnerArtifact)
            artifacts.Add("spinner");
        if (row.ContainsContaminationArtifact)
            artifacts.Add($"contamination:{row.ContaminationArtifactReason}");

        return artifacts.Count == 0 ? "none" : string.Join("; ", artifacts);
    }

    private static string Csv(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Number(double value) =>
        double.IsInfinity(value) || double.IsNaN(value)
            ? value.ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string NullableNumber(double? value) =>
        value.HasValue ? Number(value.Value) : string.Empty;

    private static string FormatNullableMs(double? value) =>
        value.HasValue ? $"{value.Value:F0} ms" : "n/a";

    private sealed class MeasuringTextOutputService : IFullBufferFinalReplacementOutputService, IPreviewTextReplacementOutputService
    {
        private readonly ITextOutputService _inner;
        private readonly Stopwatch _stopwatch;

        public MeasuringTextOutputService(ITextOutputService inner, Stopwatch stopwatch)
        {
            _inner = inner;
            _stopwatch = stopwatch;
        }

        public List<TextRenderEvent> TypeEvents { get; } = new();

        public List<TextRenderEvent> PreviewEvents { get; } = new();

        public List<TextRenderEvent> ReplaceEvents { get; } = new();

        public bool PreferFullBufferFinalReplacement =>
            _inner is IFullBufferFinalReplacementOutputService { PreferFullBufferFinalReplacement: true };

        public async Task TypeTextAsync(string text, CancellationToken cancellationToken = default, bool skipModifierRestore = false)
        {
            await _inner.TypeTextAsync(text, cancellationToken, skipModifierRestore);
            TypeEvents.Add(new TextRenderEvent(TextRenderKind.Type, text, _stopwatch.Elapsed.TotalMilliseconds));
        }

        public async Task TypePreviewTextAsync(string text, CancellationToken cancellationToken = default, bool skipModifierRestore = false)
        {
            if (_inner is IPreviewTextOutputService previewOutput)
                await previewOutput.TypePreviewTextAsync(text, cancellationToken, skipModifierRestore);
            else
                await _inner.TypeTextAsync(text, cancellationToken, skipModifierRestore);

            PreviewEvents.Add(new TextRenderEvent(TextRenderKind.Type, text, _stopwatch.Elapsed.TotalMilliseconds));
        }

        public async Task ReplacePreviewTextAsync(
            string currentText,
            string targetText,
            CancellationToken cancellationToken = default,
            bool skipModifierRestore = false)
        {
            if (_inner is IPreviewTextReplacementOutputService previewReplacementOutput)
            {
                await previewReplacementOutput.ReplacePreviewTextAsync(
                    currentText,
                    targetText,
                    cancellationToken,
                    skipModifierRestore);
            }
            else
            {
                int commonPrefixLength = CommonPrefixLength(currentText, targetText);
                int backspaceCount = currentText.Length - commonPrefixLength;
                var delta = targetText[commonPrefixLength..];
                if (backspaceCount > 0)
                    await _inner.SendBackspacesAsync(backspaceCount, cancellationToken, skipModifierRestore);
                if (!string.IsNullOrEmpty(delta))
                {
                    if (_inner is IPreviewTextOutputService previewOutput)
                        await previewOutput.TypePreviewTextAsync(delta, cancellationToken, skipModifierRestore);
                    else
                        await _inner.TypeTextAsync(delta, cancellationToken, skipModifierRestore);
                }
            }

            PreviewEvents.Add(new TextRenderEvent(
                TextRenderKind.Replace,
                PreviewRenderDelta(currentText, targetText),
                _stopwatch.Elapsed.TotalMilliseconds));
        }

        public Task SendBackspacesAsync(int count, CancellationToken cancellationToken = default, bool skipModifierRestore = false) =>
            _inner.SendBackspacesAsync(count, cancellationToken, skipModifierRestore);

        public async Task ReplaceTextAsync(
            int backspaceCount,
            string replacementText,
            CancellationToken cancellationToken = default,
            bool skipModifierRestore = false,
            bool boundToCurrentLine = false,
            string? expectedExistingText = null,
            bool allowFullBufferReplacement = false,
            TextReplacementKind replacementKind = TextReplacementKind.FinalSynchronization)
        {
            await _inner.ReplaceTextAsync(
                backspaceCount,
                replacementText,
                cancellationToken,
                skipModifierRestore,
                boundToCurrentLine,
                expectedExistingText,
                allowFullBufferReplacement,
                replacementKind);
            if (replacementKind == TextReplacementKind.Preview)
                PreviewEvents.Add(new TextRenderEvent(TextRenderKind.Replace, replacementText, _stopwatch.Elapsed.TotalMilliseconds));
            else
                ReplaceEvents.Add(new TextRenderEvent(TextRenderKind.Replace, replacementText, _stopwatch.Elapsed.TotalMilliseconds));
        }

        public Task SendKeyAsync(AutoSubmitKey key, CancellationToken cancellationToken = default) =>
            _inner.SendKeyAsync(key, cancellationToken);

        private static string PreviewRenderDelta(string currentText, string targetText)
        {
            int commonPrefixLength = CommonPrefixLength(currentText, targetText);
            return targetText[commonPrefixLength..];
        }

        private static int CommonPrefixLength(string left, string right)
        {
            int length = Math.Min(left.Length, right.Length);
            int index = 0;
            while (index < length && left[index] == right[index])
                index++;
            return index;
        }
    }

    private sealed class LockedNotepadDocumentOutputService : IPreviewTextOutputService
    {
        private readonly NotepadTestApp _notepad;
        private string _text = string.Empty;

        public LockedNotepadDocumentOutputService(NotepadTestApp notepad)
            => _notepad = notepad;

        public async Task TypeTextAsync(string text, CancellationToken cancellationToken = default, bool skipModifierRestore = false)
        {
            _text += text;
            await _notepad.WriteFileTextAsync(_text, cancellationToken);
        }

        public Task TypePreviewTextAsync(string text, CancellationToken cancellationToken = default, bool skipModifierRestore = false) =>
            TypeTextAsync(text, cancellationToken, skipModifierRestore);

        public async Task SendBackspacesAsync(int count, CancellationToken cancellationToken = default, bool skipModifierRestore = false)
        {
            if (count > 0)
            {
                _text = _text[..Math.Max(0, _text.Length - count)];
                await _notepad.WriteFileTextAsync(_text, cancellationToken);
            }
        }

        public async Task ReplaceTextAsync(
            int backspaceCount,
            string replacementText,
            CancellationToken cancellationToken = default,
            bool skipModifierRestore = false,
            bool boundToCurrentLine = false,
            string? expectedExistingText = null,
            bool allowFullBufferReplacement = false,
            TextReplacementKind replacementKind = TextReplacementKind.FinalSynchronization)
        {
            if (allowFullBufferReplacement)
            {
                _text = replacementText;
            }
            else if (!string.IsNullOrEmpty(expectedExistingText) &&
                _text.EndsWith(expectedExistingText, StringComparison.Ordinal))
            {
                _text = _text[..^expectedExistingText.Length] + replacementText;
            }
            else
            {
                _text = _text[..Math.Max(0, _text.Length - Math.Max(0, backspaceCount))] + replacementText;
            }

            await _notepad.WriteFileTextAsync(_text, cancellationToken);
        }

        public Task SendKeyAsync(AutoSubmitKey key, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed record TextRenderEvent(TextRenderKind Kind, string Text, double CompletedAtMs);

    private enum TextRenderKind
    {
        Type,
        Replace,
    }

    private sealed class NotepadAllAudioReport
    {
        public required IReadOnlyList<NotepadAudioReportRow> Rows { get; init; }

        public int TotalCases { get; init; }

        public int CompletedCases { get; init; }

        public double AverageRawWer { get; init; }

        public double AverageFinalWer { get; init; }

        public double AveragePreviewTokenRenderLatencyMs { get; init; }

        public double AverageFinalTokenRenderLatencyMs { get; init; }

        public double AverageFirstInterimLatencyMs { get; init; }

        public double AverageFirstPreviewRenderLatencyMs { get; init; }

        public double AverageFirstPreviewRenderBackpressureMs { get; init; }

        public double AveragePostProcessingRenderOverlapRate { get; init; }

        public double AverageRenderedToExpectedTokenRatio { get; init; }

        public int SpinnerArtifactCases { get; init; }

        public int ContaminationArtifactCases { get; init; }

        public static NotepadAllAudioReport Create(IReadOnlyList<NotepadAudioReportRow> rows)
        {
            var completed = rows.Where(row => row.Completed).ToArray();
            return new NotepadAllAudioReport
            {
                Rows = rows,
                TotalCases = rows.Count,
                CompletedCases = completed.Length,
                AverageRawWer = Average(completed, row => row.RawWer),
                AverageFinalWer = Average(completed, row => row.FinalWer),
                AveragePreviewTokenRenderLatencyMs = Average(completed, row => row.AveragePreviewTokenRenderLatencyMs),
                AverageFinalTokenRenderLatencyMs = Average(completed, row => row.AverageFinalTokenRenderLatencyMs),
                AverageFirstInterimLatencyMs = AverageNullable(completed, row => row.FirstInterimLatencyMs),
                AverageFirstPreviewRenderLatencyMs = AverageNullable(completed, row => row.FirstPreviewRenderLatencyMs),
                AverageFirstPreviewRenderBackpressureMs = AverageNullable(completed, row => row.FirstPreviewRenderBackpressureMs),
                AveragePostProcessingRenderOverlapRate = Average(completed, row => row.PostProcessingRenderOverlapRate),
                AverageRenderedToExpectedTokenRatio = Average(completed, row => row.RenderedToExpectedTokenRatio),
                SpinnerArtifactCases = rows.Count(row => row.ContainsSpinnerArtifact),
                ContaminationArtifactCases = rows.Count(row => row.ContainsContaminationArtifact),
            };
        }

        private static double Average(IReadOnlyList<NotepadAudioReportRow> rows, Func<NotepadAudioReportRow, double> selector) =>
            rows.Count == 0 ? 0 : rows.Average(selector);

        private static double AverageNullable(IReadOnlyList<NotepadAudioReportRow> rows, Func<NotepadAudioReportRow, double?> selector)
        {
            var values = rows
                .Select(selector)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToArray();
            return values.Length == 0 ? 0 : values.Average();
        }
    }

    private sealed class NotepadAudioReportRow
    {
        public string CaseId { get; set; } = string.Empty;

        public string Tags { get; set; } = string.Empty;

        public double AudioDurationSeconds { get; set; }

        public string ExpectedRawTranscript { get; set; } = string.Empty;

        public string ExpectedRenderedText { get; set; } = string.Empty;

        public string PostProcessorMode { get; set; } = string.Empty;

        public double DelayScale { get; set; }

        public bool DesktopLocked { get; set; }

        public string RenderBackend { get; set; } = string.Empty;

        public string RawTranscriptSeenByPostProcessor { get; set; } = string.Empty;

        public string RewrittenText { get; set; } = string.Empty;

        public string NotepadFinalText { get; set; } = string.Empty;

        public int InterimEventCount { get; set; }

        public int CommittedEventCount { get; set; }

        public int PreviewRenderEventCount { get; set; }

        public string LastInterimText { get; set; } = string.Empty;

        public double? FirstInterimLatencyMs { get; set; }

        public double? FirstCommitLatencyMs { get; set; }

        public double? FirstPreviewRenderLatencyMs { get; set; }

        public double? FirstPreviewRenderBackpressureMs { get; set; }

        public double AudioStreamingCompletedMs { get; set; }

        public double? PostProcessingStartedMs { get; set; }

        public double? PostProcessingFinishedMs { get; set; }

        public double PostProcessingDurationMs { get; set; }

        public double? FinalReplaceRenderMs { get; set; }

        public double SessionCompletedMs { get; set; }

        public double RawWer { get; set; }

        public double RawCer { get; set; }

        public double FinalWer { get; set; }

        public double FinalCer { get; set; }

        public int ExpectedRenderedTokenCount { get; set; }

        public int NotepadRenderedTokenCount { get; set; }

        public double PostProcessingRenderOverlapRate { get; set; }

        public double RenderedToExpectedTokenRatio { get; set; }

        public double AveragePreviewTokenRenderLatencyMs { get; set; }

        public double AverageFinalTokenRenderLatencyMs { get; set; }

        public bool HasSuspiciousDuplicateRun { get; set; }

        public bool ContainsSpinnerArtifact { get; set; }

        public bool ContainsContaminationArtifact { get; set; }

        public string ContaminationArtifactReason { get; set; } = string.Empty;

        public bool Completed { get; set; }

        public string ErrorMessage { get; set; } = string.Empty;
    }
}
