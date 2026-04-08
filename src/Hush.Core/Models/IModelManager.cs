// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

namespace Hush.Core.Models;

/// <summary>
/// Downloads, loads, and unloads Foundry Local models.
/// </summary>
public interface IModelManager
{
    /// <summary>
    /// Returns <see langword="true"/> if the specified model alias is already
    /// cached on disk and ready to load without a network download.
    /// </summary>
    Task<bool> IsModelCachedAsync(string modelAlias, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the model if it is not already cached, then loads it.
    /// <paramref name="downloadProgress"/> receives values in [0, 1].
    /// </summary>
    Task EnsureModelReadyAsync(
        string modelAlias,
        IProgress<double>? downloadProgress = null,
        CancellationToken cancellationToken = default);
}
