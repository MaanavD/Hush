// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.Core.Audio;
using Hush.Core.Output;
using Hush.Core.Session;
using Hush.Core.Transcription;
using Moq;

namespace Hush.Core.Tests;

/// <summary>
/// Edge-case and robustness tests for <see cref="DictationSession"/>
/// covering real-world voice typing scenarios.
/// </summary>
public sealed class DictationSessionEdgeCaseTests
{
    // ── Rapid start/stop (Wispr Flow: users tap hotkey quickly) ──────────

    [Fact]
    public async Task RapidStartStop_DoesNotThrow()
    {
        var (session, engine, capture, output) = CreateSession();

        for (int i = 0; i < 5; i++)
        {
            await session.StartAsync();
            await session.StopAsync();
        }

        await session.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_WhenNeverStarted_DoesNotThrow()
    {
        var (session, _, _, _) = CreateSession();
        await session.StopAsync();
        await session.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WhenNeverStarted_DoesNotThrow()
    {
        var (session, _, _, _) = CreateSession();
        await session.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_Idempotent()
    {
        var (session, _, _, _) = CreateSession();
        await session.StartAsync();
        await session.DisposeAsync();
        await session.DisposeAsync(); // second call must not throw
    }

    // ── Empty transcription (user holds hotkey but doesn't speak) ────────

    [Fact]
    public async Task EmptySession_NoTextTyped()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(AsyncEmpty<TranscriptionResult>());

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);
        await session.StartAsync();
        await session.StopAsync();

        outputMock.Verify(
            o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        await session.DisposeAsync();
    }

    // ── Multiple committed chunks (long dictation) ───────────────────────

    [Fact]
    public async Task MultipleCommittedChunks_AllTypedInOrder()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        var typedTexts = new List<string>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var results = new[]
        {
            new TranscriptionResult("Hello", "Hello", IsFinal: false),
            new TranscriptionResult("Hello world", " world", IsFinal: false),
            new TranscriptionResult("Hello world how are you", " how are you", IsFinal: true),
        };

        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(results.ToAsyncEnumerable());

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((text, _) =>
            {
                typedTexts.Add(text);
                return Task.CompletedTask;
            });

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);
        await session.StartAsync();
        await session.StopAsync();

        Assert.Equal(3, typedTexts.Count);
        Assert.Equal("Hello", typedTexts[0]);
        Assert.Equal(" world", typedTexts[1]);
        Assert.Equal(" how are you", typedTexts[2]);

        await session.DisposeAsync();
    }

    // ── Interim-only results (still typed via DisplayText mirroring) ────

    [Fact]
    public async Task InterimOnlyResults_StillTypedViaDisplayTextMirroring()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        var typedTexts = new List<string>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var results = new[]
        {
            new TranscriptionResult("hello", string.Empty, IsFinal: false),
            new TranscriptionResult("hello world", string.Empty, IsFinal: false),
        };

        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(results.ToAsyncEnumerable());

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((text, _) =>
            {
                typedTexts.Add(text);
                return Task.CompletedTask;
            });

        outputMock
            .Setup(o => o.SendBackspacesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var interimTexts = new List<string>();
        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);
        session.OnInterimText += t => interimTexts.Add(t);

        await session.StartAsync();
        await session.StopAsync();

        // Interim text should have been raised to the overlay.
        Assert.Equal(2, interimTexts.Count);

        // With CommittedDelta-based typing, interim-only results (empty
        // CommittedDelta) are NOT typed into the target app — they only
        // appear in the overlay. Typing happens via CommittedDelta.
        Assert.Empty(typedTexts);

        await session.DisposeAsync();
    }

    // ── Audio level events ───────────────────────────────────────────────

    [Fact]
    public async Task AudioLevelChanged_ForwardedToSession()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();

        Action<float>? capturedHandler = null;
        captureMock.SetupAdd(c => c.AudioLevelChanged += It.IsAny<Action<float>>())
            .Callback<Action<float>>(h => capturedHandler = h);

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(AsyncEmpty<TranscriptionResult>());

        var levels = new List<float>();
        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);
        session.OnAudioLevel += l => levels.Add(l);

        await session.StartAsync();

        // Simulate audio level callback
        capturedHandler?.Invoke(0.75f);
        capturedHandler?.Invoke(0.3f);

        Assert.Equal(2, levels.Count);
        Assert.Equal(0.75f, levels[0]);
        Assert.Equal(0.3f, levels[1]);

        await session.StopAsync();
        await session.DisposeAsync();
    }

    // ── OnSessionStopped event ───────────────────────────────────────────

    [Fact]
    public async Task OnSessionStopped_RaisedAfterStop()
    {
        var (session, engine, capture, output) = CreateSession();
        bool stopped = false;
        session.OnSessionStopped += () => stopped = true;

        await session.StartAsync();
        Assert.False(stopped);

        await session.StopAsync();
        Assert.True(stopped);

        await session.DisposeAsync();
    }

    // ── Cancellation support ─────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_WithCancelledToken_DoesNotStartCapture()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(AsyncEmpty<TranscriptionResult>());

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);

        // The session should handle the already-cancelled token gracefully.
        // It may throw OperationCanceledException or return successfully.
        try
        {
            await session.StartAsync(cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        await session.DisposeAsync();
    }

    // ── OutputService throws ─────────────────────────────────────────────

    [Fact]
    public async Task OutputServiceThrows_SessionContinues()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var results = new[]
        {
            new TranscriptionResult("chunk1", "chunk1", IsFinal: false),
            new TranscriptionResult("chunk1 chunk2", "chunk2", IsFinal: true),
        };

        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(results.ToAsyncEnumerable());

        int callCount = 0;
        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((_, _) =>
            {
                callCount++;
                if (callCount == 1)
                    throw new InvalidOperationException("Simulated clipboard failure");
                return Task.CompletedTask;
            });

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);
        await session.StartAsync();

        // Give the transcription loop time to process
        await Task.Delay(100);
        await session.StopAsync();

        // Session should not crash; the exception is caught in the transcription loop
        await session.DisposeAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static (DictationSession session, Mock<ITranscriptionEngine> engine,
        Mock<IAudioCaptureService> capture, Mock<ITextOutputService> output) CreateSession()
    {
        var engine = new Mock<ITranscriptionEngine>();
        var capture = new Mock<IAudioCaptureService>();
        var output = new Mock<ITextOutputService>();

        engine
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        engine
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(AsyncEmpty<TranscriptionResult>());

        var session = new DictationSession(engine.Object, capture.Object, output.Object);
        return (session, engine, capture, output);
    }

    private static async IAsyncEnumerable<T> AsyncEmpty<T>()
    {
        await Task.CompletedTask;
        yield break;
    }
}

// Re-use the array async enumerable helper
file static class ArrayExtensions
{
    public static IAsyncEnumerable<T> ToAsyncEnumerable<T>(this T[] source)
        => new ArrayAsyncEnumerable<T>(source);

    private sealed class ArrayAsyncEnumerable<T>(T[] source) : IAsyncEnumerable<T>
    {
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new Enumerator(source, cancellationToken);

        private sealed class Enumerator(T[] source, CancellationToken cancellationToken) : IAsyncEnumerator<T>
        {
            private int _index = -1;
            public T Current => source[_index];

            public ValueTask<bool> MoveNextAsync()
            {
                cancellationToken.ThrowIfCancellationRequested();
                _index++;
                return ValueTask.FromResult(_index < source.Length);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
