using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Hush.Core.Configuration;
using Hush.Core.Output;
using Hush.Core.Session;
using Hush.Core.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Hush.E2E.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacOSTextEditE2ETests
{
    private const string InjectionSentinel = "hush textedit e2e ready";

    private readonly ITestOutputHelper _output;

    public MacOSTextEditE2ETests(ITestOutputHelper output)
        => _output = output;

    [MacOSE2EFact]
    public async Task Synthetic_audio_streams_into_real_textedit_on_macos()
    {
        using var timeout = new CancellationTokenSource(MacOSE2EOptions.Timeout);
        var corpus = SyntheticAudioCorpus.Load();
        var testCase = corpus.GetCase(MacOSE2EOptions.CaseId);
        var wav = WavPcmFile.Read(corpus.GetAudioPath(testCase));
        var stopwatch = new Stopwatch();
        var sessionErrors = new List<Exception>();
        var metrics = new MacOSTextEditE2EResult
        {
            CaseId = testCase.Id,
            AudioDurationSeconds = testCase.Duration.TotalSeconds,
            ExpectedTranscript = testCase.ExpectedTranscript,
            DelayScale = MacOSE2EOptions.DelayScale,
        };

        await using var textEdit = await TextEditTestApp.LaunchAsync(timeout.Token);
        await using var engine = new TranscriptionEngine(new NullLogger<TranscriptionEngine>())
        {
            UnloadTimeout = ModelUnloadTimeout.Never,
        };

        using var capture = new WavFileAudioCaptureService(wav, MacOSE2EOptions.DelayScale);
        var outputService = new TextEditTargetedTextOutputService(textEdit);

        await VerifyTextEditOutputAsync(textEdit, outputService, timeout.Token);

        await engine.InitializeAsync(
            AudioE2EOptions.ModelAlias,
            downloadHardwareEPs: false,
            cancellationToken: timeout.Token);

        await using var session = new DictationSession(
            engine,
            capture,
            outputService,
            new NullLogger<DictationSession>());

        session.OnInterimText += text =>
        {
            metrics.InterimEventCount++;
            metrics.LastInterimText = text;
            metrics.FirstInterimLatencyMs ??= stopwatch.Elapsed.TotalMilliseconds;
        };
        session.OnCommittedChunk += text =>
        {
            metrics.CommittedEventCount++;
            metrics.ConcatenatedCommittedDeltaTranscript += text;
            metrics.FirstCommitLatencyMs ??= stopwatch.Elapsed.TotalMilliseconds;
        };
        session.OnSessionError += sessionErrors.Add;

        await textEdit.FocusAsync(timeout.Token);
        await Task.Delay(250, timeout.Token);
        stopwatch.Restart();
        await session.StartAsync(
            testCase.Language,
            streamingCommit: true,
            showSpinner: false,
            outputMode: DictationOutputMode.Streaming,
            cancellationToken: timeout.Token);

        await capture.Completion.WaitAsync(timeout.Token);
        metrics.AudioStreamingCompletedMs = stopwatch.Elapsed.TotalMilliseconds;
        await session.StopAsync(timeout.Token);
        metrics.SessionCompletedMs = stopwatch.Elapsed.TotalMilliseconds;
        metrics.TextEditFinalText = await textEdit.WaitForStableTextAsync(TimeSpan.FromSeconds(5), timeout.Token);

        var comparison = TranscriptQuality.Compare(testCase.ExpectedTranscript, metrics.TextEditFinalText);
        metrics.Wer = comparison.Wer;
        metrics.Cer = comparison.Cer;
        metrics.HasSuspiciousDuplicateRun = TranscriptQuality.HasSuspiciousDuplicateRun(metrics.TextEditFinalText);

        WriteMetrics(metrics);

        Assert.Empty(sessionErrors);
        Assert.NotNull(metrics.FirstInterimLatencyMs);
        Assert.NotNull(metrics.FirstCommitLatencyMs);
        Assert.InRange(
            TimeSpan.FromMilliseconds(metrics.FirstInterimLatencyMs.Value),
            TimeSpan.Zero,
            MacOSE2EOptions.MaxFirstInterimLatency);
        Assert.InRange(
            TimeSpan.FromMilliseconds(metrics.FirstCommitLatencyMs.Value),
            TimeSpan.Zero,
            MacOSE2EOptions.MaxFirstCommitLatency);
        Assert.NotEmpty(metrics.TextEditFinalText);
        Assert.InRange(comparison.Wer, 0, MacOSE2EOptions.MaxWer);
        Assert.InRange(comparison.Cer, 0, MacOSE2EOptions.MaxCer);
        Assert.False(metrics.HasSuspiciousDuplicateRun, $"Final TextEdit text has duplicate/overlap artifacts: {metrics.TextEditFinalText}");
    }

    private static async Task VerifyTextEditOutputAsync(
        TextEditTestApp textEdit,
        ITextOutputService outputService,
        CancellationToken cancellationToken)
    {
        await textEdit.FocusAsync(cancellationToken);
        await outputService.TypeTextAsync(InjectionSentinel, cancellationToken);
        var typed = await textEdit.WaitForStableTextAsync(TimeSpan.FromSeconds(3), cancellationToken);
        Assert.Contains(InjectionSentinel, typed, StringComparison.OrdinalIgnoreCase);
        await textEdit.SetTextAsync(string.Empty, cancellationToken);
    }

    private void WriteMetrics(MacOSTextEditE2EResult metrics)
    {
        var json = JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true });
        var resultsDir = Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(resultsDir);
        var resultPath = Path.Combine(resultsDir, "macos-textedit-e2e-result.json");
        File.WriteAllText(resultPath, json);
        _output.WriteLine(json);
        _output.WriteLine($"Result file: {resultPath}");
    }

    private sealed class MacOSTextEditE2EResult
    {
        public string CaseId { get; set; } = string.Empty;

        public double AudioDurationSeconds { get; set; }

        public string ExpectedTranscript { get; set; } = string.Empty;

        public double DelayScale { get; set; }

        public int InterimEventCount { get; set; }

        public string LastInterimText { get; set; } = string.Empty;

        public int CommittedEventCount { get; set; }

        public string ConcatenatedCommittedDeltaTranscript { get; set; } = string.Empty;

        public double? FirstInterimLatencyMs { get; set; }

        public double? FirstCommitLatencyMs { get; set; }

        public double AudioStreamingCompletedMs { get; set; }

        public double SessionCompletedMs { get; set; }

        public string TextEditFinalText { get; set; } = string.Empty;

        public double Wer { get; set; }

        public double Cer { get; set; }

        public bool HasSuspiciousDuplicateRun { get; set; }
    }
}
