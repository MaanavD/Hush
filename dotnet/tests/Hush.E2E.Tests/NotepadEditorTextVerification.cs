namespace Hush.E2E.Tests;

internal static class NotepadEditorTextVerification
{
    public static bool Matches(string actual, string expected) =>
        string.Equals(NormalizeLineEndings(actual), NormalizeLineEndings(expected), StringComparison.Ordinal);

    public static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
