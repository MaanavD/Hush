using System.Runtime.Versioning;
using Hush.Core.Configuration;
using Hush.Core.Output;

namespace Hush.E2E.Tests;

[SupportedOSPlatform("windows")]
public sealed class NotepadTargetedTextOutputServiceTests
{
    [Fact]
    public async Task StandardTypingAndBackspaceCorrectionsUseAtomicEditorBufferUpdates()
    {
        var notepad = new FakeNotepadEditorTextSink();
        var inner = new RecordingTextOutputService();
        var output = new NotepadTargetedTextOutputService(notepad, inner);

        await output.TypeTextAsync("hello wurld");
        await output.SendBackspacesAsync(5);
        await output.TypeTextAsync("world");

        Assert.Equal("hello world", notepad.Text);
        Assert.Equal(
            new[] { "hello wurld", "hello ", "hello world" },
            notepad.BestEffortWrites);
        Assert.Empty(inner.TypedTexts);
        Assert.Empty(inner.BackspaceCounts);
        Assert.Equal(0, notepad.FocusCount);
    }

    [Fact]
    public async Task StandardTypingFallsBackToInnerOutputWhenBestEffortUpdateFails()
    {
        var notepad = new FakeNotepadEditorTextSink(bestEffortResults: new[] { false, true });
        var inner = new RecordingTextOutputService();
        var output = new NotepadTargetedTextOutputService(notepad, inner);

        await output.TypeTextAsync("hello");
        await output.TypeTextAsync(" world");

        Assert.Equal("hello world", notepad.Text);
        Assert.Equal(new[] { "hello" }, inner.TypedTexts);
        Assert.Equal(new[] { "hello", "hello world" }, notepad.BestEffortWrites);
        Assert.Equal(1, notepad.FocusCount);
    }

    [Fact]
    public async Task FinalFullBufferReplacementUsesVerifiedSetEditorTextPath()
    {
        var notepad = new FakeNotepadEditorTextSink();
        var inner = new RecordingTextOutputService();
        var output = new NotepadTargetedTextOutputService(notepad, inner);

        await output.ReplaceTextAsync(
            backspaceCount: 0,
            replacementText: "final clean text",
            boundToCurrentLine: true,
            allowFullBufferReplacement: true,
            replacementKind: TextReplacementKind.FinalSynchronization);

        Assert.Equal("final clean text", notepad.Text);
        Assert.Equal(new[] { "final clean text" }, notepad.VerifiedWrites);
        Assert.Empty(notepad.BestEffortWrites);
        Assert.Empty(inner.Replacements);
        Assert.Equal(1, notepad.FocusCount);
    }

    private sealed class FakeNotepadEditorTextSink : INotepadEditorTextSink
    {
        private readonly Queue<bool> _bestEffortResults;

        public FakeNotepadEditorTextSink(IEnumerable<bool>? bestEffortResults = null)
            => _bestEffortResults = new Queue<bool>(bestEffortResults ?? []);

        public string Text { get; private set; } = string.Empty;

        public int FocusCount { get; private set; }

        public List<string> BestEffortWrites { get; } = new();

        public List<string> VerifiedWrites { get; } = new();

        public void Focus()
            => FocusCount++;

        public Task<bool> TrySetEditorTextBestEffortAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BestEffortWrites.Add(text);
            var succeeded = _bestEffortResults.Count == 0 || _bestEffortResults.Dequeue();
            if (succeeded)
                Text = text;
            return Task.FromResult(succeeded);
        }

        public Task SetEditorTextAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifiedWrites.Add(text);
            Text = text;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingTextOutputService : ITextOutputService
    {
        public List<string> TypedTexts { get; } = new();

        public List<int> BackspaceCounts { get; } = new();

        public List<string> Replacements { get; } = new();

        public Task TypeTextAsync(
            string text,
            CancellationToken cancellationToken = default,
            bool skipModifierRestore = false)
        {
            TypedTexts.Add(text);
            return Task.CompletedTask;
        }

        public Task SendBackspacesAsync(
            int count,
            CancellationToken cancellationToken = default,
            bool skipModifierRestore = false)
        {
            BackspaceCounts.Add(count);
            return Task.CompletedTask;
        }

        public Task ReplaceTextAsync(
            int backspaceCount,
            string replacementText,
            CancellationToken cancellationToken = default,
            bool skipModifierRestore = false,
            bool boundToCurrentLine = false,
            string? expectedExistingText = null,
            bool allowFullBufferReplacement = false,
            TextReplacementKind replacementKind = TextReplacementKind.FinalSynchronization)
        {
            Replacements.Add(replacementText);
            return Task.CompletedTask;
        }

        public Task SendKeyAsync(AutoSubmitKey key, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
