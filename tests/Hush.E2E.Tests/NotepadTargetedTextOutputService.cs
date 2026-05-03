using System.Runtime.Versioning;
using Hush.Core.Configuration;
using Hush.Core.Output;

namespace Hush.E2E.Tests;

internal interface INotepadEditorTextSink
{
    void Focus();

    Task<bool> TrySetEditorTextBestEffortAsync(string text, CancellationToken cancellationToken);

    Task SetEditorTextAsync(string text, CancellationToken cancellationToken);
}

[SupportedOSPlatform("windows")]
internal sealed class NotepadTargetedTextOutputService : IFullBufferFinalReplacementOutputService, IPreviewTextReplacementOutputService
{
    private readonly INotepadEditorTextSink _notepad;
    private readonly ITextOutputService _inner;
    private string _editorText = string.Empty;

    public NotepadTargetedTextOutputService(INotepadEditorTextSink notepad, ITextOutputService inner)
    {
        _notepad = notepad;
        _inner = inner;
    }

    public bool PreferFullBufferFinalReplacement => true;

    public async Task TypeTextAsync(
        string text,
        CancellationToken cancellationToken = default,
        bool skipModifierRestore = false)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var targetText = _editorText + text;
        if (await TryApplyEditorTextBestEffortAsync(targetText, cancellationToken))
            return;

        _notepad.Focus();
        await _inner.TypeTextAsync(text, cancellationToken, skipModifierRestore);
        _editorText = targetText;
    }

    public async Task TypePreviewTextAsync(
        string text,
        CancellationToken cancellationToken = default,
        bool skipModifierRestore = false)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var targetText = _editorText + text;
        if (await TryApplyEditorTextBestEffortAsync(targetText, cancellationToken))
            return;

        _notepad.Focus();
        if (_inner is IPreviewTextOutputService previewOutput)
        {
            await previewOutput.TypePreviewTextAsync(text, cancellationToken, skipModifierRestore);
            _editorText = targetText;
            return;
        }

        await _inner.TypeTextAsync(text, cancellationToken, skipModifierRestore);
        _editorText = targetText;
    }

    public async Task ReplacePreviewTextAsync(
        string currentText,
        string targetText,
        CancellationToken cancellationToken = default,
        bool skipModifierRestore = false)
    {
        if (await TryApplyEditorTextBestEffortAsync(targetText, cancellationToken))
            return;

        int commonPrefixLength = CommonPrefixLength(currentText, targetText);
        int backspaceCount = currentText.Length - commonPrefixLength;
        var delta = targetText[commonPrefixLength..];

        _notepad.Focus();
        await _inner.ReplaceTextAsync(
            backspaceCount,
            delta,
            cancellationToken,
            skipModifierRestore,
            replacementKind: TextReplacementKind.Preview);
        _editorText = targetText;
    }

    public async Task SendBackspacesAsync(
        int count,
        CancellationToken cancellationToken = default,
        bool skipModifierRestore = false)
    {
        if (count <= 0)
            return;

        var targetText = _editorText[..Math.Max(0, _editorText.Length - count)];
        if (await TryApplyEditorTextBestEffortAsync(targetText, cancellationToken))
            return;

        _notepad.Focus();
        await _inner.SendBackspacesAsync(count, cancellationToken, skipModifierRestore);
        _editorText = targetText;
    }

    public async Task ReplaceTextAsync(
        int backspaceCount,
        string replacementText,
        CancellationToken cancellationToken = default,
        bool skipModifierRestore = false,
        bool boundToCurrentLine = false,
        string? expectedExistingText = null,
        bool allowFullBufferReplacement = false,
        TextReplacementKind replacementKind = TextReplacementKind.FinalSynchronization)
    {
        if (replacementKind == TextReplacementKind.FinalSynchronization
            && (allowFullBufferReplacement || boundToCurrentLine))
        {
            _notepad.Focus();
            await _notepad.SetEditorTextAsync(replacementText, cancellationToken);
            _editorText = replacementText;
            return;
        }

        if (replacementKind == TextReplacementKind.Preview)
        {
            var previewTarget = _editorText[..Math.Max(0, _editorText.Length - Math.Max(0, backspaceCount))] + replacementText;
            if (await TryApplyEditorTextBestEffortAsync(previewTarget, cancellationToken))
                return;
        }

        _notepad.Focus();
        await _inner.ReplaceTextAsync(
            backspaceCount,
            replacementText,
            cancellationToken,
            skipModifierRestore,
            boundToCurrentLine,
            expectedExistingText,
            allowFullBufferReplacement,
            replacementKind);
        if (replacementKind == TextReplacementKind.Preview)
            _editorText = _editorText[..Math.Max(0, _editorText.Length - Math.Max(0, backspaceCount))] + replacementText;
        else
            _editorText = _editorText[..Math.Max(0, _editorText.Length - Math.Max(0, backspaceCount))] + replacementText;
    }

    private async Task<bool> TryApplyEditorTextBestEffortAsync(
        string targetText,
        CancellationToken cancellationToken)
    {
        if (await _notepad.TrySetEditorTextBestEffortAsync(targetText, cancellationToken))
        {
            _editorText = targetText;
            return true;
        }

        return false;
    }

    private static int CommonPrefixLength(string left, string right)
    {
        int length = Math.Min(left.Length, right.Length);
        int index = 0;
        while (index < length && left[index] == right[index])
            index++;
        return index;
    }

    public Task SendKeyAsync(AutoSubmitKey key, CancellationToken cancellationToken = default)
    {
        _notepad.Focus();
        return _inner.SendKeyAsync(key, cancellationToken);
    }
}
