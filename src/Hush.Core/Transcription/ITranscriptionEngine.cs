// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

namespace Hush.Core.Transcription;

/// <summary>
/// Manages the lifecycle of Foundry Local model initialisation and provides
/// live-transcription streaming sessions.
/// </summary>
public interface ITranscriptionEngine : IAsyncDisposable
{
    /// <summary>
    /// Downloads (if required) and loads the specified model alias into Foundry Local.
    /// Progress events are raised so the UI can show a download bar.
    /// <paramref name="statusProgress"/> receives short human-readable strings
    /// describing the current initialisation stage (connecting, resolving,
    /// downloading, loading, ready).
    /// </summary>
    Task InitializeAsync(
        string modelAlias = "nemotron-speech-streaming-en-0.6b",
        IProgress<double>? downloadProgress = null,
        bool downloadHardwareEPs = false,
        IProgress<string>? statusProgress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a new live-transcription session.
    /// Audio should be fed via <see cref="AppendAudioAsync"/> after this call.
    /// </summary>
    /// <param name="streamingCommit">
    /// When <see langword="true"/>, the engine commits words progressively
    /// as they stabilise between consecutive chunks. When <see langword="false"/>,
    /// text is held in the stability buffer until the session stops.
    /// </param>
    Task StartSessionAsync(
        int sampleRate = 16000,
        int channels = 1,
        string language = "en",
        bool streamingCommit = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a raw PCM audio buffer to the active session.
    /// Safe to call from a high-frequency audio callback.
    /// </summary>
    ValueTask AppendAudioAsync(ReadOnlyMemory<byte> pcmData, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the current session and flushes any remaining output.
    /// </summary>
    Task StopSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns an async stream of normalised transcription results from the
    /// active session. Each <see cref="TranscriptionResult"/> may contain an
    /// interim display text update, a committed delta, or both.
    /// </summary>
    IAsyncEnumerable<TranscriptionResult> GetResultStreamAsync(
        CancellationToken cancellationToken = default);
}
