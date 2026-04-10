// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

namespace Hush.Core.PostProcessing;

/// <summary>
/// Provides optional LLM-based post-processing of accumulated transcripts
/// in spinner mode, before the text is typed into the focused application.
/// </summary>
public interface IPostProcessingService : IAsyncDisposable
{
    /// <summary>
    /// Loads the specified model via the existing <c>FoundryLocalManager</c> instance.
    /// No-op if the model is already loaded.
    /// </summary>
    /// <param name="modelAlias">Foundry Local model alias, e.g. <c>"qwen3-0.6b"</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task InitializeAsync(string modelAlias, CancellationToken ct = default);

    /// <summary>
    /// Rewrites <paramref name="rawTranscript"/> using the provided
    /// <paramref name="systemPrompt"/>. Returns the cleaned text, or
    /// <see langword="null"/> on any failure. Never throws.
    /// </summary>
    Task<string?> RewriteAsync(string rawTranscript, string systemPrompt, CancellationToken ct = default);

    /// <summary>Whether the service has been successfully initialised and is ready to use.</summary>
    bool IsReady { get; }
}
