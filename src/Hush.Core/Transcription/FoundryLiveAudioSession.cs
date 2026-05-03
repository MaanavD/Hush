// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Microsoft.AI.Foundry.Local;
using Microsoft.AI.Foundry.Local.OpenAI;
using Microsoft.Extensions.Logging;

namespace Hush.Core.Transcription;

internal interface ILiveAudioSessionFactory
{
    ILiveAudioSession Create(string modelId, ILogger logger, OpenAIAudioClient? audioClient = null);
}

internal interface ILiveAudioSession : IAsyncDisposable
{
    Task StartAsync(
        int sampleRate,
        int channels,
        string? language,
        int pushQueueCapacity,
        CancellationToken cancellationToken = default);
    ValueTask AppendAsync(ReadOnlyMemory<byte> pcmData, CancellationToken cancellationToken = default);
    IAsyncEnumerable<LiveAudioSessionChunk> GetTranscriptionStreamAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

internal readonly record struct LiveAudioSessionChunk(
    string Text,
    bool IsFinal,
    TimeSpan? StartTime,
    TimeSpan? EndTime);

internal sealed class LiveAudioSessionFactory : ILiveAudioSessionFactory
{
    public ILiveAudioSession Create(string modelId, ILogger logger, OpenAIAudioClient? audioClient = null)
    {
        if (audioClient is null)
            throw new InvalidOperationException(
                "An OpenAIAudioClient is required for live transcription. " +
                "Ensure InitializeAsync has completed successfully before starting a session.");

        return new ManagedLiveAudioSession(audioClient, logger);
    }
}

/// <summary>
/// Wraps the official Foundry Local managed <see cref="LiveAudioTranscriptionSession"/>.
/// Translates its responses into the internal <see cref="LiveAudioSessionChunk"/> stream
/// that <see cref="TranscriptionEngine"/> consumes.
/// </summary>
internal sealed class ManagedLiveAudioSession : ILiveAudioSession
{
    private const int BitsPerSample = 16;

    private readonly OpenAIAudioClient _audioClient;
    private readonly ILogger _logger;

    private LiveAudioTranscriptionSession? _session;
    private static readonly Dictionary<string, string> LanguageAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jp"] = "ja",
        ["kr"] = "ko",
        ["cn"] = "zh"
    };

    private bool _started;
    private bool _stopped;
    private bool _streamConsumed;

    public ManagedLiveAudioSession(OpenAIAudioClient audioClient, ILogger logger)
    {
        _audioClient = audioClient;
        _logger = logger;
    }

    public async Task StartAsync(
        int sampleRate,
        int channels,
        string? language,
        int pushQueueCapacity,
        CancellationToken cancellationToken = default)
    {
        if (_started && !_stopped)
            throw new InvalidOperationException("Session already started. Call StopAsync first.");

        _session = _audioClient.CreateLiveTranscriptionSession();
        _session.Settings.SampleRate = sampleRate;
        _session.Settings.Channels = channels;
        _session.Settings.BitsPerSample = BitsPerSample;
        if (pushQueueCapacity > 0)
            _session.Settings.PushQueueCapacity = pushQueueCapacity;

        var normalizedLanguage = NormalizeLanguageHint(language);
        if (!string.IsNullOrWhiteSpace(normalizedLanguage))
            _session.Settings.Language = normalizedLanguage;

        await _session.StartAsync(cancellationToken).ConfigureAwait(false);

        _started = true;
        _stopped = false;
        _streamConsumed = false;

        _logger.LogInformation(
            "Managed live audio session started (sampleRate={SampleRate}, channels={Channels}, language={Language}, pushQueueCapacity={PushQueueCapacity}).",
            sampleRate, channels, normalizedLanguage ?? "default", pushQueueCapacity);
    }

    internal static string? NormalizeLanguageHint(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return null;

        var trimmed = language.Trim();
        var primarySubtag = trimmed.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)[0];
        if (string.IsNullOrWhiteSpace(primarySubtag))
            return null;

        var normalized = primarySubtag.ToLowerInvariant();
        return LanguageAliases.TryGetValue(normalized, out var alias)
            ? alias
            : normalized;
    }

    public ValueTask AppendAsync(ReadOnlyMemory<byte> pcmData, CancellationToken cancellationToken = default)
    {
        if (_session is null || !_started || _stopped)
            throw new InvalidOperationException("No active streaming session. Call StartAsync first.");

        return _session.AppendAsync(pcmData, cancellationToken);
    }

    public async IAsyncEnumerable<LiveAudioSessionChunk> GetTranscriptionStreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_session is null)
            throw new InvalidOperationException("No active streaming session. Call StartAsync first.");

        if (_streamConsumed)
            throw new InvalidOperationException("GetTranscriptionStreamAsync can only be consumed once per session.");

        _streamConsumed = true;

        await foreach (var response in _session.GetTranscriptionStream()
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            // LiveAudioTranscriptionResponse extends ConversationItem.
            // For input_audio content, .Text is null and .Transcript carries the text.
            // For text content, .Text carries the text and .Transcript is null.
            var rawText = response.Content?[0]?.Transcript ?? response.Content?[0]?.Text;
            if (string.IsNullOrWhiteSpace(rawText))
                continue;

            _logger.LogDebug(
                "LiveAudio chunk: IsFinal={IsFinal} text={Text}",
                response.IsFinal, rawText);

            // StartTime/EndTime are double? seconds on the response; convert to TimeSpan?.
            TimeSpan? startTime = response.StartTime.HasValue
                ? TimeSpan.FromSeconds(response.StartTime.Value)
                : null;
            TimeSpan? endTime = response.EndTime.HasValue
                ? TimeSpan.FromSeconds(response.EndTime.Value)
                : null;

            yield return new LiveAudioSessionChunk(rawText, response.IsFinal, startTime, endTime);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_session is null || !_started || _stopped)
            return;

        _stopped = true;
        await _session.StopAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Managed live audio session stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_session is not null)
                await _session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during managed audio session disposal.");
        }
    }
}
