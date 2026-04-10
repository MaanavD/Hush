// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.Core.PostProcessing;

namespace Hush.Core.Tests;

public sealed class SubstitutionProcessorTests
{
    [Fact]
    public void Apply_EmptyInput_ReturnsUnchanged()
    {
        var subs = new[] { new TextSubstitution { Match = "hello", Replace = "hi" } };
        Assert.Equal(string.Empty, SubstitutionProcessor.Apply(string.Empty, subs));
        Assert.Equal("", SubstitutionProcessor.Apply("", subs));
    }

    [Fact]
    public void Apply_NoMatchingRule_ReturnsUnchanged()
    {
        var subs = new[] { new TextSubstitution { Match = "foo", Replace = "bar" } };
        const string input = "hello world";
        Assert.Equal(input, SubstitutionProcessor.Apply(input, subs));
    }

    [Fact]
    public void Apply_CaseInsensitiveMatch_Replaces()
    {
        var subs = new[] { new TextSubstitution { Match = "hello", Replace = "hi" } };
        Assert.Equal("hi world", SubstitutionProcessor.Apply("Hello world", subs));
        Assert.Equal("hi world", SubstitutionProcessor.Apply("HELLO world", subs));
        Assert.Equal("hi world", SubstitutionProcessor.Apply("hello world", subs));
    }

    [Fact]
    public void Apply_WholeWordBoundary_DoesNotPartialMatch()
    {
        var subs = new[] { new TextSubstitution { Match = "he", Replace = "X" } };
        // "hello" contains "he" but it is not a whole word — should not be replaced.
        Assert.Equal("hello", SubstitutionProcessor.Apply("hello", subs));
        // A standalone "he" should be replaced.
        Assert.Equal("X said hello", SubstitutionProcessor.Apply("he said hello", subs));
    }

    [Fact]
    public void Apply_MultipleRules_AllApplied()
    {
        var subs = new[]
        {
            new TextSubstitution { Match = "foo", Replace = "bar" },
            new TextSubstitution { Match = "baz", Replace = "qux" },
        };
        Assert.Equal("bar and qux", SubstitutionProcessor.Apply("foo and baz", subs));
    }

    [Fact]
    public void Apply_EmptyMatchString_SkipsRule()
    {
        var subs = new[]
        {
            new TextSubstitution { Match = "", Replace = "X" },
            new TextSubstitution { Match = "hello", Replace = "hi" },
        };
        Assert.Equal("hi world", SubstitutionProcessor.Apply("hello world", subs));
    }
}
