// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.Core.Transcription;

namespace Hush.Core.Tests;

/// <summary>
/// Edge-case tests for <see cref="TranscriptionResult"/> covering real-world
/// transcription output scenarios.
/// </summary>
public sealed class TranscriptionResultEdgeCaseTests
{
    // ── Empty / whitespace text ──────────────────────────────────────────

    [Fact]
    public void EmptyDisplayText_IsValid()
    {
        var result = new TranscriptionResult(string.Empty, string.Empty, IsFinal: false);
        Assert.Empty(result.DisplayText);
        Assert.Empty(result.CommittedDelta);
    }

    [Fact]
    public void WhitespaceOnlyCommittedDelta_IsPreserved()
    {
        // ASR may produce a leading space as a word separator.
        var result = new TranscriptionResult("hello world", " ", IsFinal: false);
        Assert.Equal(" ", result.CommittedDelta);
    }

    // ── Timestamps ───────────────────────────────────────────────────────

    [Fact]
    public void Timestamps_DefaultToNull()
    {
        var result = new TranscriptionResult("text", "text", IsFinal: true);
        Assert.Null(result.StartTime);
        Assert.Null(result.EndTime);
    }

    [Fact]
    public void Timestamps_CanBeExplicitlySet()
    {
        var start = TimeSpan.FromSeconds(1.5);
        var end = TimeSpan.FromSeconds(3.2);
        var result = new TranscriptionResult("text", "text", IsFinal: true, start, end);
        Assert.Equal(start, result.StartTime);
        Assert.Equal(end, result.EndTime);
    }

    // ── Record equality ──────────────────────────────────────────────────

    [Fact]
    public void RecordEquality_SameValues_AreEqual()
    {
        var a = new TranscriptionResult("hello", "hello", IsFinal: true);
        var b = new TranscriptionResult("hello", "hello", IsFinal: true);
        Assert.Equal(a, b);
    }

    [Fact]
    public void RecordEquality_DifferentIsFinal_AreNotEqual()
    {
        var a = new TranscriptionResult("hello", "hello", IsFinal: true);
        var b = new TranscriptionResult("hello", "hello", IsFinal: false);
        Assert.NotEqual(a, b);
    }

    // ── Unicode / Special characters ─────────────────────────────────────

    [Fact]
    public void UnicodeText_IsPreserved()
    {
        var result = new TranscriptionResult(
            "Héllo wörld 你好 🎙",
            "Héllo wörld 你好 🎙",
            IsFinal: true);

        Assert.Contains("🎙", result.DisplayText);
        Assert.Contains("你好", result.CommittedDelta);
    }

    [Fact]
    public void NewlinesInText_ArePreserved()
    {
        var result = new TranscriptionResult(
            "line one\nline two",
            "line one\nline two",
            IsFinal: true);

        Assert.Contains("\n", result.DisplayText);
    }

    // ── Very long text ───────────────────────────────────────────────────

    [Fact]
    public void VeryLongText_IsHandled()
    {
        var longText = new string('a', 100_000);
        var result = new TranscriptionResult(longText, longText, IsFinal: true);
        Assert.Equal(100_000, result.DisplayText.Length);
    }

    // ── Deconstruction (record pattern) ──────────────────────────────────

    [Fact]
    public void Deconstruct_WorksCorrectly()
    {
        var result = new TranscriptionResult("display", "delta", IsFinal: true,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

        var (display, delta, isFinal, start, end) = result;

        Assert.Equal("display", display);
        Assert.Equal("delta", delta);
        Assert.True(isFinal);
        Assert.Equal(TimeSpan.FromSeconds(1), start);
        Assert.Equal(TimeSpan.FromSeconds(2), end);
    }
}
