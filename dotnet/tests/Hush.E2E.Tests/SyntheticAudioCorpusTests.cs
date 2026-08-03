namespace Hush.E2E.Tests;

public sealed class SyntheticAudioCorpusTests
{
    private static readonly TimeSpan MinimumRealtimeDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumRealtimeDuration = TimeSpan.FromMinutes(2);

    [Fact]
    public void Manifest_loads_expected_synthetic_cases()
    {
        var corpus = SyntheticAudioCorpus.Load();

        Assert.Equal(30, corpus.Cases.Count);
        Assert.All(corpus.Cases, testCase =>
        {
            Assert.False(string.IsNullOrWhiteSpace(testCase.Id));
            Assert.False(string.IsNullOrWhiteSpace(testCase.Path));
            Assert.False(string.IsNullOrWhiteSpace(testCase.ExpectedTranscript));
            Assert.Equal("en", testCase.Language);
            Assert.NotEmpty(testCase.Tags);
            Assert.True(File.Exists(corpus.GetAudioPath(testCase)), $"Audio file missing for {testCase.Id}.");
        });
        Assert.Equal(corpus.Cases.Count, corpus.Cases.Select(testCase => testCase.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Audio_files_are_16khz_mono_16bit_pcm_wav()
    {
        var corpus = SyntheticAudioCorpus.Load();

        Assert.All(corpus.Cases, testCase =>
        {
            var wav = WavPcmFile.Read(corpus.GetAudioPath(testCase));

            Assert.Equal(1, wav.AudioFormat);
            Assert.Equal(1, wav.Channels);
            Assert.Equal(16000, wav.SampleRate);
            Assert.Equal(16, wav.BitsPerSample);
            Assert.NotEmpty(wav.PcmData);
        });
    }

    [Fact]
    public void Default_realtime_regression_cases_are_independent_and_cover_recent_risks()
    {
        var corpus = SyntheticAudioCorpus.Load();
        var selectedCases = corpus.GetCases(AudioE2EOptions.CaseIds);
        var selectedTags = selectedCases.SelectMany(testCase => testCase.Tags).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.All(selectedCases, testCase =>
        {
            Assert.InRange(testCase.Duration, MinimumRealtimeDuration, MaximumRealtimeDuration);
            Assert.DoesNotContain("short_command", testCase.Tags);
            Assert.DoesNotContain("short_question", testCase.Tags);
        });

        Assert.Contains("filler", selectedTags);
        Assert.Contains("self_correction", selectedTags);
        Assert.Contains("pause", selectedTags);
        Assert.Contains("endpointing", selectedTags);
        Assert.Contains("longer_stream", selectedTags);
        Assert.Contains("partial_revisions", selectedTags);
        Assert.Contains("fast_speech", selectedTags);
        Assert.Contains("noise", selectedTags);
        Assert.Contains("stress", selectedTags);
    }

    [Fact]
    public void Sub_five_second_clips_are_excluded_from_default_realtime_selection()
    {
        var corpus = SyntheticAudioCorpus.Load();
        var selectedIds = AudioE2EOptions.CaseIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tooShortCases = corpus.Cases
            .Where(testCase => testCase.Duration < MinimumRealtimeDuration)
            .Select(testCase => testCase.Id)
            .ToArray();

        Assert.Equal(new[] { "en_rt_001_clean_command", "en_rt_002_clean_question" }, tooShortCases);
        Assert.DoesNotContain(tooShortCases, id => selectedIds.Contains(id));
    }
}
