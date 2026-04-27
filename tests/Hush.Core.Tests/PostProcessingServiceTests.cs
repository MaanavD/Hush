// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.Core.PostProcessing;
using Moq;

namespace Hush.Core.Tests;

public sealed class PostProcessingServiceTests
{
    [Fact]
    public async Task RewriteAsync_ReturnsNull_WhenNotInitialized()
    {
        var svc = new FoundryPostProcessingService();

        var result = await svc.RewriteAsync("hello world", "Fix punctuation.");

        Assert.Null(result);
    }

    [Fact]
    public async Task RewriteAsync_ReturnsNull_OnException()
    {
        // Create a mock that simulates a faulting post-processing service.
        var mock = new Mock<IPostProcessingService>();
        mock.Setup(s => s.IsReady).Returns(true);
        mock.Setup(s => s.RewriteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var result = await mock.Object.RewriteAsync("hello", "system prompt");

        Assert.Null(result);
    }

    [Fact]
    public void BuildPromptMessages_ReplacesInputPlaceholderInUserPrompt()
    {
        const string rawTranscript = "I'm testing the real time transcription.";
        const string prompt =
            "Translate the following English text into Chinese accurately and naturally.\n\n" +
            "Rules:\n" +
            "- Output only the Chinese translation.\n\n" +
            "English text:\n{input}";

        var messages = FoundryPostProcessingService.BuildPromptMessages(rawTranscript, prompt);

        Assert.Contains("post-processing assistant", messages.SystemMessage);
        Assert.Contains("/no_think", messages.UserMessage);
        Assert.Contains(rawTranscript, messages.UserMessage);
        Assert.DoesNotContain("{input}", messages.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPromptMessages_UsesPromptAsSystemMessageWhenNoPlaceholder()
    {
        const string rawTranscript = "raw text";
        const string prompt = "Fix punctuation and output only the cleaned text.";

        var messages = FoundryPostProcessingService.BuildPromptMessages(rawTranscript, prompt);

        Assert.Equal(prompt, messages.SystemMessage);
        Assert.Equal("/no_think\nraw text", messages.UserMessage);
    }

    [Fact]
    public void CleanupGenerationSettings_AreDeterministicAndBounded()
    {
        Assert.Equal(0.0f, FoundryPostProcessingService.CleanupTemperature);
        Assert.Equal(1.0f, FoundryPostProcessingService.CleanupTopP);
        Assert.Equal(0, FoundryPostProcessingService.CleanupRandomSeed);
        Assert.Equal(1024, FoundryPostProcessingService.MaxPostProcessingTokens);
    }

    [Fact]
    public void ApplyCleanDictationSafeguards_RemovesLeftoverFillersFromBuiltInCleanPrompt()
    {
        const string modelOutput =
            "Sometimes I see the fellow words, um has been fixed, um sometimes the failure word are not fixed. " +
            "I don't know how this works, but you know, I I like to see have everything fixed.";
        var prompt = Configuration.HushSettings.BuiltInPrompts[0].Prompt;

        var cleaned = FoundryPostProcessingService.ApplyCleanDictationSafeguards(modelOutput, prompt);

        Assert.DoesNotContain("um", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("you know", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("I I", cleaned, StringComparison.Ordinal);
        Assert.Equal(
            "Sometimes I see the fellow words has been fixed sometimes the failure word are not fixed. I don't know how this works, but I like to see have everything fixed.",
            cleaned);
    }

    [Fact]
    public void ApplyCleanDictationSafeguards_DoesNotAlterCustomPromptOutput()
    {
        const string modelOutput = "Keep um and you know exactly because the custom prompt asked for it.";

        var cleaned = FoundryPostProcessingService.ApplyCleanDictationSafeguards(modelOutput, "Custom prompt.");

        Assert.Equal(modelOutput, cleaned);
    }

    [Fact]
    public void BuiltInCleanPrompt_PrioritizesFillerCleanupOverWordPreservation()
    {
        var prompt = Configuration.HushSettings.BuiltInPrompts[0].Prompt;

        Assert.Contains("Cleanup rules override preservation", prompt);
        Assert.Contains("verify no standalone filler phrases", prompt);
        Assert.Contains("fellow words/failure word", prompt);
    }

    [Fact]
    public void GetActivePrompt_ReturnsFallback_WhenIdIsNull()
    {
        var settings = new Configuration.HushSettings
        {
            ActivePostProcessingPromptId = null
        };

        var prompt = settings.GetActivePrompt();

        Assert.Equal(Configuration.HushSettings.BuiltInPrompts[0].Id, prompt.Id);
    }

    [Fact]
    public void GetActivePrompt_ReturnsMatchingPrompt()
    {
        var settings = new Configuration.HushSettings
        {
            ActivePostProcessingPromptId = "email-reply"
        };

        var prompt = settings.GetActivePrompt();

        Assert.Equal("email-reply", prompt.Id);
    }

    [Fact]
    public void AllPrompts_ContainsBuiltInsFirst()
    {
        var settings = new Configuration.HushSettings
        {
            PostProcessingPrompts = new List<LlmPrompt>
            {
                new() { Id = "custom-1", Name = "Custom", Prompt = "Do custom thing." }
            }
        };

        var all = settings.AllPrompts;

        // Built-ins come first, in order.
        var builtInCount = Configuration.HushSettings.BuiltInPrompts.Count;
        for (int i = 0; i < builtInCount; i++)
            Assert.Equal(Configuration.HushSettings.BuiltInPrompts[i].Id, all[i].Id);
        Assert.Equal("custom-1", all[builtInCount].Id);
    }
}
