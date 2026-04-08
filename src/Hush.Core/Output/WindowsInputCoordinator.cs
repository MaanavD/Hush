// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using System.Threading;

namespace Hush.Core.Output;

/// <summary>
/// Coordinates Windows synthetic input between the text output path and the
/// push-to-talk hotkey release detector.
/// </summary>
public static class WindowsInputCoordinator
{
    public const nuint InjectedExtraInfo = 0x48555348;

    private static int _suppressedReleaseDetectionCount;

    public static bool IsHotkeyReleaseDetectionSuppressed
        => Volatile.Read(ref _suppressedReleaseDetectionCount) > 0;

    public static IDisposable SuppressHotkeyReleaseDetection()
    {
        Interlocked.Increment(ref _suppressedReleaseDetectionCount);
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            Interlocked.Decrement(ref _suppressedReleaseDetectionCount);
        }
    }
}