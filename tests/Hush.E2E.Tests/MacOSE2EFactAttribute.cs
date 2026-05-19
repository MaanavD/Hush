namespace Hush.E2E.Tests;

internal sealed class MacOSE2EFactAttribute : FactAttribute
{
    public MacOSE2EFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "macOS TextEdit E2E tests require macOS.";
            return;
        }

        if (!MacOSE2EOptions.RunMacOSE2E)
        {
            Skip = "Set HUSH_RUN_MACOS_E2E=1 to run macOS TextEdit E2E tests.";
            return;
        }

    }
}
