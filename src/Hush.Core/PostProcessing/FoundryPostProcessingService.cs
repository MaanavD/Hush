// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Text.RegularExpressions;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Hush.Core.Configuration;
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
    private const string InputPlaceholder = "{input}";

    private readonly ILogger<FoundryPostProcessingService> _logger;
    private OpenAIChatClient? _chatClient;
    private static readonly Regex CleanDictationFillerRegex = new(
        @"\s*,?\s*\b(?:um+|uh+|er+|ah+|you\s+know)\b\s*,?\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex CleanDictationRepeatedIRegex = new(
        @"\bI\s+I\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CleanDictationBetterAlternativeInputRegex = new(
        @"\bto\s+(?<old>[a-z0-9][a-z0-9\s-]{0,60}?)\s*[.!?]\s*actually,?\s*wait,?\s*(?<new>[a-z0-9][a-z0-9\s-]{0,60}?)\s+is\s+better\s+because\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SpaceBeforePunctuationRegex = new(
        @"\s+([,.;:!?])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SpaceAfterPunctuationRegex = new(
        @"([,.;:!?])(?=\S)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public const string DefaultModelAlias = "qwen3-0.6b";
    internal const float CleanupTemperature = 0.0f;
    internal const float CleanupTopP = 1.0f;
    internal const int CleanupRandomSeed = 0;
    internal const int MaxPostProcessingTokens = 1024;

    public FoundryPostProcessingService(ILogger<FoundryPostProcessingService>? logger = null)
        => _logger = logger ?? NullLogger<FoundryPostProcessingService>.Instance;

    /// <inheritdoc/>
    public bool IsReady => _chatClient is not null;

    /// <inheritdoc/>
    public async Task InitializeAsync(
        string modelAlias,
        IProgress<double>? downloadProgress = null,
        IProgress<string>? statusProgress = null,
        CancellationToken ct = default)
    {
        if (_chatClient is not null)
        {
            statusProgress?.Report("Cleaning model ready");
            return;
        }

        try
        {
            statusProgress?.Report($"Resolving cleaning model '{modelAlias}'…");
            var manager = FoundryLocalManager.Instance;
            var catalog = await manager.GetCatalogAsync(ct);
            var model = await catalog.GetModelAsync(modelAlias, ct);
            if (model is null)
            {
                _logger.LogWarning("Post-processing model '{Alias}' not found in catalog.", modelAlias);
                statusProgress?.Report($"Cleaning model '{modelAlias}' not found");
                return;
            }

            statusProgress?.Report($"Checking cache for '{modelAlias}'…");
            var alreadyCached = await model.IsCachedAsync();
            if (!alreadyCached)
            {
                _logger.LogInformation("Downloading post-processing model '{Alias}'…", modelAlias);
                bool announcedDownload = false;
                Action<float> sdkProgress = pct =>
                {
                    if (!announcedDownload && pct > 0.01f)
                    {
                        announcedDownload = true;
                        statusProgress?.Report($"Downloading cleaning model '{modelAlias}'…");
                    }
                    downloadProgress?.Report(pct / 100.0);
                    if (pct > 0.01f)
                        statusProgress?.Report($"Downloading cleaning model… {pct / 100.0:P0}");
                };
                await model.DownloadAsync(sdkProgress);
                downloadProgress?.Report(1.0);
                _logger.LogInformation("Downloaded post-processing model '{Alias}'.", modelAlias);
            }
            else
            {
                _logger.LogDebug("Post-processing model '{Alias}' already cached.", modelAlias);
                downloadProgress?.Report(1.0);
            }

            statusProgress?.Report("Loading cleaning model into runtime…");
            await model.LoadAsync();
            _chatClient = await model.GetChatClientAsync(ct);
            statusProgress?.Report("Cleaning model ready");
            _logger.LogInformation("Post-processing model '{Alias}' loaded.", modelAlias);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialise post-processing model '{Alias}'.", modelAlias);
            statusProgress?.Report($"Cleaning model failed to load: {ex.Message}");
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
            // Dictation cleanup should be deterministic and bounded; small local
            // models can otherwise echo instructions until the backend limit.
            // These must be applied before each call because Settings is shared state.
            _chatClient.Settings.Temperature = CleanupTemperature;
            _chatClient.Settings.TopP = CleanupTopP;
            _chatClient.Settings.RandomSeed = CleanupRandomSeed;
            _chatClient.Settings.MaxTokens = MaxPostProcessingTokens;

            var transcriptForPrompt = IsBuiltInCleanDictationPrompt(systemPrompt)
                ? ApplyCleanDictationInputSafeguards(rawTranscript)
                : rawTranscript;
            var promptMessages = BuildPromptMessages(transcriptForPrompt, systemPrompt);
            var messages = new[]
            {
                ChatMessage.FromSystem(promptMessages.SystemMessage),
                ChatMessage.FromUser(promptMessages.UserMessage)
            };

            var response = await _chatClient.CompleteChatAsync(messages, ct);
            var raw = response?.Choices?[0]?.Message?.Content?.Trim();
            if (raw is null)
                return null;

            return ApplyCleanDictationSafeguards(StripThinking(raw), systemPrompt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-processing rewrite failed — returning null.");
            return null;
        }
    }

    internal static PromptMessages BuildPromptMessages(string rawTranscript, string systemPrompt)
    {
        if (systemPrompt.Contains(InputPlaceholder, StringComparison.OrdinalIgnoreCase))
        {
            return new PromptMessages(
                "You are Hush's post-processing assistant. Follow the user's prompt exactly and output only the final transformed text.",
                WithNoThink(systemPrompt.Replace(InputPlaceholder, rawTranscript, StringComparison.OrdinalIgnoreCase)));
        }

        return new PromptMessages(systemPrompt, WithNoThink(rawTranscript));
    }

    private static string WithNoThink(string text) => "/no_think\n" + text;

    internal readonly record struct PromptMessages(string SystemMessage, string UserMessage);

    internal static string ApplyCleanDictationSafeguards(string text, string systemPrompt)
    {
        if (!IsBuiltInCleanDictationPrompt(systemPrompt) || string.IsNullOrWhiteSpace(text))
            return text;

        var cleaned = CleanDictationFillerRegex.Replace(text, " ");
        cleaned = CleanDictationRepeatedIRegex.Replace(cleaned, "I");
        cleaned = NormalizeCleanDictationOutput(cleaned);
        return CapitalizeFirstAsciiLetter(cleaned);
    }

    internal static string ApplyCleanDictationInputSafeguards(string rawTranscript)
    {
        if (string.IsNullOrWhiteSpace(rawTranscript))
            return rawTranscript;

        var cleaned = CleanDictationBetterAlternativeInputRegex.Replace(
            rawTranscript,
            match => $"to {match.Groups["new"].Value.Trim()} because");
        return NormalizeCleanDictationOutput(cleaned);
    }

    private static bool IsBuiltInCleanDictationPrompt(string systemPrompt)
    {
        var cleanPrompt = HushSettings.BuiltInPrompts.First(p => p.Id == "clean-dictation").Prompt;
        return string.Equals(systemPrompt, cleanPrompt, StringComparison.Ordinal);
    }

    private static string NormalizeCleanDictationOutput(string text)
    {
        var normalized = WhitespaceRegex.Replace(text, " ");
        normalized = SpaceBeforePunctuationRegex.Replace(normalized, "$1");
        normalized = SpaceAfterPunctuationRegex.Replace(normalized, "$1 ");
        return normalized.Trim().TrimStart(',', ';', ':').TrimStart();
    }

    private static string CapitalizeFirstAsciiLetter(string text)
    {
        if (text.Length == 0 || text[0] < 'a' || text[0] > 'z')
            return text;

        return char.ToUpperInvariant(text[0]) + text[1..];
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

