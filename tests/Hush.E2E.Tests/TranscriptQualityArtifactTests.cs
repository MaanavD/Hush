namespace Hush.E2E.Tests;

public sealed class TranscriptQualityArtifactTests
{
    private const string ForeignDocumentUrl =
        "https://contoso.example/share/dictation-fixture/document-123";

    [Fact]
    public void ContainsCleanModeSpinnerArtifact_DoesNotFlagUrlSlashes()
    {
        Assert.False(TranscriptQuality.ContainsCleanModeSpinnerArtifact(ForeignDocumentUrl));
    }

    [Theory]
    [InlineData("expected transcript|")]
    [InlineData("expected transcript/")]
    [InlineData("expected transcript\\")]
    [InlineData("expected transcript\u2014")]
    [InlineData("expected transcript\r\n|")]
    public void ContainsCleanModeSpinnerArtifact_FlagsLikelyLeakedSpinnerFrames(string text)
    {
        Assert.True(TranscriptQuality.ContainsCleanModeSpinnerArtifact(text));
    }

    [Fact]
    public void AnalyzeRenderedArtifacts_ClassifiesForeignUrlAsContaminationNotSpinner()
    {
        var analysis = TranscriptQuality.AnalyzeRenderedArtifacts(
            "what time is the design review today",
            ForeignDocumentUrl);

        Assert.False(analysis.ContainsSpinnerArtifact);
        Assert.True(analysis.ContainsContaminationArtifact);
        Assert.Equal("foreign-url", analysis.ContaminationArtifactReason);
    }

    [Fact]
    public void AnalyzeRenderedArtifacts_ClassifiesInsertedForeignUrlAsContamination()
    {
        var analysis = TranscriptQuality.AnalyzeRenderedArtifacts(
            "please speak more quietly because the microphone is very sensitive",
            "please speak more quietly " + ForeignDocumentUrl + " because the microphone is very sensitive");

        Assert.False(analysis.ContainsSpinnerArtifact);
        Assert.True(analysis.ContainsContaminationArtifact);
        Assert.Equal("foreign-url", analysis.ContaminationArtifactReason);
    }

    [Fact]
    public void AnalyzeRenderedArtifacts_ClassifiesVeryLowOverlapAsContamination()
    {
        var analysis = TranscriptQuality.AnalyzeRenderedArtifacts(
            "please move the design review to thursday afternoon",
            "completely unrelated clipboard payload from another application");

        Assert.True(analysis.ContainsContaminationArtifact);
        Assert.Equal("very-low-overlap", analysis.ContaminationArtifactReason);
    }
}
