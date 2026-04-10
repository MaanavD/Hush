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

    [Theory]
    [InlineData("fr", "fr")]
    [InlineData("pt-BR", "pt")]
    [InlineData("zh_CN", "zh")]
    [InlineData("jp", "ja")]
    [InlineData("kr", "ko")]
    [InlineData("cn", "zh")]
    [InlineData("  ja-JP  ", "ja")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void NormalizeLanguageHint_UsesPrimarySubtag(string language, string? expected)
    {
        Assert.Equal(expected, ManagedLiveAudioSession.NormalizeLanguageHint(language));
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
                // Streaming mode now holds back the unstable tail word until it settles.
                Assert.Equal("bonjour", item.DisplayText);
                Assert.Equal(string.Empty, item.CommittedDelta);
                Assert.False(item.IsFinal);
            },
            item =>
            {
                // The matching final chunk commits the buffered word.
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

        // Chunk 1: the initial cumulative hypothesis commits all but the trailing word.
        Assert.Equal("hello", results[0].CommittedDelta);
        Assert.False(results[0].IsFinal);

        // Chunk 2: the held-back word settles and is typed.
        Assert.Equal(" world", results[1].CommittedDelta);
        Assert.False(results[1].IsFinal);

        // Chunk 3: continue releasing the previously buffered tail.
        Assert.Equal(" how", results[2].CommittedDelta);
        Assert.False(results[2].IsFinal);

        // Final: flush the remaining buffered suffix.
        Assert.Equal(" are you", results[3].CommittedDelta);
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

        Assert.Equal("the", results[0].CommittedDelta);
        Assert.Equal(0, results[0].BackspaceCount);

        Assert.Equal(string.Empty, results[1].CommittedDelta);
        Assert.Equal(0, results[1].BackspaceCount);

        Assert.Equal(" quick", results[2].CommittedDelta);
        Assert.Equal(0, results[2].BackspaceCount);

        Assert.Equal(" brown", results[3].CommittedDelta);
        Assert.Equal(0, results[3].BackspaceCount);

        Assert.Equal(" fox", results[4].CommittedDelta);
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
        Assert.Equal("hello", results[0].CommittedDelta);
        Assert.Equal(0, results[0].BackspaceCount);

        Assert.Equal("hello world how are", results[1].DisplayText);
        Assert.Equal(" world how", results[1].CommittedDelta);
        Assert.Equal(0, results[1].BackspaceCount);

        Assert.Equal("hello world how are you", results[2].DisplayText);
        Assert.Equal(" are", results[2].CommittedDelta);
        Assert.Equal(0, results[2].BackspaceCount);

        Assert.Equal("hello world how are you today", results[3].DisplayText);
        Assert.Equal(" you today", results[3].CommittedDelta);
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
        Assert.Equal("see you", results[0].CommittedDelta);
        Assert.Equal(0, results[0].BackspaceCount);

        Assert.Equal("see you jason have fun", results[1].DisplayText);
        Assert.Equal(" jason have", results[1].CommittedDelta);
        Assert.Equal(0, results[1].BackspaceCount);

        Assert.Equal("see you jason have fun we'll miss you", results[2].DisplayText);
        Assert.Equal(" fun we'll miss you", results[2].CommittedDelta);
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
        Assert.Equal("hello", results[0].CommittedDelta);
        Assert.Equal(0, results[0].BackspaceCount);

        Assert.Equal("yellow world again", results[1].DisplayText);
        Assert.Equal(string.Empty, results[1].CommittedDelta);
        Assert.Equal(0, results[1].BackspaceCount);
        Assert.False(results[1].IsFinal);

        Assert.Equal("yellow world again", results[2].CommittedDelta);
        Assert.Equal("hello".Length, results[2].BackspaceCount);
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
    public async Task LiveSession_FiltersRepetitionArtifacts_FromMiddleAndEnd()
    {
        var factory = new FakeLiveAudioSessionFactory();
        var engine = new TranscriptionEngine(new NullLogger<TranscriptionEngine>(), factory);
        SetModelId(engine, "whisper-test");

        await engine.StartSessionAsync(language: "en", streamingCommit: true);

        // Chunk 1: good text to establish baseline.
        factory.Session.Emit(new LiveAudioSessionChunk(
            "I feel", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.3)));

        // Chunk 2: model starts producing degenerate repeated chars.
        // "ee" and "oo" are 2-char repeated tokens; "ttttt" has 3+ consecutive.
        factory.Session.Emit(new LiveAudioSessionChunk(
            "I feel iike ee oo ttttt", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.6)));

        // Final chunk corrects things.
        factory.Session.Emit(new LiveAudioSessionChunk(
            "I feel like this is great", true, TimeSpan.Zero, TimeSpan.FromSeconds(1.0)));

        await engine.StopSessionAsync();
        var results = await CollectAsync(engine.GetResultStreamAsync());

        // The final committed text must not contain artifact tokens.
        var lastResult = results[^1];
        Assert.True(lastResult.IsFinal);
        Assert.Equal("I feel like this is great", lastResult.DisplayText);
        Assert.DoesNotContain("ee", lastResult.DisplayText.Split(' '));
        Assert.DoesNotContain("oo", lastResult.DisplayText.Split(' '));
        Assert.DoesNotContain("ttttt", lastResult.DisplayText.Split(' '));
    }

    [Fact]
    public async Task LiveSession_RejectsDegenerateChunks()
    {
        var factory = new FakeLiveAudioSessionFactory();
        var engine = new TranscriptionEngine(new NullLogger<TranscriptionEngine>(), factory);
        SetModelId(engine, "whisper-test");

        await engine.StartSessionAsync(language: "en", streamingCommit: true);

        factory.Session.Emit(new LiveAudioSessionChunk(
            "hello world", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.3)));

        // Entirely degenerate chunk — 10+ consecutive identical chars.
        factory.Session.Emit(new LiveAudioSessionChunk(
            "oooooooooooooooooo ttttttttttttt", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.6)));

        factory.Session.Emit(new LiveAudioSessionChunk(
            "hello world", true, TimeSpan.Zero, TimeSpan.FromSeconds(0.9)));

        await engine.StopSessionAsync();
        var results = await CollectAsync(engine.GetResultStreamAsync());

        // The degenerate chunk should be filtered; the final text is clean.
        var lastResult = results[^1];
        Assert.True(lastResult.IsFinal);
        Assert.Equal("hello world", lastResult.DisplayText);
    }

    [Fact]
    public async Task LiveSession_FlushFiltersArtifactsAtSessionEnd()
    {
        var factory = new FakeLiveAudioSessionFactory();
        var engine = new TranscriptionEngine(new NullLogger<TranscriptionEngine>(), factory);
        SetModelId(engine, "whisper-test");

        await engine.StartSessionAsync(language: "en", streamingCommit: true);

        // Good text followed by artifact words — session ends without a final chunk.
        factory.Session.Emit(new LiveAudioSessionChunk(
            "good morning ee oo", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.5)));

        await engine.StopSessionAsync();
        var results = await CollectAsync(engine.GetResultStreamAsync());

        // The flush should not include the artifact tokens.
        Assert.True(results.Count > 0);
        var allText = results[^1].DisplayText;
        Assert.DoesNotContain("ee", allText.Split(' '));
        Assert.DoesNotContain("oo", allText.Split(' '));
        Assert.Contains("good", allText);
        Assert.Contains("morning", allText);
    }

    // ── BuildFullText overlap regression tests ────────────────────────────────
    //
    // After a final chunk commits text, _segmentBase is set to that committed
    // text. If Nemotron's very next rolling window starts from mid-committed
    // text (e.g. the last word of the committed sentence), BuildFullText must
    // splice at the word boundary rather than blindly concatenate, otherwise
    // the overlapping tail is typed twice into the target application.

    [Fact]
    public async Task LiveSession_AfterFinalChunk_RollingWindowOverlapWithBase_DoesNotDuplicateWords()
    {
        var factory = new FakeLiveAudioSessionFactory();
        var engine = new TranscriptionEngine(new NullLogger<TranscriptionEngine>(), factory);
        SetModelId(engine, "whisper-test");

        await engine.StartSessionAsync(language: "en", streamingCommit: true);

        // First sentence committed via a final chunk.
        factory.Session.Emit(new LiveAudioSessionChunk(
            "hello world", true, TimeSpan.Zero, TimeSpan.FromSeconds(1.0)));

        // Nemotron's next rolling window starts from the last committed word
        // ("world") — the classic overlap scenario that triggers the bug.
        factory.Session.Emit(new LiveAudioSessionChunk(
            "world how are you", false, TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(1.5)));

        factory.Session.Emit(new LiveAudioSessionChunk(
            "world how are you today", true, TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(2.0)));

        await engine.StopSessionAsync();
        var results = await CollectAsync(engine.GetResultStreamAsync());

        // No committed delta must contain the duplicate word "world".
        var allDeltas = string.Concat(results.Select(r => r.CommittedDelta));
        Assert.DoesNotContain("world world", allDeltas);

        // Final display text must be clean.
        var lastResult = results[^1];
        Assert.True(lastResult.IsFinal);
        Assert.Equal("hello world how are you today", lastResult.DisplayText);
    }

    [Fact]
    public async Task LiveSession_StreamingCommit_IntermediateDeltasNeverContainArtifacts()
    {
        var factory = new FakeLiveAudioSessionFactory();
        var engine = new TranscriptionEngine(new NullLogger<TranscriptionEngine>(), factory);
        SetModelId(engine, "whisper-test");

        await engine.StartSessionAsync(language: "en", streamingCommit: true);

        // Progressive rolling windows that mimic Nemotron's behaviour: each
        // window extends the current hypothesis by one or two words.
        factory.Session.Emit(new LiveAudioSessionChunk(
            "is it gonna work", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.4)));

        factory.Session.Emit(new LiveAudioSessionChunk(
            "is it gonna work now or no", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.7)));

        // A window that suddenly carries degenerate tokens alongside real words.
        factory.Session.Emit(new LiveAudioSessionChunk(
            "is it gonna work now or no s but yyyyyyyyyy eeee oooooo",
            false, TimeSpan.Zero, TimeSpan.FromSeconds(1.0)));

        factory.Session.Emit(new LiveAudioSessionChunk(
            "is it gonna work now or no s but sometimes it doesn't",
            true, TimeSpan.Zero, TimeSpan.FromSeconds(1.5)));

        await engine.StopSessionAsync();
        var results = await CollectAsync(engine.GetResultStreamAsync());

        // No individual committed delta may contain any artifact token.
        var artifactPatterns = new[] { "yyyyyyyyyy", "eeee", "oooooo", "ee", "oo" };
        foreach (var result in results)
        {
            var deltaWords = result.CommittedDelta
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var artifact in artifactPatterns)
                Assert.DoesNotContain(artifact, deltaWords);
        }

        // Final text is clean.
        var last = results[^1];
        Assert.True(last.IsFinal);
        Assert.DoesNotContain("yyyyyyyyyy", last.DisplayText);
        Assert.DoesNotContain("eeee", last.DisplayText);
    }

    [Fact]
    public async Task LiveSession_FinalChunk_SameWordsWithCapitalisationAndPunctuation_DoesNotBackspace()
    {
        // Regression test: when the final chunk adds punctuation/capitalisation to words
        // already committed, the word-level diff should avoid erasing and retyping them.
        var factory = new FakeLiveAudioSessionFactory();
        var engine = new TranscriptionEngine(new NullLogger<TranscriptionEngine>(), factory);
        SetModelId(engine, "whisper-test");

        await engine.StartSessionAsync(language: "en", streamingCommit: true);

        // Progressive chunks — engine commits each stable word in turn.
        factory.Session.Emit(new LiveAudioSessionChunk(
            "hello world", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.3)));
        factory.Session.Emit(new LiveAudioSessionChunk(
            "hello world how", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.5)));
        factory.Session.Emit(new LiveAudioSessionChunk(
            "hello world how are", false, TimeSpan.Zero, TimeSpan.FromSeconds(0.7)));

        // Final chunk: same words but now capitalised and punctuated, plus a new word.
        factory.Session.Emit(new LiveAudioSessionChunk(
            "Hello, world! How are you.", true, TimeSpan.Zero, TimeSpan.FromSeconds(1.0)));

        await engine.StopSessionAsync();
        var results = await CollectAsync(engine.GetResultStreamAsync());

        // The final result must not backspace the already-correct words.
        var finalResult = results[^1];
        Assert.True(finalResult.IsFinal);
        Assert.Equal(0, finalResult.BackspaceCount);
        // Only the new word (and its punctuation suffix) should be typed.
        Assert.Equal(" are you.", finalResult.CommittedDelta);
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
