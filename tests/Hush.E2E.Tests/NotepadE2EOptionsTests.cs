namespace Hush.E2E.Tests;

public sealed class NotepadE2EOptionsTests
{
    [Fact]
    public void ParseReportCaseIds_AcceptsCommaSemicolonAndNewlineSeparatedValues()
    {
        var caseIds = NotepadE2EOptions.ParseReportCaseIds(" alpha, beta;ALPHA\r\ngamma\n ; ");

        Assert.Equal(new[] { "alpha", "beta", "gamma" }, caseIds);
    }

    [Fact]
    public void FilterReportCases_ReturnsAllCasesWhenNoSubsetIsConfigured()
    {
        var cases = CreateCases("alpha", "beta");

        var selectedCases = NotepadE2EOptions.FilterReportCases(cases, Array.Empty<string>());

        Assert.Same(cases, selectedCases);
    }

    [Fact]
    public void FilterReportCases_SelectsRequestedCasesCaseInsensitively()
    {
        var cases = CreateCases("alpha", "beta", "gamma");

        var selectedCases = NotepadE2EOptions.FilterReportCases(cases, new[] { "GAMMA", "alpha" });

        Assert.Equal(new[] { "gamma", "alpha" }, selectedCases.Select(testCase => testCase.Id));
    }

    [Fact]
    public void FilterReportCases_FailsWhenRequestedCaseIsMissing()
    {
        var cases = CreateCases("alpha", "beta");

        var exception = Assert.ThrowsAny<Exception>(() =>
            NotepadE2EOptions.FilterReportCases(cases, new[] { "alpha", "missing" }));

        Assert.Contains("HUSH_NOTEPAD_E2E_CASES requested unknown case(s): missing", exception.Message);
        Assert.Contains("Available cases: alpha, beta", exception.Message);
    }

    private static IReadOnlyList<SyntheticAudioCase> CreateCases(params string[] ids) =>
        ids.Select(id => new SyntheticAudioCase(
                id,
                $"{id}.wav",
                DurationSeconds: 1,
                "en",
                Array.Empty<string>(),
                $"expected transcript for {id}"))
            .ToArray();
}
