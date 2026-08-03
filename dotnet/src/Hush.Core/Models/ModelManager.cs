// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Hush.Core.Transcription;

namespace Hush.Core.Models;

/// <summary>
/// Delegates model download and load operations to the Foundry Local SDK.
/// This class is used during app startup to ensure the model is ready before
/// dictation begins.
/// </summary>
public sealed class ModelManager : IModelManager, IAsyncDisposable
{
    private readonly ILogger<ModelManager> _logger;
    private bool _managerCreated;

    public ModelManager(ILogger<ModelManager>? logger = null)
    {
        _logger = logger ?? NullLogger<ModelManager>.Instance;
    }

    private async Task EnsureManagerAsync(CancellationToken cancellationToken)
    {
        if (!_managerCreated && !FoundryLocalManager.IsInitialized)
        {
            await FoundryLocalManager.CreateAsync(
                FoundryRuntimeConfiguration.Create("Hush", _logger),
                NullLogger.Instance);
        }
        _managerCreated = true;
    }

    /// <inheritdoc/>
    public async Task<bool> IsModelCachedAsync(
        string modelAlias,
        CancellationToken cancellationToken = default)
    {
        await EnsureManagerAsync(cancellationToken);
        var catalog = await FoundryLocalManager.Instance.GetCatalogAsync(cancellationToken);
        var model = await catalog.GetModelAsync(modelAlias, cancellationToken);
        if (model is null) return false;
        return await model.IsCachedAsync();
    }

    /// <inheritdoc/>
    public async Task EnsureModelReadyAsync(
        string modelAlias,
        IProgress<double>? downloadProgress = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureManagerAsync(cancellationToken);
        var manager = FoundryLocalManager.Instance;

        if (OperatingSystem.IsWindows() && downloadProgress is not null)
            await manager.DownloadAndRegisterEpsAsync();

        var catalog = await manager.GetCatalogAsync(cancellationToken);
        var model = await catalog.GetModelAsync(modelAlias, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Model '{modelAlias}' not found in catalog.");

        if (!await model.IsCachedAsync())
        {
            _logger.LogInformation("Downloading model '{ModelAlias}'.", modelAlias);
            // SDK progress is 0–100 float; normalise to 0.0–1.0 for our interface.
            Action<float>? sdkProgress = downloadProgress is null
                ? null
                : p => downloadProgress.Report(p / 100.0);
            await model.DownloadAsync(sdkProgress);
        }

        await model.LoadAsync();
        _logger.LogInformation("Model '{ModelAlias}' is ready.", modelAlias);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_managerCreated)
            FoundryLocalManager.Instance.Dispose();
        return ValueTask.CompletedTask;
    }
}
