// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.Core.PostProcessing;

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

    /// <summary>Whether to run an LLM cleanup pass on the accumulated transcript in spinner mode.</summary>
    public bool PostProcessingEnabled { get; set; } = true;

    /// <summary>Foundry Local model alias used for post-processing.</summary>
    public string PostProcessingModel { get; set; } = "qwen3-0.6b";

    /// <summary>User-defined prompts added to the built-in set.</summary>
    public List<LlmPrompt> PostProcessingPrompts { get; set; } = new();

    /// <summary>
    /// Id of the active <see cref="LlmPrompt"/>. <see langword="null"/> falls back
    /// to <see cref="BuiltInPrompts"/>[0].
    /// </summary>
    public string? ActivePostProcessingPromptId { get; set; }

    public static IReadOnlyList<LlmPrompt> BuiltInPrompts { get; } = new LlmPrompt[]
    {
        new() { Id = "fix-punctuation", Name = "Fix punctuation & grammar",
                Prompt = "Fix punctuation, capitalization, and grammar. Return only the corrected text. Do not add commentary.", IsBuiltIn = true },
        new() { Id = "formal-prose", Name = "Rewrite as formal prose",
                Prompt = "Rewrite the text as formal, professional prose. Return only the result. Do not add commentary.", IsBuiltIn = true },
        new() { Id = "bullet-list", Name = "Convert to bullet list",
                Prompt = "Convert the text into a concise bullet list, one idea per item. Return only the list. Do not add commentary.", IsBuiltIn = true },
    };

    /// <summary>All prompts: built-ins first, then user-defined.</summary>
    public IReadOnlyList<LlmPrompt> AllPrompts =>
        BuiltInPrompts.Concat(PostProcessingPrompts).ToList();

    /// <summary>
    /// Returns the active prompt, or <see cref="BuiltInPrompts"/>[0] as fallback
    /// when <see cref="ActivePostProcessingPromptId"/> is null or unrecognised.
    /// </summary>
    public LlmPrompt GetActivePrompt() =>
        AllPrompts.FirstOrDefault(p => p.Id == ActivePostProcessingPromptId)
        ?? BuiltInPrompts[0];
}
