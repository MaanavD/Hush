// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.Core.Models;
using Microsoft.AI.Foundry.Local;

namespace Hush.Core.Tests;

public sealed class FoundryLanguageModelCatalogServiceTests
{
    private static ModelInfo CreateModelInfo(string task)
        => new()
        {
            Id = "test-model:1",
            Name = "test-model",
            Alias = "test-model",
            ProviderType = "AzureFoundry",
            Uri = "azureml://registries/azureml/models/test-model/versions/1",
            ModelType = "ONNX",
            Task = task,
            InputModalities = "text,image",
            OutputModalities = "text",
        };

    [Theory]
    [InlineData("qwen3-0.6b", 0.6)]
    [InlineData("qwen3.5-0.8b", 0.8)]
    [InlineData("qwen3-4b", 4.0)]
    [InlineData("qwen2.5-coder-1.5b", 1.5)]
    [InlineData("phi-3-mini-128k", 3.8)]
    [InlineData("phi-4-mini-reasoning", 3.8)]
    public void TryGetParameterCountBillions_ParsesSmallLanguageModelAliases(
        string alias,
        double expected)
    {
        var actual = FoundryLanguageModelCatalogService.TryGetParameterCountBillions(alias);

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.Value, precision: 3);
    }

    [Theory]
    [InlineData("qwen3-8b")]
    [InlineData("deepseek-r1-14b")]
    [InlineData("phi-4")]
    public void TryGetParameterCountBillions_ParsesLargeOrUnknownAliases(string alias)
    {
        var actual = FoundryLanguageModelCatalogService.TryGetParameterCountBillions(alias);

        if (alias == "phi-4")
            Assert.Null(actual);
        else
            Assert.True(actual > 4.0);
    }

    [Fact]
    public void IsLanguageModel_IncludesChatCompletionModelsWithTextIO()
    {
        var info = CreateModelInfo("chat-completion");

        Assert.True(FoundryLanguageModelCatalogService.IsLanguageModel(info));
    }

    [Fact]
    public void IsLanguageModel_ExcludesVisionLanguageTasks()
    {
        var info = CreateModelInfo("vision-language-chat");

        Assert.False(FoundryLanguageModelCatalogService.IsLanguageModel(info));
    }
}