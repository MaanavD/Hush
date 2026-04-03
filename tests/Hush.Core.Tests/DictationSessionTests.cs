using Hush.Core.Audio;
using Hush.Core.Output;
using Hush.Core.Session;
using Hush.Core.Transcription;
using Moq;

namespace Hush.Core.Tests;

public sealed class DictationSessionTests
{
    [Fact]
    public async Task StartAsync_StartsEngineAndCapture()
    {
        // Arrange
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<TranscriptionResult>());

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);

        // Act
        await session.StartAsync();

        // Assert
        engineMock.Verify(e => e.StartSessionAsync(16000, 1, "en", It.IsAny<CancellationToken>()), Times.Once);
        captureMock.Verify(
            c => c.Start(It.IsAny<Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>>()),
            Times.Once);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_StopsEngineAndCapture()
    {
        // Arrange
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<TranscriptionResult>());

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);
        await session.StartAsync();

        // Act
        await session.StopAsync();

        // Assert
        captureMock.Verify(c => c.Stop(), Times.Once);
        engineMock.Verify(e => e.StopSessionAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnInterimText_RaisedForInterimResults()
    {
        // Arrange
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var results = new[]
        {
            new TranscriptionResult("hello world", string.Empty, IsFinal: false),
            new TranscriptionResult("hello world!", "hello world!", IsFinal: true)
        };

        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(results.ToAsyncEnumerable());

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var interimTexts = new List<string>();
        var committedChunks = new List<string>();

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);
        session.OnInterimText += t => interimTexts.Add(t);
        session.OnCommittedChunk += t => committedChunks.Add(t);

        // Act
        await session.StartAsync();
        await session.StopAsync();

        // Assert
        Assert.Contains("hello world", interimTexts);
        Assert.Contains("hello world!", committedChunks);
        outputMock.Verify(o => o.TypeTextAsync("hello world!", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CommittedText_IsTypedBeforeStopAsync()
    {
        // Arrange
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        var typedText = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new TranscriptionResult("buffered chunk", "buffered chunk", IsFinal: true)
            }.ToAsyncEnumerable());

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((text, _) =>
            {
                typedText.TrySetResult(text);
                return Task.CompletedTask;
            });

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);

        // Act
        await session.StartAsync();
        string committed = await typedText.Task.WaitAsync(TimeSpan.FromSeconds(1));

        // Assert: committed text is typed before the session ends.
        Assert.Equal("buffered chunk", committed);
        outputMock.Verify(o => o.TypeTextAsync("buffered chunk", It.IsAny<CancellationToken>()), Times.Once);

        await session.StopAsync();
    }
}

// Minimal async enumerable helpers for tests without System.Linq.Async
file static class AsyncEnumerable
{
    public static IAsyncEnumerable<T> Empty<T>() => EmptyAsyncEnumerable<T>.Instance;

    private sealed class EmptyAsyncEnumerable<T> : IAsyncEnumerable<T>
    {
        public static readonly EmptyAsyncEnumerable<T> Instance = new();

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => EmptyAsyncEnumerator<T>.Instance;
    }

    private sealed class EmptyAsyncEnumerator<T> : IAsyncEnumerator<T>
    {
        public static readonly EmptyAsyncEnumerator<T> Instance = new();
        public T Current => default!;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

