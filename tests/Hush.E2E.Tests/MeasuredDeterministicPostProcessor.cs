using Hush.Core.PostProcessing;

namespace Hush.E2E.Tests;

internal interface IMeasuredPostProcessor : IPostProcessingService
{
    string RawTranscript { get; }

    string? RewrittenText { get; }

    TimeSpan RewriteDuration { get; }
}

internal sealed class MeasuredDeterministicPostProcessor : IMeasuredPostProcessor
{
    private readonly string _rewrittenText;
    private readonly TimeSpan _delay;
    private readonly TimeProvider _timeProvider;

    public MeasuredDeterministicPostProcessor(
        string rewrittenText,
        TimeSpan delay,
        TimeProvider? timeProvider = null)
    {
        _rewrittenText = rewrittenText;
        _delay = delay;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsReady => true;

    public string RawTranscript { get; private set; } = string.Empty;

    public string? RewrittenText { get; private set; }

    public TimeSpan RewriteDuration { get; private set; }

    public Task InitializeAsync(
        string modelAlias,
        IProgress<double>? downloadProgress = null,
        IProgress<string>? statusProgress = null,
        CancellationToken ct = default) => Task.CompletedTask;

    public async Task<string?> RewriteAsync(string rawTranscript, string systemPrompt, CancellationToken ct = default)
    {
        RawTranscript = rawTranscript;
        var started = _timeProvider.GetTimestamp();
        if (_delay > TimeSpan.Zero)
            await Task.Delay(_delay, ct);

        RewrittenText = _rewrittenText;
        RewriteDuration = _timeProvider.GetElapsedTime(started);
        return RewrittenText;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
