namespace Hush.E2E.Tests;

internal sealed class AudioE2EFactAttribute : FactAttribute
{
    public AudioE2EFactAttribute()
    {
        if (!AudioE2EOptions.RunAudioE2E)
            Skip = "Set HUSH_RUN_AUDIO_E2E=1 to run model-backed audio E2E tests.";
    }
}
