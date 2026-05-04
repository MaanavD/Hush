// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Globalization;
using System.Text.RegularExpressions;
using Hush.Core.Transcription;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hush.Core.Models;

/// <summary>
/// Reads the Foundry Local catalog and returns small chat-completion models.
/// </summary>
public sealed class FoundryLanguageModelCatalogService : ILanguageModelCatalogService
{
    private const double MaxParameterCountBillions = 4.0;
    private static readonly Regex ParameterCountRegex = new(
        @"(?<![\d.])(?<count>\d+(?:\.\d+)?)\s*b\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly SemaphoreSlim ManagerGate = new(1, 1);

    private readonly ILogger<FoundryLanguageModelCatalogService> _logger;

    public FoundryLanguageModelCatalogService(ILogger<FoundryLanguageModelCatalogService>? logger = null)
    {
        _logger = logger ?? NullLogger<FoundryLanguageModelCatalogService>.Instance;
    }

    public async Task<IReadOnlyList<LanguageModelCatalogItem>> ListSmallLanguageModelsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureManagerAsync(cancellationToken);

        var catalog = await FoundryLocalManager.Instance.GetCatalogAsync(cancellationToken);
        var models = await catalog.ListModelsAsync(cancellationToken);
        var candidates = new List<LanguageModelCatalogItem>();

        foreach (var model in models)
        {
            if (TryCreateCatalogItem(model, out var item))
                candidates.Add(item);
        }

        return candidates
            .GroupBy(item => item.Alias, StringComparer.OrdinalIgnoreCase)
            .Select(ChooseRepresentative)
            .OrderBy(item => item.ParameterCountBillions)
            .ThenBy(item => item.Alias, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task EnsureManagerAsync(CancellationToken cancellationToken)
    {
        if (FoundryLocalManager.IsInitialized)
            return;

        await ManagerGate.WaitAsync(cancellationToken);
        try
        {
            if (FoundryLocalManager.IsInitialized)
                return;

            await FoundryLocalManager.CreateAsync(
                FoundryRuntimeConfiguration.Create("Hush", _logger),
                NullLogger.Instance,
                cancellationToken);
        }
        finally
        {
            ManagerGate.Release();
        }
    }

    private static bool TryCreateCatalogItem(IModel model, out LanguageModelCatalogItem item)
    {
        var info = model.Info;
        item = null!;

        if (!IsLanguageModel(info))
            return false;

        var parameterCount = TryGetParameterCountBillions(
            model.Alias,
            model.Id,
            info.Alias,
            info.Id,
            info.Name,
            info.DisplayName);

        if (parameterCount is null or > MaxParameterCountBillions)
            return false;

        item = new LanguageModelCatalogItem(
            model.Alias,
            string.IsNullOrWhiteSpace(info.DisplayName) ? model.Alias : info.DisplayName,
            parameterCount.Value,
            info.FileSizeMb,
            info.Cached,
            info.Capabilities);
        return true;
    }

    internal static bool IsLanguageModel(ModelInfo info)
        => string.Equals(info.Task, "chat-completion", StringComparison.OrdinalIgnoreCase)
           && HasTextModality(info.InputModalities)
           && HasTextModality(info.OutputModalities);

    internal static double? TryGetParameterCountBillions(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var match = ParameterCountRegex.Match(candidate);
            if (match.Success
                && double.TryParse(
                    match.Groups["count"].Value,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                return value;
            }
        }

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var normalized = candidate.ToLowerInvariant();
            if (normalized.Contains("phi-3-mini", StringComparison.Ordinal)
                || normalized.Contains("phi-3.5-mini", StringComparison.Ordinal)
                || normalized.Contains("phi-4-mini", StringComparison.Ordinal))
            {
                return 3.8;
            }
        }

        return null;
    }

    private static bool HasTextModality(string? modalities)
        => modalities?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(modality => string.Equals(modality, "text", StringComparison.OrdinalIgnoreCase)) == true;

    private static LanguageModelCatalogItem ChooseRepresentative(IEnumerable<LanguageModelCatalogItem> group)
    {
        var items = group.ToList();
        var representative = items
            .OrderByDescending(item => item.IsCached)
            .ThenBy(item => item.FileSizeMb ?? int.MaxValue)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .First();
        var fileSizes = items
            .Where(item => item.FileSizeMb.HasValue)
            .Select(item => item.FileSizeMb!.Value)
            .ToList();

        return representative with
        {
            FileSizeMb = fileSizes.Count == 0 ? null : fileSizes.Min(),
            IsCached = items.Any(item => item.IsCached),
        };
    }
}