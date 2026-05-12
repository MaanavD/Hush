using System.Runtime.Versioning;
using Hush.Core.Configuration;
using Hush.Core.Output;

namespace Hush.E2E.Tests;

[SupportedOSPlatform("macos")]
internal sealed class TextEditTargetedTextOutputService : IFullBufferFinalReplacementOutputService, IPreviewTextReplacementOutputService
{
    private readonly TextEditTestApp _textEdit;
    private string _editorText = string.Empty;

    public TextEditTargetedTextOutputService(TextEditTestApp textEdit)
        => _textEdit = textEdit;

    public bool PreferFullBufferFinalReplacement => true;

    public Task TypeTextAsync(
        string text,
        CancellationToken cancellationToken = default,
        bool skipModifierRestore = false)
    {
        if (string.IsNullOrEmpty(text))
            return Task.CompletedTask;

        return SetEditorTextAsync(_editorText + text, cancellationToken);
    }

    public Task TypePreviewTextAsync(
        string text,
        CancellationToken cancellationToken = default,
        bool skipModifierRestore = false)
        => TypeTextAsync(text, cancellationToken, skipModifierRestore);

    public Task ReplacePreviewTextAsync(
        string currentText,
        string targetText,
        CancellationToken cancellationToken = default,
        bool skipModifierRestore = false)
        => SetEditorTextAsync(targetText, cancellationToken);

    public Task SendBackspacesAsync(
        int count,
        CancellationToken cancellationToken = default,
        bool skipModifierRestore = false)
    {
        if (count <= 0)
            return Task.CompletedTask;

        var targetText = _editorText[..Math.Max(0, _editorText.Length - count)];
        return SetEditorTextAsync(targetText, cancellationToken);
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
        var targetText = replacementKind == TextReplacementKind.FinalSynchronization
            && (allowFullBufferReplacement || boundToCurrentLine)
                ? replacementText
                : _editorText[..Math.Max(0, _editorText.Length - Math.Max(0, backspaceCount))] + replacementText;

        return SetEditorTextAsync(targetText, cancellationToken);
    }

    public Task SendKeyAsync(AutoSubmitKey key, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    private async Task SetEditorTextAsync(string text, CancellationToken cancellationToken)
    {
        await _textEdit.SetTextAsync(text, cancellationToken);
        _editorText = text;
    }
}

