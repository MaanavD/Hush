// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Globalization;

namespace Hush.Core.Models;

/// <summary>
/// A Foundry Local chat model that is suitable for clean-mode rewriting.
/// </summary>
public sealed record LanguageModelCatalogItem(
    string Alias,
    string DisplayName,
    double ParameterCountBillions,
    int? FileSizeMb,
    bool IsCached,
    string? Capabilities)
{
    public string ParameterLabel => ParameterCountBillions.ToString("0.#", CultureInfo.InvariantCulture) + "B";

    public string FileSizeLabel => FileSizeMb is int mb
        ? mb.ToString("N0", CultureInfo.InvariantCulture) + " MB"
        : "size unknown";
}