// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

namespace Hush.Core.Configuration;

/// <summary>
/// Persisted user preferences for Hush.
/// Serialised to/from <c>~/.hush/settings.json</c>.
/// </summary>
public sealed class HushSettings
{
    /// <summary>Global hotkey combination, e.g. <c>"Ctrl+Shift+H"</c>.</summary>
    public string Hotkey { get; set; } = "Ctrl+H";

/// <summary>Language hint passed to Foundry Local. Accepts ISO 639-1 codes like <c>"en"</c> and locale tags like <c>"en-US"</c>.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Foundry Local model alias to use for transcription.</summary>
    public string TranscriptionModel { get; set; } = "whisper-tiny";

    /// <summary>Whether to show unstable interim transcription text in the overlay.</summary>
    public bool PartialsInOverlay { get; set; } = true;

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

    /// <summary>
    /// When <see langword="true"/>, text is committed to the target application
    /// as words stabilise during dictation (progressive streaming). When
    /// <see langword="false"/>, all text is committed only after the hotkey is
    /// released (batch mode). Default is <see langword="true"/>.
    /// </summary>
    public bool StreamingCommit { get; set; } = true;

    /// <summary>Whether Hush should launch at OS login.</summary>
    public bool AutoStart { get; set; } = false;

    /// <summary>
    /// Audio input device index. <c>-1</c> means system default.
    /// </summary>
    public int MicrophoneDeviceIndex { get; set; } = -1;

    // ── Clean Mode ────────────────────────────────────────────────────────
    /// <summary>Hotkey combination for clean-mode (LLM-rewritten) dictation.</summary>
    public string CleanHotkey { get; set; } = "Alt+H";

    /// <summary>Whether to run an LLM rewrite pass in clean-mode sessions.</summary>
    public bool PostProcessingEnabled { get; set; } = true;

    /// <summary>Foundry Local model alias used for LLM rewriting.</summary>
    public string PostProcessingModel { get; set; } = "qwen3-0.6b";

    /// <summary>ID of the active post-processing prompt template.</summary>
    public string? ActivePostProcessingPromptId { get; set; }

    /// <summary>User-defined prompt templates for LLM rewriting.</summary>
    public List<HushPromptEntry> PostProcessingPrompts { get; set; } = new();

    // ── Dictionary ────────────────────────────────────────────────────────
    /// <summary>Custom word substitution rules applied after transcription.</summary>
    public List<HushSubstitutionEntry> CustomSubstitutions { get; set; } = new();

    // ── Behavior additions ────────────────────────────────────────────────
    /// <summary>Display-string key sent to target app after dictation ends (e.g. "None", "Enter", "Ctrl+Enter").</summary>
    public string AutoSubmitKey { get; set; } = "None";

    /// <summary>Display-string for model unload idle timeout (e.g. "After 5 min").</summary>
    public string ModelUnloadTimeout { get; set; } = "After 5 min";
}

/// <summary>Persisted user-defined prompt template entry.</summary>
public sealed record HushPromptEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Prompt { get; init; } = "";
}

/// <summary>Persisted word-substitution rule.</summary>
public sealed record HushSubstitutionEntry
{
    public string Match { get; init; } = "";
    public string Replace { get; init; } = "";
}
