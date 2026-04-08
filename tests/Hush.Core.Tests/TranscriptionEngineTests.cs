// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Reflection;
using System.Threading.Channels;
using Hush.Core.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hush.Core.Tests;

public sealed class TranscriptionEngineTests
{
    [Fact]
    public async Task StartSessionAsync_WithoutInitialize_Throws()
    {
        var engine = new TranscriptionEngine();

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartSessionAsync());
    }

    [Fact]
    public async Task GetResultStreamAsync_WithoutSession_Throws()
    {
        var engine = new TranscriptionEngine();

        var stream = engine.GetResultStreamAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in stream)
            {
            }
        });
    }

    [Fact]
    public async Task StopSessionAsync_WithoutStart_DoesNotThrow()
    {
        var engine = new TranscriptionEngine();

        await engine.StopSessionAsync();
    }

    [Fact]
    public async Task DisposeAsync_WithoutInit_DoesNotThrow()
    {
        var engine = new TranscriptionEngine();

        await engine.DisposeAsync();
    }

    [Fact]
    public async Task AppendAudioAsync_NoActiveSession_IsNoOp()
    {
        var engine = new TranscriptionEngine();

        await engine.AppendAudioAsync(new byte[] { 1, 2, 3, 4 });
    }

    [Fact]
    public async Task LiveSession_UsesConfiguredLanguage_AndNormalizesCommittedOutput()
    {
        var factory = new FakeLiveAudioSessionFactory();
        var engine = new TranscriptionEngine(new NullLogger<TranscriptionEngine>(), factory);
        SetModelId(engine, "whisper-test");

        await engine.StartSessionAsync(language: "fr");
        factory.Session.Emit(new LiveAudioSessionChunk("bonjour", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.4)));
        factory.Session.Emit(new LiveAudioSessionChunk("bonjour", true, TimeSpan.Zero, TimeSpan.FromSeconds(0.4)));
        factory.Session.Emit(new LiveAudioSessionChunk("tout le monde", true, TimeSpan.FromSeconds(0.4), TimeSpan.FromSeconds(0.9)));
        await engine.StopSessionAsync();

        var results = await CollectAsync(engine.GetResultStreamAsync());

        Assert.Equal("fr", factory.Session.StartLanguage);
        Assert.Collection(
            results,
            item =>
            {
                // Streaming mode now commits the first monotonic chunk immediately.
                Assert.Equal("bonjour", item.DisplayText);
                Assert.Equal("bonjour", item.CommittedDelta);
                Assert.False(item.IsFinal);
            },
            item =>
            {
                // The matching final chunk adds no further delta.
                Assert.Equal("bonjour", item.DisplayText);
                Assert.Equal(string.Empty, item.CommittedDelta);
                Assert.True(item.IsFinal);
            },
            item =>
            {
                Assert.Equal("bonjour tout le monde", item.DisplayText);
                Assert.Equal(" tout le monde", item.CommittedDelta);
                Assert.True(item.IsFinal);
            });
    }

    [Fact]
    public async Task LiveSession_BatchMode_HoldsTextUntilFinal()
    {
        var factory = new FakeLiveAudioSessionFactory();
        var engine = new TranscriptionEngine(new NullLogger<TranscriptionEngine>(), factory);
        SetModelId(engine, "whisper-test");

        await engine.StartSessionAsync(language: "en", streamingCommit: false);
        factory.Session.Emit(new LiveAudioSessionChunk("bonjour", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.4)));
        factory.Session.Emit(new LiveAudioSessionChunk("bonjour", true, TimeSpan.Zero, TimeSpan.FromSeconds(0.4)));
        factory.Session.Emit(new LiveAudioSessionChunk("tout le monde", true, TimeSpan.FromSeconds(0.4), TimeSpan.FromSeconds(0.9)));
        await engine.StopSessionAsync();

        var results = await CollectAsync(engine.GetResultStreamAsync());

        Assert.Collection(
            results,
            item =>
            {
                // Batch mode: non-final chunk does not commit (no stable prefix yet).
                Assert.Equal("bonjour", item.DisplayText);
                Assert.Equal(string.Empty, item.CommittedDelta);
                Assert.False(item.IsFinal);
            },
            item =>
            {
                Assert.Equal("bonjour", item.DisplayText);
                Assert.Equal("bonjour", item.CommittedDelta);
                Assert.True(item.IsFinal);
            },
            item =>
            {
                Assert.Equal("bonjour tout le monde", item.DisplayText);
                Assert.Equal(" tout le monde", item.CommittedDelta);
                Assert.True(item.IsFinal);
            });
    }

    [Fact]
    public async Task LiveSession_StreamingMode_CommitsStablePrefix()
    {
        // Simulates a real dictation: 3 non-final chunks where words
        // stabilise progressively, then a final chunk commits the rest.
        var factory = new FakeLiveAudioSessionFactory();
        var engine = new TranscriptionEngine(new NullLogger<TranscriptionEngine>(), factory);
        SetModelId(engine, "whisper-test");

        await engine.StartSessionAsync(language: "en", streamingCommit: true);

        // Chunk 1: model's first hypothesis -- nothing to compare, no commit.
        factory.Session.Emit(new LiveAudioSessionChunk(
            "hello world", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.2)));

        // Chunk 2: cumulative growth continues, so the new suffix commits.
        factory.Session.Emit(new LiveAudioSessionChunk(
            "hello world how", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.4)));

        // Chunk 3: more monotonic growth.
        factory.Session.Emit(new LiveAudioSessionChunk(
            "hello world how are", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.6)));

        // Final chunk: remaining text flushes.
        factory.Session.Emit(new LiveAudioSessionChunk(
            "hello world how are you", true, TimeSpan.Zero, TimeSpan.FromSeconds(0.8)));

        await engine.StopSessionAsync();
        var results = await CollectAsync(engine.GetResultStreamAsync());

        Assert.Equal(4, results.Count);

        // Chunk 1: the initial cumulative hypothesis commits immediately.
        Assert.Equal("hello world", results[0].CommittedDelta);
        Assert.False(results[0].IsFinal);

        // Chunk 2: only the newly grown suffix is typed.
        Assert.Equal(" how", results[1].CommittedDelta);
        Assert.False(results[1].IsFinal);

        // Chunk 3: continue appending the new suffix.
        Assert.Equal(" are", results[2].CommittedDelta);
        Assert.False(results[2].IsFinal);

        // Final: flush the remaining suffix.
        Assert.Equal(" you", results[3].CommittedDelta);
        Assert.True(results[3].IsFinal);
    }

    [Fact]
    public async Task LiveSession_StreamingMode_AdvancesThroughGrowingPartialWords()
    {
        var factory = new FakeLiveAudioSessionFactory();
        var engine = new TranscriptionEngine(new NullLogger<TranscriptionEngine>(), factory);
        SetModelId(engine, "whisper-test");

        await engine.StartSessionAsync(language: "en", streamingCommit: true);

        factory.Session.Emit(new LiveAudioSessionChunk(
            "the q", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.2)));
        factory.Session.Emit(new LiveAudioSessionChunk(
            "the qu", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.3)));
        factory.Session.Emit(new LiveAudioSessionChunk(
            "the quick b", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.4)));
        factory.Session.Emit(new LiveAudioSessionChunk(
            "the quick brown f", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.5)));
        factory.Session.Emit(new LiveAudioSessionChunk(
            "the quick brown fox", true, TimeSpan.Zero, TimeSpan.FromSeconds(0.6)));

        await engine.StopSessionAsync();
        var results = await CollectAsync(engine.GetResultStreamAsync());

        Assert.Equal(5, results.Count);

        Assert.Equal("the q", results[0].CommittedDelta);
        Assert.Equal(0, results[0].BackspaceCount);

        Assert.Equal("u", results[1].CommittedDelta);
        Assert.Equal(0, results[1].BackspaceCount);

        Assert.Equal("ick b", results[2].CommittedDelta);
        Assert.Equal(0, results[2].BackspaceCount);

        Assert.Equal("rown f", results[3].CommittedDelta);
        Assert.Equal(0, results[3].BackspaceCount);

        Assert.Equal("ox", results[4].CommittedDelta);
        Assert.Equal(0, results[4].BackspaceCount);
        Assert.True(results[4].IsFinal);
    }

    [Fact]
    public async Task LiveSession_StreamingMode_MergesRollingWindowChunks_WithoutErasingEarlierText()
    {
        var factory = new FakeLiveAudioSessionFactory();
        var engine = new TranscriptionEngine(new NullLogger<TranscriptionEngine>(), factory);
        SetModelId(engine, "whisper-test");

        await engine.StartSessionAsync(language: "en", streamingCommit: true);

        factory.Session.Emit(new LiveAudioSessionChunk(
            "hello world", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.2)));
        factory.Session.Emit(new LiveAudioSessionChunk(
            "world how are", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.4)));
        factory.Session.Emit(new LiveAudioSessionChunk(
            "how are you", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.6)));
        factory.Session.Emit(new LiveAudioSessionChunk(
            "hello world how are you today", true, TimeSpan.Zero, TimeSpan.FromSeconds(0.8)));

        await engine.StopSessionAsync();
        var results = await CollectAsync(engine.GetResultStreamAsync());

        Assert.Equal(4, results.Count);

        Assert.Equal("hello world", results[0].DisplayText);
        Assert.Equal("hello world", results[0].CommittedDelta);
        Assert.Equal(0, results[0].BackspaceCount);

        Assert.Equal("hello world how are", results[1].DisplayText);
        Assert.Equal(" how are", results[1].CommittedDelta);
        Assert.Equal(0, results[1].BackspaceCount);

        Assert.Equal("hello world how are you", results[2].DisplayText);
        Assert.Equal(" you", results[2].CommittedDelta);
        Assert.Equal(0, results[2].BackspaceCount);

        Assert.Equal("hello world how are you today", results[3].DisplayText);
        Assert.Equal(" today", results[3].CommittedDelta);
        Assert.Equal(0, results[3].BackspaceCount);
        Assert.True(results[3].IsFinal);
    }

    [Fact]
    public async Task LiveSession_StreamingMode_AppendsDetachedFollowOnWindows()
    {
        var factory = new FakeLiveAudioSessionFactory();
        var engine = new TranscriptionEngine(new NullLogger<TranscriptionEngine>(), factory);
        SetModelId(engine, "whisper-test");

        await engine.StartSessionAsync(language: "en", streamingCommit: true);

        factory.Session.Emit(new LiveAudioSessionChunk(
            "see you jason", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.8)));
        factory.Session.Emit(new LiveAudioSessionChunk(
            "have fun", false, TimeSpan.FromSeconds(0.82), TimeSpan.FromSeconds(1.2)));
        factory.Session.Emit(new LiveAudioSessionChunk(
            "we'll miss you", true, TimeSpan.FromSeconds(1.22), TimeSpan.FromSeconds(1.7)));

        await engine.StopSessionAsync();
        var results = await CollectAsync(engine.GetResultStreamAsync());

        Assert.Equal(3, results.Count);

        Assert.Equal("see you jason", results[0].DisplayText);
        Assert.Equal("see you jason", results[0].CommittedDelta);
        Assert.Equal(0, results[0].BackspaceCount);

        Assert.Equal("see you jason have fun", results[1].DisplayText);
        Assert.Equal(" have fun", results[1].CommittedDelta);
        Assert.Equal(0, results[1].BackspaceCount);

        Assert.Equal("see you jason have fun we'll miss you", results[2].DisplayText);
        Assert.Equal(" we'll miss you", results[2].CommittedDelta);
        Assert.Equal(0, results[2].BackspaceCount);
        Assert.True(results[2].IsFinal);
    }

    [Fact]
    public async Task LiveSession_FinalChunk_CorrectsEarlierCommit_WhenInterimRevisionOccurs()
    {
        var factory = new FakeLiveAudioSessionFactory();
        var engine = new TranscriptionEngine(new NullLogger<TranscriptionEngine>(), factory);
        SetModelId(engine, "whisper-test");

        await engine.StartSessionAsync(language: "en", streamingCommit: true);

        factory.Session.Emit(new LiveAudioSessionChunk(
            "hello world", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.2)));
        factory.Session.Emit(new LiveAudioSessionChunk(
            "yellow world again", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.4)));
        factory.Session.Emit(new LiveAudioSessionChunk(
            "yellow world again", true, TimeSpan.Zero, TimeSpan.FromSeconds(0.6)));

        await engine.StopSessionAsync();
        var results = await CollectAsync(engine.GetResultStreamAsync());

        Assert.Equal(3, results.Count);
        Assert.Equal("hello world", results[0].CommittedDelta);
        Assert.Equal(0, results[0].BackspaceCount);

        Assert.Equal("yellow world again", results[1].DisplayText);
        Assert.Equal(string.Empty, results[1].CommittedDelta);
        Assert.Equal(0, results[1].BackspaceCount);
        Assert.False(results[1].IsFinal);

        Assert.Equal("yellow world again", results[2].CommittedDelta);
        Assert.Equal("hello world".Length, results[2].BackspaceCount);
        Assert.True(results[2].IsFinal);
    }

    [Fact]
    public async Task LiveSession_FiltersNoiseTokens()
    {
        var factory = new FakeLiveAudioSessionFactory();
        var engine = new TranscriptionEngine(new NullLogger<TranscriptionEngine>(), factory);
        SetModelId(engine, "whisper-test");

        await engine.StartSessionAsync();
        factory.Session.Emit(new LiveAudioSessionChunk("[silence]", true, null, null));
        factory.Session.Emit(new LiveAudioSessionChunk("real words", true, null, null));
        await engine.StopSessionAsync();

        var results = await CollectAsync(engine.GetResultStreamAsync());

        var result = Assert.Single(results);
        Assert.Equal("real words", result.DisplayText);
        Assert.Equal("real words", result.CommittedDelta);
        Assert.True(result.IsFinal);
    }

    [Fact]
    public async Task StopSessionAsync_FlushesFinalChunkBeforeStreamCompletes()
    {
        var factory = new FakeLiveAudioSessionFactory
        {
            ConfigureStop = session =>
            {
                session.Emit(new LiveAudioSessionChunk("final words", true, null, null));
                return Task.CompletedTask;
            }
        };

        var engine = new TranscriptionEngine(new NullLogger<TranscriptionEngine>(), factory);
        SetModelId(engine, "whisper-test");

        await engine.StartSessionAsync(language: "en");
        await engine.StopSessionAsync();

        var results = await CollectAsync(engine.GetResultStreamAsync());

        var finalResult = Assert.Single(results);
        Assert.Equal("final words", finalResult.DisplayText);
        Assert.Equal("final words", finalResult.CommittedDelta);
        Assert.True(finalResult.IsFinal);
    }

    [Fact]
    public async Task AppendAudioAsync_ForwardsAudioToLiveSession()
    {
        var factory = new FakeLiveAudioSessionFactory();
        var engine = new TranscriptionEngine(new NullLogger<TranscriptionEngine>(), factory);
        SetModelId(engine, "whisper-test");

        await engine.StartSessionAsync();
        await engine.AppendAudioAsync(new byte[] { 1, 2, 3, 4 });
        await engine.StopSessionAsync();

        var appended = Assert.Single(factory.Session.AppendedAudio);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, appended);
    }

    private static async Task<List<TranscriptionResult>> CollectAsync(IAsyncEnumerable<TranscriptionResult> stream)
    {
        var items = new List<TranscriptionResult>();

        await foreach (var item in stream)
            items.Add(item);

        return items;
    }

    private static void SetModelId(TranscriptionEngine engine, string modelId)
    {
        var field = typeof(TranscriptionEngine).GetField("_modelId", BindingFlags.NonPublic | BindingFlags.Instance);
        field!.SetValue(engine, modelId);
    }
}

