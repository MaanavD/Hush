using System.Runtime.Versioning;

namespace Hush.E2E.Tests;

public sealed class NotepadEditorTextVerificationTests
{
    [Fact]
    public void Matches_normalizes_windows_and_unix_line_endings()
    {
        Assert.True(NotepadEditorTextVerification.Matches("one\r\ntwo\r\nthree", "one\ntwo\nthree"));
    }

    [Fact]
    public void Matches_rejects_real_content_difference()
    {
        Assert.False(NotepadEditorTextVerification.Matches("one\r\ntwo", "one\nthree"));
    }

    [NotepadE2EFact]
    [SupportedOSPlatform("windows")]
    public async Task SetEditorTextAsync_round_trips_through_launched_notepad_editor()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var notepad = await NotepadTestApp.LaunchAsync(timeout.Token);

        const string expected = "Numbers: 123 and dates: 2026-04-15\r\nFinal line.";
        await notepad.SetEditorTextAsync("stale preview text", timeout.Token);
        await notepad.SetEditorTextAsync(expected, timeout.Token);

        var actual = await notepad.WaitForStableTextAsync(TimeSpan.FromSeconds(3), timeout.Token);
        Assert.True(NotepadEditorTextVerification.Matches(actual, expected), actual);
    }
}
