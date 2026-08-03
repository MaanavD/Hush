// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

namespace Hush.App.ViewModels;

internal sealed class PostProcessingWarmupController : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _currentSource;
    private int _currentVersion;

    public PostProcessingWarmupOperation Begin(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _currentSource?.Cancel();

            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var version = ++_currentVersion;
            _currentSource = source;
            return new PostProcessingWarmupOperation(this, version, source);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _currentSource?.Cancel();
            _currentSource?.Dispose();
            _currentSource = null;
        }
    }

    internal bool IsCurrent(PostProcessingWarmupOperation operation)
    {
        lock (_gate)
        {
            return _currentVersion == operation.Version
                && ReferenceEquals(_currentSource, operation.Source);
        }
    }

    internal bool IsLatestVersion(int version)
    {
        lock (_gate)
        {
            return _currentVersion == version;
        }
    }

    internal void Complete(PostProcessingWarmupOperation operation)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_currentSource, operation.Source))
                _currentSource = null;
        }

        operation.Source.Dispose();
    }
}

internal sealed class PostProcessingWarmupOperation : IDisposable
{
    private readonly PostProcessingWarmupController _owner;
    private bool _disposed;

    internal PostProcessingWarmupOperation(
        PostProcessingWarmupController owner,
        int version,
        CancellationTokenSource source)
    {
        _owner = owner;
        Version = version;
        Source = source;
    }

    public int Version { get; }

    public CancellationToken Token => Source.Token;

    public bool IsCurrent => !_disposed && _owner.IsCurrent(this);

    public bool IsLatestVersion => _owner.IsLatestVersion(Version);

    internal CancellationTokenSource Source { get; }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _owner.Complete(this);
    }
}
