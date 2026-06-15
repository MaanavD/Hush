using System.Text;

namespace Hush.E2E.Tests;

internal static class NotepadPersistedStateCleanup
{
    public const string HushTestFileNamePrefix = "hush-notepad-e2e-";

    private static readonly byte[] Utf8HushMarker = Encoding.UTF8.GetBytes(HushTestFileNamePrefix);
    private static readonly byte[] Utf16HushMarker = Encoding.Unicode.GetBytes(HushTestFileNamePrefix);

    public static void DeleteHushOwnedPersistedState()
    {
        foreach (var localStatePath in GetNotepadLocalStatePaths())
            DeleteHushOwnedPersistedState(localStatePath);
    }

    internal static void DeleteHushOwnedPersistedState(string localStatePath)
    {
        if (string.IsNullOrWhiteSpace(localStatePath) || !Directory.Exists(localStatePath))
            return;

        var tabStatePath = Path.Combine(localStatePath, "TabState");
        var windowStatePath = Path.Combine(localStatePath, "WindowState");
        var tabOwnershipById = new Dictionary<Guid, bool>();

        foreach (var tabStateFile in EnumerateFilesSafe(tabStatePath))
        {
            if (!TryGetGuidFromTabStateFileName(tabStateFile, out var tabId))
                continue;

            var isHushOwned = TryReadAllBytes(tabStateFile, out var bytes) && ContainsHushMarker(bytes);
            tabOwnershipById[tabId] = isHushOwned;
            if (isHushOwned)
                TryDeleteFile(tabStateFile);
        }

        if (tabOwnershipById.Count == 0)
            return;

        foreach (var windowStateFile in EnumerateFilesSafe(windowStatePath))
        {
            if (!TryReadAllBytes(windowStateFile, out var bytes))
                continue;

            var referencedTabs = FindKnownReferencedTabs(bytes, tabOwnershipById.Keys).ToArray();
            var referencesHushTab = referencedTabs.Any(tabId => tabOwnershipById[tabId]);
            if (!referencesHushTab)
                continue;

            if (referencedTabs.All(tabId => tabOwnershipById[tabId]))
                TryDeleteFile(windowStateFile);
        }
    }

    private static IEnumerable<string> GetNotepadLocalStatePaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            return [];

        var packagesPath = Path.Combine(localAppData, "Packages");
        if (!Directory.Exists(packagesPath))
            return [];

        try
        {
            return Directory
                .EnumerateDirectories(packagesPath, "Microsoft.WindowsNotepad_*", SearchOption.TopDirectoryOnly)
                .Select(path => Path.Combine(path, "LocalState"))
                .Where(Directory.Exists)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateFiles(path).ToArray()
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static bool TryReadAllBytes(string filePath, out byte[] bytes)
    {
        try
        {
            bytes = File.ReadAllBytes(filePath);
            return true;
        }
        catch
        {
            bytes = [];
            return false;
        }
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch
        {
            // Best-effort cleanup of Notepad state entries owned by Hush tests.
        }
    }

    private static bool TryGetGuidFromTabStateFileName(string tabStateFile, out Guid tabId)
    {
        var fileName = Path.GetFileNameWithoutExtension(tabStateFile);
        return Guid.TryParse(fileName, out tabId);
    }

    private static bool ContainsHushMarker(byte[] bytes) =>
        ContainsSequence(bytes, Utf8HushMarker) ||
        ContainsSequence(bytes, Utf16HushMarker);

    private static IEnumerable<Guid> FindKnownReferencedTabs(byte[] bytes, IEnumerable<Guid> knownTabIds)
    {
        foreach (var tabId in knownTabIds)
        {
            if (ContainsSequence(bytes, tabId.ToByteArray()))
                yield return tabId;
        }
    }

    private static bool ContainsSequence(byte[] bytes, byte[] sequence)
    {
        if (sequence.Length == 0 || bytes.Length < sequence.Length)
            return false;

        for (var i = 0; i <= bytes.Length - sequence.Length; i++)
        {
            var matches = true;
            for (var j = 0; j < sequence.Length; j++)
            {
                if (bytes[i + j] != sequence[j])
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
                return true;
        }

        return false;
    }
}
