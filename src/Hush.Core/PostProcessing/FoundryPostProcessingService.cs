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
            var messages = new[]
            {
                // /no_thinking disables Qwen3's chain-of-thought mode via the soft switch.
                ChatMessage.FromSystem("/no_thinking\n" + systemPrompt),
                ChatMessage.FromUser(rawTranscript)
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
    /// Removes Qwen3 chain-of-thought content. Thinking models wrap reasoning in
    /// &lt;think&gt;…&lt;/think&gt; before the final answer; strip that block.
    /// If no closing tag is found the text is returned as-is.
    /// </summary>
    private static string StripThinking(string text)
    {
        var closeIdx = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (closeIdx >= 0)
            return text[(closeIdx + "</think>".Length)..].Trim();
        return text;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _chatClient = null;
        return ValueTask.CompletedTask;
    }
}

