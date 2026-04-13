// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hush.Core.PostProcessing;

/// <summary>
/// Post-processing service backed by Foundry Local. Uses the existing
/// <see cref="FoundryLocalManager.Instance"/> — never creates a second manager.
/// </summary>
public sealed class FoundryPostProcessingService : IPostProcessingService
{
    private readonly ILogger<FoundryPostProcessingService> _logger;
    private OpenAIChatClient? _chatClient;

    public const string DefaultModelAlias = "qwen3-0.6b";

    public FoundryPostProcessingService(ILogger<FoundryPostProcessingService>? logger = null)
        => _logger = logger ?? NullLogger<FoundryPostProcessingService>.Instance;

    /// <inheritdoc/>
    public bool IsReady => _chatClient is not null;

    /// <inheritdoc/>
    public async Task InitializeAsync(string modelAlias, CancellationToken ct = default)
    {
        if (_chatClient is not null)
            return;

        try
        {
            var manager = FoundryLocalManager.Instance;
            var catalog = await manager.GetCatalogAsync(ct);
            var model = await catalog.GetModelAsync(modelAlias, ct);
            if (model is null)
            {
                _logger.LogWarning("Post-processing model '{Alias}' not found in catalog.", modelAlias);
                return;
            }

            if (!await model.IsCachedAsync())
                await model.DownloadAsync(null);

            await model.LoadAsync();
            _chatClient = await model.GetChatClientAsync(ct);
            _logger.LogDebug("Post-processing model '{Alias}' loaded.", modelAlias);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialise post-processing model '{Alias}'.", modelAlias);
        }
    }

    /// <inheritdoc/>
    public async Task<string?> RewriteAsync(string rawTranscript, string systemPrompt, CancellationToken ct = default)
    {
        if (_chatClient is null)
        {
            _logger.LogDebug("RewriteAsync called but service is not initialised — returning null.");
            return null;
        }

        try
        {
            // Set recommended non-thinking parameters for Qwen3.
            // These must be applied before each call because Settings is shared state.
            _chatClient.Settings.Temperature = 0.7f;
            _chatClient.Settings.TopP = 0.8f;

            var messages = new[]
            {
                ChatMessage.FromSystem(systemPrompt),
                // /no_think is Qwen3's soft switch to disable chain-of-thought.
                // It must appear in the user message; prepend it before the transcript.
                ChatMessage.FromUser("/no_think\n" + rawTranscript)
            };

            var response = await _chatClient.CompleteChatAsync(messages, ct);
            var raw = response?.Choices?[0]?.Message?.Content?.Trim();
            return raw is null ? null : StripThinking(raw);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-processing rewrite failed — returning null.");
            return null;
        }
    }

    /// <summary>
    /// Removes Qwen3 reasoning content from the output.
    /// <list type="bullet">
    ///   <item>If the response contains a &lt;/think&gt; closing tag, everything up to and
    ///         including that tag is discarded (standard thinking-mode output).</item>
    ///   <item>Otherwise, if the response is multi-paragraph, only the <b>last non-empty
    ///         paragraph</b> is returned — Qwen3 without proper tag stripping tends to
    ///         repeat the final answer as the last paragraph after its reasoning prose.</item>
    /// </list>
    /// </summary>
    private static string StripThinking(string text)
    {
        // Case 1: model emitted <think>...</think> tags — keep only what follows.
        var closeIdx = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (closeIdx >= 0)
            return text[(closeIdx + "</think>".Length)..].Trim();

        // Case 2: backend stripped tags but left reasoning as leading paragraphs.
        // The actual answer is the last non-empty paragraph.
        var paragraphs = text
            .Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();

        if (paragraphs.Length > 1)
            return paragraphs[^1];

        return text;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _chatClient = null;
        return ValueTask.CompletedTask;
    }
}

