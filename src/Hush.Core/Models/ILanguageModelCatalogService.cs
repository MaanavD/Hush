// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

namespace Hush.Core.Models;

/// <summary>
/// Lists Foundry Local language models that can be used for clean-mode rewriting.
/// </summary>
public interface ILanguageModelCatalogService
{
    Task<IReadOnlyList<LanguageModelCatalogItem>> ListSmallLanguageModelsAsync(
        CancellationToken cancellationToken = default);
}