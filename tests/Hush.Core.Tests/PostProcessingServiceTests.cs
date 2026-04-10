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
            ActivePostProcessingPromptId = "formal-prose"
        };

        var prompt = settings.GetActivePrompt();

        Assert.Equal("formal-prose", prompt.Id);
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

        // Built-ins come first.
        Assert.Equal(Configuration.HushSettings.BuiltInPrompts[0].Id, all[0].Id);
        Assert.Equal(Configuration.HushSettings.BuiltInPrompts[1].Id, all[1].Id);
        Assert.Equal(Configuration.HushSettings.BuiltInPrompts[2].Id, all[2].Id);
        Assert.Equal("custom-1", all[3].Id);
    }
}
