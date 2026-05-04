using System.Text;

namespace Hush.E2E.Tests;

public sealed class NotepadPersistedStateCleanupTests
{
    [Fact]
    public void DeleteHushOwnedPersistedStateDeletesOnlyHushTabStateFiles()
    {
        var localStatePath = CreateLocalStatePath();
        try
        {
            var tabStatePath = Directory.CreateDirectory(Path.Combine(localStatePath, "TabState")).FullName;
            var hushTabStateFile = WriteTabStateFile(tabStatePath, Guid.NewGuid(), @"C:\Temp\hush-notepad-e2e-owned.txt");
            var userTabStateFile = WriteTabStateFile(tabStatePath, Guid.NewGuid(), @"C:\Users\person\notes.txt");

            NotepadPersistedStateCleanup.DeleteHushOwnedPersistedState(localStatePath);

            Assert.False(File.Exists(hushTabStateFile));
            Assert.True(File.Exists(userTabStateFile));
        }
        finally
        {
            DeleteDirectory(localStatePath);
        }
    }

    [Fact]
    public void DeleteHushOwnedPersistedStateDeletesOnlyHushOwnedWindowStateFiles()
    {
        var localStatePath = CreateLocalStatePath();
        try
        {
            var tabStatePath = Directory.CreateDirectory(Path.Combine(localStatePath, "TabState")).FullName;
            var windowStatePath = Directory.CreateDirectory(Path.Combine(localStatePath, "WindowState")).FullName;
            var hushTabId = Guid.NewGuid();
            var secondHushTabId = Guid.NewGuid();
            var userTabId = Guid.NewGuid();
            WriteTabStateFile(tabStatePath, hushTabId, @"C:\Temp\hush-notepad-e2e-owned.txt");
            WriteTabStateFile(tabStatePath, secondHushTabId, @"C:\Temp\hush-notepad-e2e-second.txt");
            WriteTabStateFile(tabStatePath, userTabId, @"C:\Users\person\notes.txt");
            var hushOnlyWindowStateFile = WriteWindowStateFile(windowStatePath, hushTabId, secondHushTabId);
            var mixedWindowStateFile = WriteWindowStateFile(windowStatePath, hushTabId, userTabId);
            var userOnlyWindowStateFile = WriteWindowStateFile(windowStatePath, userTabId);

            NotepadPersistedStateCleanup.DeleteHushOwnedPersistedState(localStatePath);

            Assert.False(File.Exists(hushOnlyWindowStateFile));
            Assert.True(File.Exists(mixedWindowStateFile));
            Assert.True(File.Exists(userOnlyWindowStateFile));
        }
        finally
        {
            DeleteDirectory(localStatePath);
        }
    }

    private static string CreateLocalStatePath()
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            "NotepadPersistedStateCleanupTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteTabStateFile(string tabStatePath, Guid tabId, string referencedPath)
    {
        var filePath = Path.Combine(tabStatePath, $"{tabId}.bin");
        File.WriteAllBytes(filePath, Encoding.Unicode.GetBytes(referencedPath));
        return filePath;
    }

    private static string WriteWindowStateFile(string windowStatePath, params Guid[] tabIds)
    {
        var filePath = Path.Combine(windowStatePath, $"{Guid.NewGuid()}.0.bin");
        using var stream = File.OpenWrite(filePath);
        stream.Write([0x48, 0x55, 0x53, 0x48]);
        foreach (var tabId in tabIds)
        {
            stream.Write(tabId.ToByteArray());
            stream.WriteByte(0);
        }

        return filePath;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Test cleanup best effort.
        }
    }
}
