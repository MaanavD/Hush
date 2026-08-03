// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.Core.Session;

namespace Hush.Core.Tests;

public sealed class TranscriptBufferTests
{
    [Fact]
    public void Push_AddsToBuffer()
    {
        var buffer = new TranscriptBuffer();
        buffer.Push("first");
        Assert.Single(buffer.GetAll());
    }

    [Fact]
    public void GetLatest_ReturnsNewest()
    {
        var buffer = new TranscriptBuffer();
        buffer.Push("older");
        buffer.Push("newer");
        Assert.Equal("newer", buffer.GetLatest());
    }

    [Fact]
    public void GetAll_ReturnsNewestFirst()
    {
        var buffer = new TranscriptBuffer();
        buffer.Push("first");
        buffer.Push("second");
        buffer.Push("third");

        var all = buffer.GetAll();
        Assert.Equal(3, all.Count);
        Assert.Equal("third",  all[0]);
        Assert.Equal("second", all[1]);
        Assert.Equal("first",  all[2]);
    }

    [Fact]
    public void Push_ExceedsCapacity_DropsOldest()
    {
        var buffer = new TranscriptBuffer();
        for (int i = 1; i <= 6; i++)
            buffer.Push($"item{i}");

        var all = buffer.GetAll();
        Assert.Equal(buffer.Capacity, all.Count);
        // Oldest item1 should have been dropped; item6 should be newest.
        Assert.Equal("item6", all[0]);
        Assert.DoesNotContain("item1", all);
    }

    [Fact]
    public void GetLatest_EmptyBuffer_ReturnsNull()
    {
        var buffer = new TranscriptBuffer();
        Assert.Null(buffer.GetLatest());
    }
}
