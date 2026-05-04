namespace Hush.E2E.Tests;

public sealed class TranscriptQualityArtifactTests
{
    private const string SharePointUrl =
        "https://microsoft.sharepoint.com/:p:/t/FoundryPlanningandRelease/cQpzpGGnk_KLT7b57TlZN1YeEgUCipEilQcqihVK80bTswkGHg";

    [Fact]
    public void ContainsCleanModeSpinnerArtifact_DoesNotFlagUrlSlashes()
    {
        Assert.False(TranscriptQuality.ContainsCleanModeSpinnerArtifact(SharePointUrl));
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
            SharePointUrl);

        Assert.False(analysis.ContainsSpinnerArtifact);
        Assert.True(analysis.ContainsContaminationArtifact);
        Assert.Equal("foreign-url", analysis.ContaminationArtifactReason);
    }

    [Fact]
    public void AnalyzeRenderedArtifacts_ClassifiesInsertedForeignUrlAsContamination()
    {
        var analysis = TranscriptQuality.AnalyzeRenderedArtifacts(
            "please speak more quietly because the microphone is very sensitive",
            "please speak more quietly " + SharePointUrl + " because the microphone is very sensitive");

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
