using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Hush.Core.Configuration;
using Hush.Core.Output;
using Hush.Core.Session;
using Hush.Core.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Hush.E2E.Tests;

[SupportedOSPlatform("windows")]
public sealed class NotepadStandardAllAudioReportE2ETests
{
    private readonly ITestOutputHelper _output;

    public NotepadStandardAllAudioReportE2ETests(ITestOutputHelper output)
        => _output = output;

    [NotepadStandardReportE2EFact]
    public async Task All_synthetic_audio_files_generate_standard_notepad_accuracy_latency_and_artifact_report()
    {
        using var timeout = new CancellationTokenSource(NotepadE2EOptions.Timeout);
        var corpus = SyntheticAudioCorpus.Load();
        var reportCases = NotepadE2EOptions.FilterReportCases(corpus.Cases);
        var results = new List<NotepadStandardAudioReportRow>();
        var lockedDesktop = WindowsInteractiveDesktop.IsLocked();

        foreach (var testCase in reportCases.OrderBy(testCase => testCase.Id, StringComparer.Ordinal))
        {
            var result = await RunCaseAsync(corpus, testCase, lockedDesktop, timeout.Token);
            results.Add(result);
            _output.WriteLine(
                $"{result.CaseId}: asrWER={result.AsrWer:P1}, " +
                $"outputWER={result.OutputFidelityWer:P1}, " +
                $"firstCommit={FormatNullableMs(result.FirstCommitLatencyMs)}, " +
                $"firstRender={FormatNullableMs(result.FirstRenderLatencyMs)}, " +
                $"avgPreviewToken={result.AverageEstimatedPreviewTokenE2ELatencyMs:F0}ms, " +
                $"avgCommitToken={result.AverageEstimatedCommittedTokenAvailabilityLatencyMs:F0}ms, " +
                $"renderOutput={result.AverageRenderOutputDurationMs:F0}ms, " +
                $"outputOverlap={result.OutputFidelityOverlapRate:P1}");
        }

        var report = NotepadStandardAllAudioReport.Create(results);
        WriteReport(report);

        Assert.All(results, result => Assert.True(result.Completed, result.ErrorMessage));
    }

