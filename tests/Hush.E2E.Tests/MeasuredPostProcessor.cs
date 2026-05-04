using Hush.Core.PostProcessing;

namespace Hush.E2E.Tests;

internal sealed class MeasuredPostProcessor : IMeasuredPostProcessor
{
    private readonly IPostProcessingService _inner;
    private readonly TimeProvider _timeProvider;

    public MeasuredPostProcessor(IPostProcessingService inner, TimeProvider? timeProvider = null)
    {
        _inner = inner;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsReady => _inner.IsReady;

    public string RawTranscript { get; private set; } = string.Empty;

    public string? RewrittenText { get; private set; }

    public TimeSpan RewriteDuration { get; private set; }

    public Task InitializeAsync(
        string modelAlias,
        IProgress<double>? downloadProgress = null,
        IProgress<string>? statusProgress = null,
        CancellationToken ct = default) =>
        _inner.InitializeAsync(modelAlias, downloadProgress, statusProgress, ct);

    public async Task<string?> RewriteAsync(string rawTranscript, string systemPrompt, CancellationToken ct = default)
    {
        RawTranscript = rawTranscript;
        var started = _timeProvider.GetTimestamp();
        RewrittenText = await _inner.RewriteAsync(rawTranscript, systemPrompt, ct);
        RewriteDuration = _timeProvider.GetElapsedTime(started);
        return RewrittenText;
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
