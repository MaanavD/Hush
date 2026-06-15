using System.Text.Json.Serialization;

namespace Hush.E2E.Tests;

internal sealed record SyntheticAudioCase(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("duration_sec")] double DurationSeconds,
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("tags")] string[] Tags,
    [property: JsonPropertyName("expected_transcript")] string ExpectedTranscript)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(DurationSeconds);
}