    private static async Task<NotepadStandardAudioReportRow> RunCaseAsync(
        SyntheticAudioCorpus corpus,
        SyntheticAudioCase testCase,
        bool lockedDesktop,
        CancellationToken cancellationToken)
    {
        var wav = WavPcmFile.Read(corpus.GetAudioPath(testCase));
        var stopwatch = new Stopwatch();
        var sessionErrors = new List<Exception>();
        var row = new NotepadStandardAudioReportRow
        {
            CaseId = testCase.Id,
            Tags = string.Join(";", testCase.Tags),
            AudioDurationSeconds = testCase.Duration.TotalSeconds,
            ExpectedRawTranscript = testCase.ExpectedTranscript,
            OutputMode = DictationOutputMode.Streaming.ToString(),
            StreamingCommit = true,
            ShowSpinner = false,
            PostProcessorMode = "none",
            DelayScale = NotepadE2EOptions.DelayScale,
            DesktopLocked = lockedDesktop,
            RenderBackend = lockedDesktop ? "locked-file-backed-notepad-document" : "interactive-notepad-uia-buffered-setvalue",
            VoiceOnsetMs = wav.DetectVoiceOnsetMilliseconds(),
        };

        try
        {
            await using var notepad = await NotepadTestApp.LaunchAsync(
                cancellationToken,
                requireInteractiveWindow: !lockedDesktop);
            await using var engine = new TranscriptionEngine(new NullLogger<TranscriptionEngine>())
            {
                UnloadTimeout = ModelUnloadTimeout.Never,
                LiveAudioPushQueueCapacity = NotepadE2EOptions.LiveAudioPushQueueCapacity,
            };

            using var capture = new WavFileAudioCaptureService(
                wav,
                NotepadE2EOptions.DelayScale,
                NotepadE2EOptions.AudioChunkDurationMilliseconds);
            capture.AudioStreamingStarted += stopwatch.Restart;
            ITextOutputService innerOutput = lockedDesktop
                ? new LockedNotepadDocumentOutputService(notepad)
                : new NotepadTargetedTextOutputService(
                    notepad,
                    new KeystrokeTypingService(
                        new NullLogger<KeystrokeTypingService>(),
                        useClipboardFallback: false));
            var measuredOutput = new MeasuringTextOutputService(innerOutput, stopwatch);
            var transcriptBuffer = new TranscriptBuffer();
            var commitAvailabilityEvents = new List<NotepadStandardRenderTiming>();
            await using var session = new DictationSession(
                engine,
                capture,
                measuredOutput,
                new NullLogger<DictationSession>(),
                transcriptBuffer: transcriptBuffer);

            session.OnInterimText += text =>
            {
                row.InterimEventCount++;
                row.LastInterimText = text;
                row.FirstInterimLatencyMs ??= stopwatch.Elapsed.TotalMilliseconds;
            };
            session.OnCommittedChunk += text =>
            {
                var committedAtMs = stopwatch.Elapsed.TotalMilliseconds;
                row.CommittedEventCount++;
                row.ConcatenatedCommittedDeltaTranscript += text;
                row.FirstCommitLatencyMs ??= committedAtMs;
                commitAvailabilityEvents.Add(new NotepadStandardRenderTiming(text, committedAtMs, committedAtMs));
            };
            session.OnPostProcessingStateChanged += _ => row.UnexpectedPostProcessingEventCount++;
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
            await session.StartAsync(
                testCase.Language,
                streamingCommit: true,
                showSpinner: false,
                postProcessingPrompt: null,
                outputMode: DictationOutputMode.Streaming,
                cancellationToken: cancellationToken);

            await capture.Completion.WaitAsync(cancellationToken);
            row.AudioStreamingCompletedMs = stopwatch.Elapsed.TotalMilliseconds;
            await session.StopAsync(cancellationToken);
            row.SessionCompletedMs = stopwatch.Elapsed.TotalMilliseconds;
            row.NotepadFinalText = lockedDesktop
                ? await notepad.ReadFileTextAsync(cancellationToken)
                : await notepad.WaitForStableTextAsync(TimeSpan.FromSeconds(5), cancellationToken);
            row.ReconstructedCommittedTranscript =
                transcriptBuffer.GetLatest()
                ?? NotepadStandardReportMetrics.ReconstructCommittedText(measuredOutput.CommittedTextOperations);

            row.TypeRenderEventCount = measuredOutput.TypeEvents.Count;
            row.BackspaceEventCount = measuredOutput.BackspaceEvents.Count;
            row.ReplacementEventCount = measuredOutput.ReplaceEvents.Count;
            row.PreviewRenderEventCount = measuredOutput.PreviewEvents.Count;
            var primaryRenderEvents = measuredOutput.PreviewEvents.Count > 0
                ? measuredOutput.PreviewEvents
                : measuredOutput.TypeEvents;
            row.FirstRenderLatencyMs = primaryRenderEvents.FirstOrDefault()?.CompletedAtMs;
            row.FirstInterimAfterVoiceOnsetMs = SubtractVoiceOnset(row.FirstInterimLatencyMs, row.VoiceOnsetMs);
            row.FirstRenderAfterVoiceOnsetMs = SubtractVoiceOnset(row.FirstRenderLatencyMs, row.VoiceOnsetMs);
            row.AverageRenderOutputDurationMs =
                NotepadStandardReportMetrics.CalculateAverageRenderOutputDuration(primaryRenderEvents);
            row.AverageInterCommitIntervalMs =
                NotepadStandardReportMetrics.CalculateAverageInterCommitInterval(commitAvailabilityEvents);
            row.AverageInterRenderIntervalMs =
                NotepadStandardReportMetrics.CalculateAverageInterRenderInterval(primaryRenderEvents);

            var asrComparison = TranscriptQuality.Compare(testCase.ExpectedTranscript, row.ReconstructedCommittedTranscript);
            var outputFidelityComparison = TranscriptQuality.Compare(row.ReconstructedCommittedTranscript, row.NotepadFinalText);
            row.AsrWer = asrComparison.Wer;
            row.AsrCer = asrComparison.Cer;
            row.OutputFidelityWer = outputFidelityComparison.Wer;
            row.OutputFidelityCer = outputFidelityComparison.Cer;
            row.ExpectedRawTokenCount = TranscriptQuality.CountTokens(testCase.ExpectedTranscript);
            row.ReconstructedCommittedTokenCount = TranscriptQuality.CountTokens(row.ReconstructedCommittedTranscript);
            row.NotepadRenderedTokenCount = TranscriptQuality.CountTokens(row.NotepadFinalText);
            row.OutputFidelityOverlapRate = TranscriptQuality.WordOverlapRate(row.ReconstructedCommittedTranscript, row.NotepadFinalText);
            row.OutputFidelityTokenRatio = TranscriptQuality.RenderedToExpectedTokenRatio(row.ReconstructedCommittedTranscript, row.NotepadFinalText);
            row.HasSuspiciousDuplicateRun = TranscriptQuality.HasSuspiciousDuplicateRun(row.NotepadFinalText);
            var artifactAnalysis = TranscriptQuality.AnalyzeRenderedArtifacts(row.ReconstructedCommittedTranscript, row.NotepadFinalText);
            row.ContainsSpinnerArtifact = artifactAnalysis.ContainsSpinnerArtifact;
            row.ContainsContaminationArtifact = artifactAnalysis.ContainsContaminationArtifact;
            row.ContaminationArtifactReason = artifactAnalysis.ContaminationArtifactReason;
            row.AverageEstimatedPreviewTokenE2ELatencyMs = NotepadStandardReportMetrics.CalculateAverageEstimatedAudioTokenE2ELatency(
                primaryRenderEvents,
                testCase.ExpectedTranscript,
                testCase.Duration);
            row.FirstEstimatedPreviewTokenE2ELatencyMs = NotepadStandardReportMetrics.CalculateFirstEstimatedAudioTokenE2ELatency(
                primaryRenderEvents,
                testCase.ExpectedTranscript,
                testCase.Duration);
            row.AverageEstimatedCommittedTokenAvailabilityLatencyMs = NotepadStandardReportMetrics.CalculateAverageEstimatedAudioTokenE2ELatency(
                commitAvailabilityEvents,
                testCase.ExpectedTranscript,
                testCase.Duration);
            row.Completed = sessionErrors.Count == 0 && row.UnexpectedPostProcessingEventCount == 0;
            row.ErrorMessage = string.Join(" | ", sessionErrors.Select(error => error.Message));
            if (row.UnexpectedPostProcessingEventCount > 0)
            {
                row.ErrorMessage = string.Join(
                    " | ",
                    new[] { row.ErrorMessage, $"Unexpected post-processing events: {row.UnexpectedPostProcessingEventCount}" }
                        .Where(message => !string.IsNullOrWhiteSpace(message)));
            }
        }
        catch (Exception ex)
        {
            row.Completed = false;
            row.ErrorMessage = ex.ToString();
        }

        return row;
    }