file sealed class FakeLiveAudioSessionFactory : ILiveAudioSessionFactory
{
    public FakeLiveAudioSession Session { get; } = new();

    public Func<FakeLiveAudioSession, Task>? ConfigureStop { get; init; }

    public ILiveAudioSession Create(string modelId, ILogger logger, Microsoft.AI.Foundry.Local.OpenAIAudioClient? audioClient = null)
    {
        Session.ModelId = modelId;
        Session.ConfigureStop = ConfigureStop;
        return Session;
    }
}

file sealed class FakeLiveAudioSession : ILiveAudioSession
{
    private readonly Channel<LiveAudioSessionChunk> _channel = Channel.CreateUnbounded<LiveAudioSessionChunk>();

    public string? ModelId { get; set; }
    public string? StartLanguage { get; private set; }
    public List<byte[]> AppendedAudio { get; } = new();
    public Func<FakeLiveAudioSession, Task>? ConfigureStop { get; set; }

    public Task StartAsync(int sampleRate, int channels, string? language, CancellationToken cancellationToken = default)
    {
        StartLanguage = language;
        return Task.CompletedTask;
    }

    public ValueTask AppendAsync(ReadOnlyMemory<byte> pcmData, CancellationToken cancellationToken = default)
    {
        AppendedAudio.Add(pcmData.ToArray());
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<LiveAudioSessionChunk> GetTranscriptionStreamAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (ConfigureStop is not null)
            await ConfigureStop(this);

        _channel.Writer.TryComplete();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Emit(LiveAudioSessionChunk chunk) => _channel.Writer.TryWrite(chunk);
}
