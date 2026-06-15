// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

namespace Hush.Core.PostProcessing;

public sealed record LlmPrompt
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>System message sent to the LLM. Raw transcript is the user message.</summary>
    public required string Prompt { get; init; }
    public bool IsBuiltIn { get; init; }

    /// <summary>Optional emoji shown in the prompt picker. Built-ins only.</summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>Short one-line use-case description shown under the name. Built-ins only.</summary>
    public string Description { get; init; } = string.Empty;
}