    private void WriteReport(NotepadStandardAllAudioReport report)
    {
        var resultsDir = Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(resultsDir);

        var jsonPath = Path.Combine(resultsDir, "notepad-standard-all-audio-report.json");
        var csvPath = Path.Combine(resultsDir, "notepad-standard-all-audio-report.csv");
        var markdownPath = Path.Combine(resultsDir, "notepad-standard-all-audio-report.md");

        File.WriteAllText(
            jsonPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(csvPath, BuildCsv(report.Rows));
        File.WriteAllText(markdownPath, BuildMarkdown(report));

        _output.WriteLine($"JSON report: {jsonPath}");
        _output.WriteLine($"CSV report: {csvPath}");
        _output.WriteLine($"Markdown report: {markdownPath}");
    }

    private static double? SubtractVoiceOnset(double? latencyMs, double voiceOnsetMs) =>
        latencyMs.HasValue ? Math.Max(0, latencyMs.Value - voiceOnsetMs) : null;

    private static string BuildCsv(IReadOnlyList<NotepadStandardAudioReportRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "case_id,tags,audio_seconds,voice_onset_ms,asr_wer,asr_cer,output_fidelity_wer,output_fidelity_cer,estimated_preview_token_e2e_latency_ms,first_estimated_preview_token_e2e_latency_ms,estimated_committed_token_availability_latency_ms,first_interim_latency_ms,first_interim_after_voice_onset_ms,first_commit_latency_ms,first_render_latency_ms,first_render_after_voice_onset_ms,avg_render_output_duration_ms,avg_inter_commit_interval_ms,avg_inter_render_interval_ms,audio_streaming_completed_ms,session_completed_ms,output_fidelity_overlap_rate,output_fidelity_token_ratio,expected_raw_tokens,reconstructed_committed_tokens,rendered_tokens,interim_events,committed_events,type_render_events,backspace_events,replacement_events,preview_render_events,unexpected_postprocessing_events,desktop_locked,render_backend,output_mode,streaming_commit,show_spinner,post_processor_mode,delay_scale,duplicate_artifact,spinner_artifact,contamination_artifact,contamination_reason,completed,error");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(
                ',',
                Csv(row.CaseId),
                Csv(row.Tags),
                Number(row.AudioDurationSeconds),
                Number(row.VoiceOnsetMs),
                Number(row.AsrWer),
                Number(row.AsrCer),
                Number(row.OutputFidelityWer),
                Number(row.OutputFidelityCer),
                Number(row.AverageEstimatedPreviewTokenE2ELatencyMs),
                Number(row.FirstEstimatedPreviewTokenE2ELatencyMs),
                Number(row.AverageEstimatedCommittedTokenAvailabilityLatencyMs),
                NullableNumber(row.FirstInterimLatencyMs),
                NullableNumber(row.FirstInterimAfterVoiceOnsetMs),
                NullableNumber(row.FirstCommitLatencyMs),
                NullableNumber(row.FirstRenderLatencyMs),
                NullableNumber(row.FirstRenderAfterVoiceOnsetMs),
                Number(row.AverageRenderOutputDurationMs),
                Number(row.AverageInterCommitIntervalMs),
                Number(row.AverageInterRenderIntervalMs),
                Number(row.AudioStreamingCompletedMs),
                Number(row.SessionCompletedMs),
                Number(row.OutputFidelityOverlapRate),
                Number(row.OutputFidelityTokenRatio),
                row.ExpectedRawTokenCount.ToString(CultureInfo.InvariantCulture),
                row.ReconstructedCommittedTokenCount.ToString(CultureInfo.InvariantCulture),
                row.NotepadRenderedTokenCount.ToString(CultureInfo.InvariantCulture),
                row.InterimEventCount.ToString(CultureInfo.InvariantCulture),
                row.CommittedEventCount.ToString(CultureInfo.InvariantCulture),
                row.TypeRenderEventCount.ToString(CultureInfo.InvariantCulture),
                row.BackspaceEventCount.ToString(CultureInfo.InvariantCulture),
                row.ReplacementEventCount.ToString(CultureInfo.InvariantCulture),
                row.PreviewRenderEventCount.ToString(CultureInfo.InvariantCulture),
                row.UnexpectedPostProcessingEventCount.ToString(CultureInfo.InvariantCulture),
                row.DesktopLocked.ToString(CultureInfo.InvariantCulture),
                Csv(row.RenderBackend),
                Csv(row.OutputMode),
                row.StreamingCommit.ToString(CultureInfo.InvariantCulture),
                row.ShowSpinner.ToString(CultureInfo.InvariantCulture),
                Csv(row.PostProcessorMode),
                Number(row.DelayScale),
                row.HasSuspiciousDuplicateRun.ToString(CultureInfo.InvariantCulture),
                row.ContainsSpinnerArtifact.ToString(CultureInfo.InvariantCulture),
                row.ContainsContaminationArtifact.ToString(CultureInfo.InvariantCulture),
                Csv(row.ContaminationArtifactReason),
                row.Completed.ToString(CultureInfo.InvariantCulture),
                Csv(row.ErrorMessage)));
        }

        return sb.ToString();
    }

    private static string BuildMarkdown(NotepadStandardAllAudioReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Notepad standard-mode all-audio E2E report");
        sb.AppendLine();
        sb.AppendLine("- Mode: `DictationOutputMode.Streaming` with `streamingCommit=true`, `showSpinner=false`, and no post-processor.");
        sb.AppendLine($"- Cases: {report.TotalCases}");
        sb.AppendLine($"- Completed: {report.CompletedCases}");
        sb.AppendLine($"- Average ASR WER vs expected transcript: {report.AverageAsrWer:P2}");
        sb.AppendLine($"- Average ASR CER vs expected transcript: {report.AverageAsrCer:P2}");
        sb.AppendLine($"- Average output fidelity WER vs reconstructed committed transcript: {report.AverageOutputFidelityWer:P2}");
        sb.AppendLine($"- Average output fidelity CER vs reconstructed committed transcript: {report.AverageOutputFidelityCer:P2}");
        sb.AppendLine($"- Average ASR first-interim latency: {report.AverageFirstInterimLatencyMs:F0} ms");
        sb.AppendLine($"- Average ASR first-interim latency after voice onset: {report.AverageFirstInterimAfterVoiceOnsetMs:F0} ms");
        sb.AppendLine($"- Average first commit availability latency: {report.AverageFirstCommitLatencyMs:F0} ms");
        sb.AppendLine($"- Average first render completion latency: {report.AverageFirstRenderLatencyMs:F0} ms");
        sb.AppendLine($"- Average first render completion latency after voice onset: {report.AverageFirstRenderAfterVoiceOnsetMs:F0} ms");
        sb.AppendLine($"- Average render output duration (commit availability to render completion): {report.AverageRenderOutputDurationMs:F0} ms");
        sb.AppendLine($"- Average inter-commit interval: {report.AverageInterCommitIntervalMs:F0} ms");
        sb.AppendLine($"- Average inter-render interval: {report.AverageInterRenderIntervalMs:F0} ms");
        sb.AppendLine($"- Estimated preview audio-token end-to-end latency: {report.AverageEstimatedPreviewTokenE2ELatencyMs:F0} ms");
        sb.AppendLine($"- First estimated preview token end-to-end latency: {report.AverageFirstEstimatedPreviewTokenE2ELatencyMs:F0} ms");
        sb.AppendLine($"- Estimated committed-token availability latency: {report.AverageEstimatedCommittedTokenAvailabilityLatencyMs:F0} ms");
        sb.AppendLine($"- Average output fidelity overlap: {report.AverageOutputFidelityOverlapRate:P2}");
        sb.AppendLine($"- Average output fidelity token ratio: {report.AverageOutputFidelityTokenRatio:P2}");
        sb.AppendLine($"- Duplicate artifact cases: {report.DuplicateArtifactCases}");
        sb.AppendLine($"- Spinner artifact cases: {report.SpinnerArtifactCases}");
        sb.AppendLine($"- Contamination artifact cases: {report.ContaminationArtifactCases}");
        sb.AppendLine($"- Unexpected post-processing event cases: {report.UnexpectedPostProcessingEventCases}");
        sb.AppendLine($"- Render backend: {string.Join(", ", report.Rows.Select(row => row.RenderBackend).Distinct(StringComparer.Ordinal))}");
        sb.AppendLine();
        sb.AppendLine("| Case | ASR WER | Output WER | Voice onset | ASR first interim | First render after voice onset | First commit | First render | Render output | Inter-commit | Inter-render | Est. preview token E2E | Est. commit token availability | Output overlap | Output token ratio | Artifacts | Backend | Completed |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- | --- |");
        foreach (var row in report.Rows)
        {
            var artifacts = BuildArtifactSummary(row);
            sb.AppendLine(
                $"| {row.CaseId} | {row.AsrWer:P1} | {row.OutputFidelityWer:P1} | {row.VoiceOnsetMs:F0} ms | " +
                $"{FormatNullableMs(row.FirstInterimLatencyMs)} | {FormatNullableMs(row.FirstRenderAfterVoiceOnsetMs)} | {FormatNullableMs(row.FirstCommitLatencyMs)} | " +
                $"{FormatNullableMs(row.FirstRenderLatencyMs)} | {row.AverageRenderOutputDurationMs:F0} ms | " +
                $"{row.AverageInterCommitIntervalMs:F0} ms | {row.AverageInterRenderIntervalMs:F0} ms | " +
                $"{row.AverageEstimatedPreviewTokenE2ELatencyMs:F0} ms | {row.AverageEstimatedCommittedTokenAvailabilityLatencyMs:F0} ms | {row.OutputFidelityOverlapRate:P1} | " +
                $"{row.OutputFidelityTokenRatio:P1} | {artifacts} | {row.RenderBackend} | {row.Completed} |");
        }

        return sb.ToString();
    }

    private static string BuildArtifactSummary(NotepadStandardAudioReportRow row)
    {
        var artifacts = new List<string>();
        if (row.HasSuspiciousDuplicateRun)
            artifacts.Add("duplicate");
        if (row.ContainsSpinnerArtifact)
            artifacts.Add("spinner");
        if (row.ContainsContaminationArtifact)
            artifacts.Add($"contamination:{row.ContaminationArtifactReason}");
        if (row.UnexpectedPostProcessingEventCount > 0)
            artifacts.Add("post-processing-event");

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

        public List<NotepadStandardRenderTiming> TypeEvents { get; } = new();

        public List<NotepadStandardRenderTiming> BackspaceEvents { get; } = new();

        public List<NotepadStandardRenderTiming> ReplaceEvents { get; } = new();

        public List<NotepadStandardRenderTiming> PreviewEvents { get; } = new();

        public List<NotepadStandardCommittedTextOperation> CommittedTextOperations { get; } = new();

        public bool PreferFullBufferFinalReplacement =>
            _inner is IFullBufferFinalReplacementOutputService { PreferFullBufferFinalReplacement: true };

        public async Task TypeTextAsync(string text, CancellationToken cancellationToken = default, bool skipModifierRestore = false)
        {
            var startedAtMs = _stopwatch.Elapsed.TotalMilliseconds;
            await _inner.TypeTextAsync(text, cancellationToken, skipModifierRestore);
            var completedAtMs = _stopwatch.Elapsed.TotalMilliseconds;
            CommittedTextOperations.Add(new NotepadStandardCommittedTextOperation(
                NotepadStandardCommittedTextOperationKind.TypeText,
                text));
            TypeEvents.Add(new NotepadStandardRenderTiming(text, startedAtMs, completedAtMs));
        }

        public async Task TypePreviewTextAsync(string text, CancellationToken cancellationToken = default, bool skipModifierRestore = false)
        {
            var startedAtMs = _stopwatch.Elapsed.TotalMilliseconds;
            if (_inner is IPreviewTextOutputService previewOutput)
                await previewOutput.TypePreviewTextAsync(text, cancellationToken, skipModifierRestore);
            else
                await _inner.TypeTextAsync(text, cancellationToken, skipModifierRestore);
            var completedAtMs = _stopwatch.Elapsed.TotalMilliseconds;

            PreviewEvents.Add(new NotepadStandardRenderTiming(text, startedAtMs, completedAtMs));
        }

        public async Task ReplacePreviewTextAsync(
            string currentText,
            string targetText,
            CancellationToken cancellationToken = default,
            bool skipModifierRestore = false)
        {
            var startedAtMs = _stopwatch.Elapsed.TotalMilliseconds;
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

            var replacementDelta = PreviewRenderDelta(currentText, targetText);
            PreviewEvents.Add(new NotepadStandardRenderTiming(
                replacementDelta,
                startedAtMs,
                _stopwatch.Elapsed.TotalMilliseconds));
        }

        public async Task SendBackspacesAsync(int count, CancellationToken cancellationToken = default, bool skipModifierRestore = false)
        {
            var startedAtMs = _stopwatch.Elapsed.TotalMilliseconds;
            await _inner.SendBackspacesAsync(count, cancellationToken, skipModifierRestore);
            if (count > 0)
            {
                CommittedTextOperations.Add(new NotepadStandardCommittedTextOperation(
                    NotepadStandardCommittedTextOperationKind.Backspace,
                    string.Empty,
                    count));
                BackspaceEvents.Add(new NotepadStandardRenderTiming(new string('\b', count), startedAtMs, _stopwatch.Elapsed.TotalMilliseconds));
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
            var startedAtMs = _stopwatch.Elapsed.TotalMilliseconds;
            await _inner.ReplaceTextAsync(
                backspaceCount,
                replacementText,
                cancellationToken,
                skipModifierRestore,
                boundToCurrentLine,
                expectedExistingText,
                allowFullBufferReplacement,
                replacementKind);
            CommittedTextOperations.Add(new NotepadStandardCommittedTextOperation(
                NotepadStandardCommittedTextOperationKind.ReplaceText,
                replacementText,
                backspaceCount));
            ReplaceEvents.Add(new NotepadStandardRenderTiming(replacementText, startedAtMs, _stopwatch.Elapsed.TotalMilliseconds));
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

    private sealed class LockedNotepadDocumentOutputService : IPreviewTextReplacementOutputService
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

        public async Task ReplacePreviewTextAsync(
            string currentText,
            string targetText,
            CancellationToken cancellationToken = default,
            bool skipModifierRestore = false)
        {
            _text = targetText;
            await _notepad.WriteFileTextAsync(_text, cancellationToken);
        }

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
            _text = _text[..Math.Max(0, _text.Length - Math.Max(0, backspaceCount))] + replacementText;
            await _notepad.WriteFileTextAsync(_text, cancellationToken);
        }

        public Task SendKeyAsync(AutoSubmitKey key, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NotepadStandardAllAudioReport
    {
        public required IReadOnlyList<NotepadStandardAudioReportRow> Rows { get; init; }

        public int TotalCases { get; init; }

        public int CompletedCases { get; init; }

        public double AverageAsrWer { get; init; }

        public double AverageAsrCer { get; init; }

        public double AverageOutputFidelityWer { get; init; }

        public double AverageOutputFidelityCer { get; init; }

        public double AverageEstimatedPreviewTokenE2ELatencyMs { get; init; }

        public double AverageFirstEstimatedPreviewTokenE2ELatencyMs { get; init; }

        public double AverageEstimatedCommittedTokenAvailabilityLatencyMs { get; init; }

        public double AverageFirstInterimLatencyMs { get; init; }

        public double AverageFirstInterimAfterVoiceOnsetMs { get; init; }

        public double AverageFirstCommitLatencyMs { get; init; }

        public double AverageFirstRenderLatencyMs { get; init; }

        public double AverageFirstRenderAfterVoiceOnsetMs { get; init; }

        public double AverageRenderOutputDurationMs { get; init; }

        public double AverageInterCommitIntervalMs { get; init; }

        public double AverageInterRenderIntervalMs { get; init; }

        public double AverageOutputFidelityOverlapRate { get; init; }

        public double AverageOutputFidelityTokenRatio { get; init; }

        public int DuplicateArtifactCases { get; init; }

        public int SpinnerArtifactCases { get; init; }

        public int ContaminationArtifactCases { get; init; }

        public int UnexpectedPostProcessingEventCases { get; init; }

        public static NotepadStandardAllAudioReport Create(IReadOnlyList<NotepadStandardAudioReportRow> rows)
        {
            var completed = rows.Where(row => row.Completed).ToArray();
            return new NotepadStandardAllAudioReport
            {
                Rows = rows,
                TotalCases = rows.Count,
                CompletedCases = completed.Length,
                AverageAsrWer = Average(completed, row => row.AsrWer),
                AverageAsrCer = Average(completed, row => row.AsrCer),
                AverageOutputFidelityWer = Average(completed, row => row.OutputFidelityWer),
                AverageOutputFidelityCer = Average(completed, row => row.OutputFidelityCer),
                AverageEstimatedPreviewTokenE2ELatencyMs = Average(completed, row => row.AverageEstimatedPreviewTokenE2ELatencyMs),
                AverageFirstEstimatedPreviewTokenE2ELatencyMs = Average(completed, row => row.FirstEstimatedPreviewTokenE2ELatencyMs),
                AverageEstimatedCommittedTokenAvailabilityLatencyMs = Average(completed, row => row.AverageEstimatedCommittedTokenAvailabilityLatencyMs),
                AverageFirstInterimLatencyMs = AverageNullable(completed, row => row.FirstInterimLatencyMs),
                AverageFirstInterimAfterVoiceOnsetMs = AverageNullable(completed, row => row.FirstInterimAfterVoiceOnsetMs),
                AverageFirstCommitLatencyMs = AverageNullable(completed, row => row.FirstCommitLatencyMs),
                AverageFirstRenderLatencyMs = AverageNullable(completed, row => row.FirstRenderLatencyMs),
                AverageFirstRenderAfterVoiceOnsetMs = AverageNullable(completed, row => row.FirstRenderAfterVoiceOnsetMs),
                AverageRenderOutputDurationMs = Average(completed, row => row.AverageRenderOutputDurationMs),
                AverageInterCommitIntervalMs = Average(completed, row => row.AverageInterCommitIntervalMs),
                AverageInterRenderIntervalMs = Average(completed, row => row.AverageInterRenderIntervalMs),
                AverageOutputFidelityOverlapRate = Average(completed, row => row.OutputFidelityOverlapRate),
                AverageOutputFidelityTokenRatio = Average(completed, row => row.OutputFidelityTokenRatio),
                DuplicateArtifactCases = rows.Count(row => row.HasSuspiciousDuplicateRun),
                SpinnerArtifactCases = rows.Count(row => row.ContainsSpinnerArtifact),
                ContaminationArtifactCases = rows.Count(row => row.ContainsContaminationArtifact),
                UnexpectedPostProcessingEventCases = rows.Count(row => row.UnexpectedPostProcessingEventCount > 0),
            };
        }

        private static double Average(IReadOnlyList<NotepadStandardAudioReportRow> rows, Func<NotepadStandardAudioReportRow, double> selector) =>
            rows.Count == 0 ? 0 : rows.Average(selector);

        private static double AverageNullable(IReadOnlyList<NotepadStandardAudioReportRow> rows, Func<NotepadStandardAudioReportRow, double?> selector)
        {
            var values = rows
                .Select(selector)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToArray();
            return values.Length == 0 ? 0 : values.Average();
        }
    }

    private sealed class NotepadStandardAudioReportRow
    {
        public string CaseId { get; set; } = string.Empty;

        public string Tags { get; set; } = string.Empty;

        public double AudioDurationSeconds { get; set; }

        public double VoiceOnsetMs { get; set; }

        public string ExpectedRawTranscript { get; set; } = string.Empty;

        public string ReconstructedCommittedTranscript { get; set; } = string.Empty;

        public string ConcatenatedCommittedDeltaTranscript { get; set; } = string.Empty;

        public string NotepadFinalText { get; set; } = string.Empty;

        public string OutputMode { get; set; } = string.Empty;

        public bool StreamingCommit { get; set; }

        public bool ShowSpinner { get; set; }

        public string PostProcessorMode { get; set; } = string.Empty;

        public double DelayScale { get; set; }

        public bool DesktopLocked { get; set; }

        public string RenderBackend { get; set; } = string.Empty;

        public int InterimEventCount { get; set; }

        public int CommittedEventCount { get; set; }

        public int TypeRenderEventCount { get; set; }

        public int BackspaceEventCount { get; set; }

        public int ReplacementEventCount { get; set; }

        public int PreviewRenderEventCount { get; set; }

        public int UnexpectedPostProcessingEventCount { get; set; }

        public string LastInterimText { get; set; } = string.Empty;

        public double? FirstInterimLatencyMs { get; set; }

        public double? FirstInterimAfterVoiceOnsetMs { get; set; }

        public double? FirstCommitLatencyMs { get; set; }

        public double? FirstRenderLatencyMs { get; set; }

        public double? FirstRenderAfterVoiceOnsetMs { get; set; }

        public double AudioStreamingCompletedMs { get; set; }

        public double SessionCompletedMs { get; set; }

        public double AsrWer { get; set; }

        public double AsrCer { get; set; }

        public double OutputFidelityWer { get; set; }

        public double OutputFidelityCer { get; set; }

        public int ExpectedRawTokenCount { get; set; }

        public int ReconstructedCommittedTokenCount { get; set; }

        public int NotepadRenderedTokenCount { get; set; }

        public double OutputFidelityOverlapRate { get; set; }

        public double OutputFidelityTokenRatio { get; set; }

        public double AverageEstimatedPreviewTokenE2ELatencyMs { get; set; }

        public double FirstEstimatedPreviewTokenE2ELatencyMs { get; set; }

        public double AverageEstimatedCommittedTokenAvailabilityLatencyMs { get; set; }

        public double AverageRenderOutputDurationMs { get; set; }

        public double AverageInterCommitIntervalMs { get; set; }

        public double AverageInterRenderIntervalMs { get; set; }

        public bool HasSuspiciousDuplicateRun { get; set; }

        public bool ContainsSpinnerArtifact { get; set; }

        public bool ContainsContaminationArtifact { get; set; }

        public string ContaminationArtifactReason { get; set; } = string.Empty;

        public bool Completed { get; set; }

        public string ErrorMessage { get; set; } = string.Empty;
    }
}
