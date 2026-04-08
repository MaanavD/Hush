// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.Core.Transcription;

namespace Hush.Core.Tests;

public sealed class TranscriptionResultTests
{
    [Fact]
    public void CommittedDelta_EmptyByDefault_ForInterimResult()
    {
        var result = new TranscriptionResult(
            DisplayText: "the quick brown",
            CommittedDelta: string.Empty,
            IsFinal: false);

        Assert.False(result.IsFinal);
        Assert.Empty(result.CommittedDelta);
        Assert.Equal("the quick brown", result.DisplayText);
    }

    [Fact]
    public void FinalResult_HasCommittedDelta()
    {
        var result = new TranscriptionResult(
            DisplayText: "the quick brown fox",
            CommittedDelta: "the quick brown fox",
            IsFinal: true);

        Assert.True(result.IsFinal);
        Assert.NotEmpty(result.CommittedDelta);
    }
}
