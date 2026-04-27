// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.Core.Audio;
using Hush.Core.Configuration;
using Hush.Core.Output;
using Hush.Core.PostProcessing;
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
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<TranscriptionResult>());

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);

        // Act
        await session.StartAsync();

        // Assert
        engineMock.Verify(e => e.StartSessionAsync(16000, 1, "en", true, It.IsAny<CancellationToken>()), Times.Once);
        captureMock.Verify(
            c => c.Start(It.IsAny<Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>>()),
            Times.Once);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_PassesRequestedLanguageToEngine()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<TranscriptionResult>());

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);

        await session.StartAsync("fr");

        engineMock.Verify(e => e.StartSessionAsync(16000, 1, "fr", true, It.IsAny<CancellationToken>()), Times.Once);
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
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
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
    public async Task OnInterimText_RaisedForInterimResults_AndOnlyCommittedDeltasAreTyped()
    {
        // Arrange
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var results = new[]
        {
            new TranscriptionResult("see you jason", "see you jason", IsFinal: false),
            new TranscriptionResult("have fun", " have fun", IsFinal: false),
            new TranscriptionResult("we'll miss you", " we'll miss you", IsFinal: true)
        };

        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(results.ToAsyncEnumerable());

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        outputMock
            .Setup(o => o.SendBackspacesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
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
        Assert.Contains("see you jason", interimTexts);
        Assert.Contains("we'll miss you", interimTexts);
        Assert.Contains(" we'll miss you", committedChunks);
        outputMock.Verify(o => o.TypeTextAsync("see you jason", It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
        outputMock.Verify(o => o.TypeTextAsync(" have fun", It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
        outputMock.Verify(o => o.TypeTextAsync(" we'll miss you", It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
        outputMock.Verify(o => o.SendBackspacesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task BackspaceCorrections_AreDrivenByCommitDeltas()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        var typedTexts = new List<string>();
        var backspaceCounts = new List<int>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new TranscriptionResult("It's not wor", "It's not wor", IsFinal: false),
                new TranscriptionResult("It's working", "working", IsFinal: true)
                {
                    BackspaceCount = 7
                }
            }.ToAsyncEnumerable());

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<string, CancellationToken, bool>((text, _, _) =>
            {
                typedTexts.Add(text);
                return Task.CompletedTask;
            });

        outputMock
            .Setup(o => o.SendBackspacesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<int, CancellationToken, bool>((count, _, _) =>
            {
                backspaceCounts.Add(count);
                return Task.CompletedTask;
            });

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);

        await session.StartAsync();
        await session.StopAsync();

        Assert.Equal(new[] { "It's not wor", "working" }, typedTexts);
        Assert.Equal(new[] { 7 }, backspaceCounts);

        await session.DisposeAsync();
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
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new TranscriptionResult("buffered chunk", "buffered chunk", IsFinal: true)
            }.ToAsyncEnumerable());

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<string, CancellationToken, bool>((text, _, _) =>
            {
                typedText.TrySetResult(text);
                return Task.CompletedTask;
            });

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);

        // Act
        await session.StartAsync();
        string committed = await typedText.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert: committed text is typed before the session ends.
        Assert.Equal("buffered chunk", committed);
        outputMock.Verify(o => o.TypeTextAsync("buffered chunk", It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);

        await session.StopAsync();
    }
    [Fact]
    public async Task StartAsync_WithAutoSubmitKey_SendsKeyOnStop()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<TranscriptionResult>());
        outputMock
            .Setup(o => o.SendKeyAsync(It.IsAny<AutoSubmitKey>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);
        await session.StartAsync(autoSubmitKey: AutoSubmitKey.Enter);
        await session.StopAsync();

        outputMock.Verify(o => o.SendKeyAsync(AutoSubmitKey.Enter, It.IsAny<CancellationToken>()), Times.Once);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_PushesTranscriptToBuffer()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        var bufferMock = new Mock<ITranscriptBuffer>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(new[] { new TranscriptionResult("hello", "hello", IsFinal: true) }.ToAsyncEnumerable());
        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        var session = new DictationSession(
            engineMock.Object, captureMock.Object, outputMock.Object,
            transcriptBuffer: bufferMock.Object);

        await session.StartAsync();
        await session.StopAsync();

        bufferMock.Verify(b => b.Push(It.IsAny<string>()), Times.Once);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task StreamingMode_AppliesSubstitutionsBeforeTyping()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        var typedTexts = new List<string>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(new[] { new TranscriptionResult("gonna", "gonna", IsFinal: true) }.ToAsyncEnumerable());
        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<string, CancellationToken, bool>((text, _, _) => { typedTexts.Add(text); return Task.CompletedTask; });

        var substitutions = new[] { new TextSubstitution { Match = "gonna", Replace = "going to" } };
        var session = new DictationSession(
            engineMock.Object, captureMock.Object, outputMock.Object,
            substitutions: substitutions);

        await session.StartAsync();
        await session.StopAsync();

        Assert.Contains("going to", typedTexts);
        Assert.DoesNotContain("gonna", typedTexts);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task SpinnerMode_AppliesSubstitutionsBeforeTyping()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        var typedTexts = new List<string>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(new[] { new TranscriptionResult("wanna", "wanna", IsFinal: true) }.ToAsyncEnumerable());
        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<string, CancellationToken, bool>((text, _, _) => { typedTexts.Add(text); return Task.CompletedTask; });
        outputMock
            .Setup(o => o.SendBackspacesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        var substitutions = new[] { new TextSubstitution { Match = "wanna", Replace = "want to" } };
        var session = new DictationSession(
            engineMock.Object, captureMock.Object, outputMock.Object,
            substitutions: substitutions);

        await session.StartAsync(showSpinner: true);
        await session.StopAsync();

        // The final typed text (spinner flush) should use the substituted form.
        Assert.Contains(typedTexts, t => t.Contains("want to"));
        Assert.DoesNotContain(typedTexts, t => t == "wanna");
        await session.DisposeAsync();
    }

    // ── Spinner mode + post-processing tests ─────────────────────────────

    [Fact]
    public async Task SpinnerMode_WithPostProcessor_ReplacesAccumulatedTextWithRewritten()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        var postProcessorMock = new Mock<IPostProcessingService>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new TranscriptionResult("raw text", "raw text", IsFinal: true)
            }.ToAsyncEnumerable());

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        outputMock
            .Setup(o => o.SendBackspacesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        postProcessorMock
            .Setup(p => p.RewriteAsync("raw text", "Fix punctuation.", It.IsAny<CancellationToken>()))
            .ReturnsAsync("Raw text.");

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object,
            postProcessor: postProcessorMock.Object);

        await session.StartAsync(showSpinner: true, postProcessingPrompt: "Fix punctuation.");
        await session.StopAsync();

        // Should type the rewritten text, NOT the raw text.
        outputMock.Verify(o => o.TypeTextAsync("Raw text.", It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
        outputMock.Verify(o => o.TypeTextAsync("raw text", It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task SpinnerMode_WithPostProcessorReturningNull_TypesRawText()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        var postProcessorMock = new Mock<IPostProcessingService>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new TranscriptionResult("fallback text", "fallback text", IsFinal: true)
            }.ToAsyncEnumerable());

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        outputMock
            .Setup(o => o.SendBackspacesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        // Processor returns null (failure/unavailable).
        postProcessorMock
            .Setup(p => p.RewriteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object,
            postProcessor: postProcessorMock.Object);

        await session.StartAsync(showSpinner: true, postProcessingPrompt: "Fix punctuation.");
        await session.StopAsync();

        // Should fall back to raw text.
        outputMock.Verify(o => o.TypeTextAsync("fallback text", It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task SpinnerMode_WithNullPostProcessor_TypesRawTextUnmodified()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new TranscriptionResult("raw unmodified", "raw unmodified", IsFinal: true)
            }.ToAsyncEnumerable());

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        outputMock
            .Setup(o => o.SendBackspacesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        // No post-processor injected.
        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);

        await session.StartAsync(showSpinner: true, postProcessingPrompt: "Fix punctuation.");
        await session.StopAsync();

        outputMock.Verify(o => o.TypeTextAsync("raw unmodified", It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task SpinnerMode_WithEmptyPromptString_SkipsPostProcessing()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        var postProcessorMock = new Mock<IPostProcessingService>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new TranscriptionResult("skip me", "skip me", IsFinal: true)
            }.ToAsyncEnumerable());

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        outputMock
            .Setup(o => o.SendBackspacesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object,
            postProcessor: postProcessorMock.Object);

        // Empty prompt string → skip post-processing.
        await session.StartAsync(showSpinner: true, postProcessingPrompt: "");
        await session.StopAsync();

        postProcessorMock.Verify(
            p => p.RewriteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        outputMock.Verify(o => o.TypeTextAsync("skip me", It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task CleanStreamingPreview_ForcesStreamingCommit()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<TranscriptionResult>());

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);

        await session.StartAsync(streamingCommit: false, outputMode: DictationOutputMode.CleanStreamingPreview);
        await session.StopAsync();

        engineMock.Verify(e => e.StartSessionAsync(16000, 1, "en", true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CleanStreamingPreview_TypesStablePreviewThenReplacesWithRewriteBeforeAutoSubmit()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        var postProcessorMock = new Mock<IPostProcessingService>();
        var operations = new List<string>();
        var postProcessingStates = new List<bool>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new TranscriptionResult("hello", "hello", IsFinal: false),
                new TranscriptionResult("hello world", " world", IsFinal: true)
            }.ToAsyncEnumerable());

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<string, CancellationToken, bool>((text, _, _) =>
            {
                operations.Add($"type:{text}");
                return Task.CompletedTask;
            });
        outputMock
            .Setup(o => o.SendBackspacesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<int, CancellationToken, bool>((count, _, _) =>
            {
                operations.Add($"backspace:{count}");
                return Task.CompletedTask;
            });
        outputMock
            .Setup(o => o.SendKeyAsync(It.IsAny<AutoSubmitKey>(), It.IsAny<CancellationToken>()))
            .Returns<AutoSubmitKey, CancellationToken>((key, _) =>
            {
                operations.Add($"key:{key}");
                return Task.CompletedTask;
            });

        postProcessorMock
            .Setup(p => p.RewriteAsync("hello world", "Fix punctuation.", It.IsAny<CancellationToken>()))
            .ReturnsAsync("Hello world.");

        var session = new DictationSession(
            engineMock.Object,
            captureMock.Object,
            outputMock.Object,
            postProcessor: postProcessorMock.Object);
        session.OnPostProcessingStateChanged += state => postProcessingStates.Add(state);

        await session.StartAsync(
            postProcessingPrompt: "Fix punctuation.",
            autoSubmitKey: AutoSubmitKey.Enter,
            outputMode: DictationOutputMode.CleanStreamingPreview);
        await session.StopAsync();

        Assert.Equal(
            new[] { "type:hello", "type:|", "backspace:6", "type:Hello world.", "key:Enter" },
            operations);
        Assert.Equal(new[] { true, false }, postProcessingStates);
    }

    [Fact]
    public async Task CleanStreamingPreview_DefersFinalAsrCorrectionUntilPostProcessingReplacement()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        var postProcessorMock = new Mock<IPostProcessingService>();
        var operations = new List<string>();
        const string preview = "Hey , I'm testing the real time transcription tell me it's";
        const string finalDelta = ", I'm testing the real time transcription tell me it's working";
        const string finalRaw = "Hey, I'm testing the real time transcription tell me it's working";
        const string cleaned = "Hey, I'm testing the real time transcription tell me it's working.";

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new TranscriptionResult(preview, preview, IsFinal: false),
                new TranscriptionResult(finalRaw, finalDelta, IsFinal: true)
                {
                    BackspaceCount = preview.Length - "Hey".Length
                }
            }.ToAsyncEnumerable());

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<string, CancellationToken, bool>((text, _, _) =>
            {
                operations.Add($"type:{text}");
                return Task.CompletedTask;
            });
        outputMock
            .Setup(o => o.SendBackspacesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<int, CancellationToken, bool>((count, _, _) =>
            {
                operations.Add($"backspace:{count}");
                return Task.CompletedTask;
            });

        postProcessorMock
            .Setup(p => p.RewriteAsync(finalRaw, "Fix punctuation.", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cleaned);

        var session = new DictationSession(
            engineMock.Object,
            captureMock.Object,
            outputMock.Object,
            postProcessor: postProcessorMock.Object);

        await session.StartAsync(
            postProcessingPrompt: "Fix punctuation.",
            outputMode: DictationOutputMode.CleanStreamingPreview);
        await session.StopAsync();

        Assert.Equal(
            new[]
            {
                $"type:{preview}",
                "type:|",
                $"backspace:{preview.Length + 1 - "Hey".Length}",
                $"type:{cleaned["Hey".Length..]}"
            },
            operations);
    }

    [Fact]
    public async Task CleanStreamingPreview_LargeWholeLineRewriteUsesBoundedReplacement()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        var postProcessorMock = new Mock<IPostProcessingService>();
        var operations = new List<string>();
        const string preview = "um I'm testing the real time transcription I hope everything uh is working";
        const string finalRaw = "um I'm testing the real time transcription I hope everything uh is working fine";
        const string cleaned = "I'm testing the real-time transcription. I hope everything is working fine.";

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new TranscriptionResult(preview, preview, IsFinal: false),
                new TranscriptionResult(finalRaw, " fine", IsFinal: true)
            }.ToAsyncEnumerable());

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<string, CancellationToken, bool>((text, _, _) =>
            {
                operations.Add($"type:{text}");
                return Task.CompletedTask;
            });
        outputMock
            .Setup(o => o.SendBackspacesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<int, CancellationToken, bool>((count, _, _) =>
            {
                operations.Add($"backspace:{count}");
                return Task.CompletedTask;
            });
        outputMock
            .Setup(o => o.ReplaceTextAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<string?>()))
            .Returns<int, string, CancellationToken, bool, bool, string?>((count, text, _, _, bounded, existing) =>
            {
                operations.Add($"replace:{count}:{bounded}:{existing}:{text}");
                return Task.CompletedTask;
            });

        postProcessorMock
            .Setup(p => p.RewriteAsync(finalRaw, "Fix punctuation.", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cleaned);

        var session = new DictationSession(
            engineMock.Object,
            captureMock.Object,
            outputMock.Object,
            postProcessor: postProcessorMock.Object);

        await session.StartAsync(
            postProcessingPrompt: "Fix punctuation.",
            outputMode: DictationOutputMode.CleanStreamingPreview);
        await session.StopAsync();

        Assert.Equal(
            new[]
            {
                $"type:{preview}",
                "type:|",
                $"replace:{preview.Length + 1}:True:{preview}|:{cleaned}"
            },
            operations);
    }

    [Fact]
    public async Task CleanStreamingPreview_TranslationUsesExpectedTextReplacement()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        var postProcessorMock = new Mock<IPostProcessingService>();
        var operations = new List<string>();
        const string preview = "Uh today is Monday and the first working day of this week I want to talk to my cow";
        const string translated = "今天是周一，这是本周的第一个工作日。我想和我的同事聊聊。";

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new TranscriptionResult(preview, preview, IsFinal: false),
                new TranscriptionResult(preview, string.Empty, IsFinal: true)
            }.ToAsyncEnumerable());

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<string, CancellationToken, bool>((text, _, _) =>
            {
                operations.Add($"type:{text}");
                return Task.CompletedTask;
            });
        outputMock
            .Setup(o => o.ReplaceTextAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<string?>()))
            .Returns<int, string, CancellationToken, bool, bool, string?>((count, text, _, _, bounded, existing) =>
            {
                operations.Add($"replace:{count}:{bounded}:{existing}:{text}");
                return Task.CompletedTask;
            });

        postProcessorMock
            .Setup(p => p.RewriteAsync(preview, "Translate english to Chinese", It.IsAny<CancellationToken>()))
            .ReturnsAsync(translated);

        var session = new DictationSession(
            engineMock.Object,
            captureMock.Object,
            outputMock.Object,
            postProcessor: postProcessorMock.Object);

        await session.StartAsync(
            postProcessingPrompt: "Translate english to Chinese",
            outputMode: DictationOutputMode.CleanStreamingPreview);
        await session.StopAsync();

        Assert.Equal(
            new[]
            {
                $"type:{preview}",
                "type:|",
                $"replace:{preview.Length + 1}:True:{preview}|:{translated}"
            },
            operations);
    }

    [Fact]
    public async Task CleanStreamingPreview_WithPostProcessorReturningNull_LeavesRawPreview()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        var postProcessorMock = new Mock<IPostProcessingService>();
        var typedTexts = new List<string>();
        var backspaceCounts = new List<int>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(new[] { new TranscriptionResult("fallback text", "fallback text", IsFinal: true) }.ToAsyncEnumerable());

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<string, CancellationToken, bool>((text, _, _) =>
            {
                typedTexts.Add(text);
                return Task.CompletedTask;
            });
        outputMock
            .Setup(o => o.SendBackspacesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<int, CancellationToken, bool>((count, _, _) =>
            {
                backspaceCounts.Add(count);
                return Task.CompletedTask;
            });
        postProcessorMock
            .Setup(p => p.RewriteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var session = new DictationSession(
            engineMock.Object,
            captureMock.Object,
            outputMock.Object,
            postProcessor: postProcessorMock.Object);

        await session.StartAsync(
            postProcessingPrompt: "Fix punctuation.",
            outputMode: DictationOutputMode.CleanStreamingPreview);
        await session.StopAsync();

        Assert.Equal(new[] { "|", "fallback text" }, typedTexts);
        Assert.Equal(new[] { 1 }, backspaceCounts);
    }

    [Fact]
    public async Task CleanStreamingPreview_BackspaceCorrectionsUpdateVisiblePreview()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        var operations = new List<string>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new TranscriptionResult("It's not wor", "It's not wor", IsFinal: false),
                new TranscriptionResult("It's working", "working", IsFinal: true)
                {
                    BackspaceCount = 7
                }
            }.ToAsyncEnumerable());

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<string, CancellationToken, bool>((text, _, _) =>
            {
                operations.Add($"type:{text}");
                return Task.CompletedTask;
            });
        outputMock
            .Setup(o => o.SendBackspacesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<int, CancellationToken, bool>((count, _, _) =>
            {
                operations.Add($"backspace:{count}");
                return Task.CompletedTask;
            });

        var session = new DictationSession(engineMock.Object, captureMock.Object, outputMock.Object);

        await session.StartAsync(outputMode: DictationOutputMode.CleanStreamingPreview);
        await session.StopAsync();

        Assert.Equal(new[] { "type:It's not wor", "backspace:7", "type:working" }, operations);
    }

    [Fact]
    public async Task CleanStreamingPreview_AppliesSubstitutionsToFullVisiblePreview()
    {
        var engineMock = new Mock<ITranscriptionEngine>();
        var captureMock = new Mock<IAudioCaptureService>();
        var outputMock = new Mock<ITextOutputService>();
        var typedTexts = new List<string>();

        engineMock
            .Setup(e => e.StartSessionAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        engineMock
            .Setup(e => e.GetResultStreamAsync(It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new TranscriptionResult("wanna", "wanna", IsFinal: false),
                new TranscriptionResult("wanna go", " go", IsFinal: true)
            }.ToAsyncEnumerable());

        outputMock
            .Setup(o => o.TypeTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns<string, CancellationToken, bool>((text, _, _) =>
            {
                typedTexts.Add(text);
                return Task.CompletedTask;
            });
        outputMock
            .Setup(o => o.SendBackspacesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        var substitutions = new[] { new TextSubstitution { Match = "wanna", Replace = "want to" } };
        var session = new DictationSession(
            engineMock.Object,
            captureMock.Object,
            outputMock.Object,
            substitutions: substitutions);

        await session.StartAsync(outputMode: DictationOutputMode.CleanStreamingPreview);
        await session.StopAsync();

        Assert.Equal(new[] { "want to", " go" }, typedTexts);
    }
}
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

