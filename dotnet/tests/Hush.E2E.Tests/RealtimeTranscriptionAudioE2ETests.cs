using System.Text;
using Hush.Core.Transcription;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hush.E2E.Tests;

public sealed class RealtimeTranscriptionAudioE2ETests
{
    private const int ChunkDurationMilliseconds = 50;

    [AudioE2EFact]
    public async Task Synthetic_audio_streams_through_real_transcription_engine()
    {
        using var timeout = new CancellationTokenSource(AudioE2EOptions.Timeout);
        var corpus = SyntheticAudioCorpus.Load();
        var selectedCases = corpus.GetCases(AudioE2EOptions.CaseIds);

        await using var engine = new TranscriptionEngine(new NullLogger<TranscriptionEngine>())
        {
            UnloadTimeout = Hush.Core.Configuration.ModelUnloadTimeout.Never,
        };

        await engine.InitializeAsync(
            AudioE2EOptions.ModelAlias,
            downloadHardwareEPs: false,
            cancellationToken: timeout.Token);

        foreach (var testCase in selectedCases)
            await RunCaseAsync(engine, corpus, testCase, timeout.Token);
    }

    private static async Task RunCaseAsync(
        ITranscriptionEngine engine,
        SyntheticAudioCorpus corpus,
        SyntheticAudioCase testCase,
        CancellationToken cancellationToken)
    {
        var wav = WavPcmFile.Read(corpus.GetAudioPath(testCase));
        Assert.Equal(16000, wav.SampleRate);
        Assert.Equal(1, wav.Channels);
        Assert.Equal(16, wav.BitsPerSample);

        await engine.StartSessionAsync(
            wav.SampleRate,
            wav.Channels,
            testCase.Language,
            streamingCommit: true,
            cancellationToken);

        var resultsTask = CollectResultsAsync(engine, cancellationToken);

        await StreamPcmAsync(engine, wav, cancellationToken);
        await engine.StopSessionAsync(cancellationToken);

        var results = await resultsTask.WaitAsync(cancellationToken);
        Assert.NotEmpty(results);
        Assert.Contains(results, result => result.IsFinal || !string.IsNullOrWhiteSpace(result.DisplayText));

        var transcript = BuildTranscript(results);
        var comparison = TranscriptQuality.Compare(testCase.ExpectedTranscript, transcript);

        Assert.False(
            TranscriptQuality.HasSuspiciousDuplicateRun(transcript),
            $"{testCase.Id} produced a suspicious duplicate run: '{transcript}'.");
        Assert.True(
            comparison.Wer <= AudioE2EOptions.MaxWer,
            $"{testCase.Id} WER {comparison.Wer:P1} exceeded {AudioE2EOptions.MaxWer:P1}. Expected '{comparison.ExpectedNormalized}', actual '{comparison.ActualNormalized}'.");
        Assert.True(
            comparison.Cer <= AudioE2EOptions.MaxCer,
            $"{testCase.Id} CER {comparison.Cer:P1} exceeded {AudioE2EOptions.MaxCer:P1}. Expected '{comparison.ExpectedNormalized}', actual '{comparison.ActualNormalized}'.");
    }

    private static async Task StreamPcmAsync(
        ITranscriptionEngine engine,
        WavPcmFile wav,
        CancellationToken cancellationToken)
    {
        var bytesPerSampleFrame = wav.Channels * (wav.BitsPerSample / 8);
        var chunkSize = wav.SampleRate * bytesPerSampleFrame * ChunkDurationMilliseconds / 1000;
        if (chunkSize <= 0)
            throw new InvalidOperationException($"Invalid WAV chunk size for {wav.FilePath}.");

        for (int offset = 0; offset < wav.PcmData.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, wav.PcmData.Length - offset);
            await engine.AppendAudioAsync(wav.PcmData.AsMemory(offset, length), cancellationToken);

            if (AudioE2EOptions.DelayScale > 0)
            {
                var delay = TimeSpan.FromMilliseconds(ChunkDurationMilliseconds * AudioE2EOptions.DelayScale);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static async Task<IReadOnlyList<TranscriptionResult>> CollectResultsAsync(
        ITranscriptionEngine engine,
        CancellationToken cancellationToken)
    {
        var results = new List<TranscriptionResult>();
        await foreach (var result in engine.GetResultStreamAsync(cancellationToken))
            results.Add(result);

        return results;
    }

    private static string BuildTranscript(IReadOnlyList<TranscriptionResult> results)
    {
        var committed = new StringBuilder();
        foreach (var result in results)
        {
            if (result.BackspaceCount > 0 && committed.Length > 0)
                committed.Length = Math.Max(0, committed.Length - result.BackspaceCount);

            if (!string.IsNullOrWhiteSpace(result.CommittedDelta))
            {
                if (committed.Length > 0 && !char.IsWhiteSpace(committed[^1]))
                    committed.Append(' ');

                committed.Append(result.CommittedDelta.Trim());
            }
        }

        if (committed.Length > 0)
            return committed.ToString();

        return results.LastOrDefault(result => !string.IsNullOrWhiteSpace(result.DisplayText))?.DisplayText ?? string.Empty;
    }
}
