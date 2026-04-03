namespace Hush.Core.Configuration;

/// <summary>
/// Persisted user preferences for Hush.
/// Serialised to/from <c>~/.hush/settings.json</c>.
/// </summary>
public sealed class HushSettings
{
    /// <summary>Global hotkey combination, e.g. <c>"Ctrl+Shift+H"</c>.</summary>
    public string Hotkey { get; set; } = "Ctrl+Shift+H";

    /// <summary>BCP-47 language tag used for transcription, e.g. <c>"en"</c>.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Foundry Local model alias to use for transcription.</summary>
    public string TranscriptionModel { get; set; } = "whisper-tiny";

    /// <summary>Whether to show unstable interim transcription text in the overlay.</summary>
    public bool PartialsInOverlay { get; set; } = true;

    /// <summary>Whether to type only committed (stable) text into the target app (MVP default).</summary>
    public bool TypeCommittedTextOnly { get; set; } = true;

    /// <summary>Overlay screen position hint.</summary>
    public string OverlayPosition { get; set; } = "bottom-center";

    /// <summary>Overlay background opacity (0 = fully transparent, 1 = opaque).</summary>
    public double OverlayOpacity { get; set; } = 0.85;

    /// <summary>Whether to play start/stop sound effects.</summary>
    public bool SoundEffects { get; set; } = true;

    /// <summary>
    /// Whether to fall back to clipboard paste when keystroke injection is blocked.
    /// Disabled by default to preserve clipboard contents.
    /// </summary>
    public bool ClipboardFallback { get; set; } = false;

    /// <summary>Whether Hush should launch at OS login.</summary>
    public bool AutoStart { get; set; } = false;

    /// <summary>UI colour theme: <c>"system"</c>, <c>"light"</c>, or <c>"dark"</c>.</summary>
    public string Theme { get; set; } = "system";
}
