using System.Text.Json;

namespace Hush.E2E.Tests;

internal sealed class SyntheticAudioCorpus
{
    public const string CorpusName = "rt_transcription_en_synthetic_v1";

    private SyntheticAudioCorpus(string rootPath, IReadOnlyList<SyntheticAudioCase> cases)
    {
        RootPath = rootPath;
        Cases = cases;
    }

    public string RootPath { get; }

    public IReadOnlyList<SyntheticAudioCase> Cases { get; }

    public static SyntheticAudioCorpus Load()
    {
        var rootPath = ResolveRootPath();
        var manifestPath = System.IO.Path.Combine(rootPath, "manifest.jsonl");
        Assert.True(File.Exists(manifestPath), $"Manifest not found: {manifestPath}");

        var cases = File.ReadLines(manifestPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<SyntheticAudioCase>(line)
                ?? throw new JsonException("Manifest row deserialized to null."))
            .ToArray();

        return new SyntheticAudioCorpus(rootPath, cases);
    }

    public string GetAudioPath(SyntheticAudioCase testCase)
    {
        var pathParts = testCase.Path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        return System.IO.Path.Combine(new[] { RootPath }.Concat(pathParts).ToArray());
    }

    public SyntheticAudioCase GetCase(string id) =>
        Cases.Single(testCase => string.Equals(testCase.Id, id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<SyntheticAudioCase> GetCases(IEnumerable<string> ids) =>
        ids.Select(GetCase).ToArray();

    private static string ResolveRootPath()
    {
        var configured = Environment.GetEnvironmentVariable("HUSH_AUDIO_E2E_CORPUS_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var outputRoot = System.IO.Path.Combine(AppContext.BaseDirectory, "TestAudio", CorpusName);
        if (Directory.Exists(outputRoot))
            return outputRoot;

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = System.IO.Path.Combine(current.FullName, "tests", "TestAudio", CorpusName);
            if (Directory.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        return outputRoot;
    }
}
