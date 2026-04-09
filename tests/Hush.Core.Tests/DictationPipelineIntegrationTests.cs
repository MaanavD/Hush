// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.Core.Audio;
using Hush.Core.Output;
using Hush.Core.Session;
using Hush.Core.Transcription;
using Moq;

namespace Hush.Core.Tests;

/// <summary>
/// Integration-style tests that exercise the full dictation pipeline:
/// capture → transcription → output, using mocked infrastructure.
/// These simulate real-world voice typing workflows similar to Wispr Flow.
/// </summary>
public sealed class DictationPipelineIntegrationTests
{
    // ── Full pipeline: audio → transcription → typing ────────────────────

    [Fact]
    public async Task FullPipeline_AudioToOutput()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        var typedTexts = new List<string>();

        SetupEngine(engineMock, new[]
        {
            new TranscriptionResult("Hello", "Hello", IsFinal: false),
            new TranscriptionResult("Hello there", " there", IsFinal: true),
        });

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<string, CancellationToken, bool>((t, _, _) =>
            {
                typedTexts.Add(t);
                return Task.CompletedTask;
            });

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);

        await session.StartAsync();
        await session.StopAsync();

        Assert.Contains("Hello", typedTexts);
        Assert.Contains(" there", typedTexts);

        await session.DisposeAsync();
    }

    // ── Long dictation with many chunks ──────────────────────────────────

    [Fact]
    public async Task LongDictation_ManyChunks_AllOutput()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        var typedTexts = new List<string>();

        var results = Enumerable.Range(1, 20).Select(i =>
            new TranscriptionResult(
                $"word{i}",
                $" word{i}",
                IsFinal: i == 20)
        ).ToArray();

        SetupEngine(engineMock, results);

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<string, CancellationToken, bool>((t, _, _) =>
            {
                typedTexts.Add(t);
                return Task.CompletedTask;
            });

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);
        await session.StartAsync();
        await session.StopAsync();

        Assert.Equal(20, typedTexts.Count);

        await session.DisposeAsync();
    }

    // ── Event sequence: interim → committed → stopped ────────────────────

    [Fact]
    public async Task EventSequence_CorrectOrder()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        var events = new List<string>();

        SetupEngine(engineMock, new[]
        {
            new TranscriptionResult("partial", string.Empty, IsFinal: false),
            new TranscriptionResult("partial complete", "partial complete", IsFinal: true),
        });

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);
        session.OnInterimText += _ => events.Add("interim");
        session.OnCommittedChunk += _ => events.Add("committed");
        session.OnSessionStopped += () => events.Add("stopped");

        await session.StartAsync();
        await session.StopAsync();

        // Interims should come before committed, stopped should be last
        int firstInterim = events.IndexOf("interim");
        int firstCommitted = events.IndexOf("committed");
        int stoppedIdx = events.IndexOf("stopped");

        Assert.True(firstInterim >= 0, "Should have received interim events");
        Assert.True(firstCommitted > firstInterim, "Committed should come after interim");
        Assert.True(stoppedIdx > firstCommitted, "Stopped should be last");
    }

    // ── Multiple sessions in sequence (repeated usage) ───────────────────

    [Fact]
    public async Task MultipleSessions_IndependentOutput()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        var allTyped = new List<string>();

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<string, CancellationToken, bool>((t, _, _) =>
            {
                allTyped.Add(t);
                return Task.CompletedTask;
            });

        // Session 1
        SetupEngine(engineMock, new[]
        {
            new TranscriptionResult("session one", "session one", IsFinal: true),
        });

        var session1 = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);
        await session1.StartAsync();
        await session1.StopAsync();
        await session1.DisposeAsync();

        // Session 2
        SetupEngine(engineMock, new[]
        {
            new TranscriptionResult("session two", "session two", IsFinal: true),
        });

        var session2 = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);
        await session2.StartAsync();
        await session2.StopAsync();
        await session2.DisposeAsync();

        Assert.Contains("session one", allTyped);
        Assert.Contains("session two", allTyped);
    }

    // ── Unicode text through the pipeline ────────────────────────────────

    [Fact]
    public async Task UnicodeText_PassesThroughPipeline()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        string? typedText = null;

        SetupEngine(engineMock, new[]
        {
            new TranscriptionResult("你好世界 こんにちは", "你好世界 こんにちは", IsFinal: true),
        });

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<string, CancellationToken, bool>((t, _, _) =>
            {
                typedText = t;
                return Task.CompletedTask;
            });

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);
        await session.StartAsync();
        await session.StopAsync();

        Assert.Contains("你好世界", typedText);
        Assert.Contains("こんにちは", typedText);

        await session.DisposeAsync();
    }

    // ── Text with punctuation (speech-to-text common output) ─────────────

    [Fact]
    public async Task PunctuatedText_PreservedExactly()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        string? typed = null;

        SetupEngine(engineMock, new[]
        {
            new TranscriptionResult(
                "Hello, how are you? I'm fine. Thanks!",
                "Hello, how are you? I'm fine. Thanks!",
                IsFinal: true),
        });

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<string, CancellationToken, bool>((t, _, _) => { typed = t; return Task.CompletedTask; });

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);
        await session.StartAsync();
        await session.StopAsync();

        Assert.Equal("Hello, how are you? I'm fine. Thanks!", typed);

        await session.DisposeAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void SetupEngine(Mock<ITranscriptionEngine> engine, TranscriptionResult[] results)
    {
        engine
            .Setup(e => e.StartSessionAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        engine
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(results.ToAsyncEnumerable());
    }
}

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
