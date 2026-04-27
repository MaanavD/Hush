// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

using Hush.Core.PostProcessing;

namespace Hush.Core.Configuration;

public enum AutoSubmitKey { None, Enter, CtrlEnter }

public enum ModelUnloadTimeout { Never, Min2, Min5, Min15 }

/// <summary>
/// Persisted user preferences for Hush.
/// Serialised to/from <c>~/.hush/settings.json</c>.
/// </summary>
public sealed class HushSettings
{
    public const string DefaultTranscriptionModel = "nemotron-speech-streaming-en-0.6b-generic-cpu";

    /// <summary>
    /// Global push-to-talk hotkey. Supports combos like <c>"Ctrl+Shift+H"</c>
    /// as well as modifier-only gestures such as plain <c>"Alt"</c> — in
    /// that case dictation begins after a brief hold (~250 ms) so ordinary
    /// Alt+Tab / Alt+F4 / menu-access interactions are unaffected.
    /// Default is <c>"Ctrl+H"</c> on every platform.
    /// </summary>
    public string Hotkey { get; set; } = DefaultRawHotkey();

    public static string DefaultRawHotkey() => "Ctrl+H";

    /// <summary>Language hint passed to Foundry Local. Accepts ISO 639-1 codes like <c>"en"</c> and locale tags like <c>"en-US"</c>.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Foundry Local model alias to use for transcription.</summary>
    public string TranscriptionModel { get; set; } = DefaultTranscriptionModel;

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

    /// <summary>Audio input device index. <c>-1</c> means system default.</summary>
    public int MicrophoneDeviceIndex { get; set; } = -1;

    /// <summary>User-defined word substitutions applied to committed dictation output.</summary>
    public List<TextSubstitution> CustomSubstitutions { get; set; } = new();

    /// <summary>Key combination sent to the focused app after dictation ends. Default is None.</summary>
    public AutoSubmitKey AutoSubmitKey { get; set; } = AutoSubmitKey.None;

    /// <summary>How long after the last session before the transcription model is unloaded to free RAM.</summary>
    public ModelUnloadTimeout ModelUnloadTimeout { get; set; } = ModelUnloadTimeout.Min5;

    // ── Clean Mode ────────────────────────────────────────────────────────
    /// <summary>Hotkey combination for clean-mode (LLM-rewritten) dictation.
    /// Default <c>"Ctrl+Alt+H"</c> is intentionally distinct from the raw
    /// <c>Ctrl+H</c> hotkey and avoids common OS / Office chords such as
    /// Alt+Tab, Alt+Shift (layout switch), or bare Alt menu activation.</summary>
    public string CleanHotkey { get; set; } = "Ctrl+Alt+H";

    /// <summary>Whether to run an LLM rewrite pass in clean-mode sessions.</summary>
    public bool PostProcessingEnabled { get; set; } = true;

    /// <summary>Foundry Local model alias used for LLM rewriting.</summary>
    public string PostProcessingModel { get; set; } = "qwen3-0.6b";

    /// <summary>ID of the active post-processing prompt template.</summary>
    public string? ActivePostProcessingPromptId { get; set; }

    /// <summary>User-defined prompt templates for LLM rewriting.</summary>
    public List<LlmPrompt> PostProcessingPrompts { get; set; } = new();

    public static IReadOnlyList<LlmPrompt> BuiltInPrompts { get; } = new LlmPrompt[]
    {
        new()
        {
            Id = "clean-dictation",
            Name = "Clean up dictation",
            Icon = "🧹",
            Description = "Default. Removes filler words, applies punctuation, honors self-corrections.",
            IsBuiltIn = true,
            Prompt =
                "You are a dictation cleanup engine. Rewrite the user's dictated transcript into clean, readable prose.\n\n" +
                "Rules:\n" +
                "- Remove filler words: \"um\", \"uh\", \"er\", \"ah\", \"like\" (when used as filler), \"you know\", \"I mean\" (when filler), \"sort of\", \"kind of\" (when filler), and stutter repetitions.\n" +
                "- Honor self-corrections: if the speaker says \"I mean X\", \"no wait Y\", \"scratch that Z\", \"actually Z\", keep only the corrected version.\n" +
                "- Add correct punctuation, capitalization, and sentence breaks.\n" +
                "- Fix obvious transcription errors from context (e.g. their/there/they're, or filler words misheard as fellow words/failure word) but do NOT invent facts or change meaning.\n" +
                "- Cleanup rules override preservation for filler words, stutters, and self-corrections.\n" +
                "- After cleanup, preserve the speaker's meaningful word choices, names, numbers, and technical terms exactly.\n" +
                "- Keep the same language as the input.\n\n" +
                "Before output, verify no standalone filler phrases such as \"um\", \"uh\", or \"you know\" remain.\n\n" +
                "Output ONLY the cleaned text. No preamble, no explanation, no quotes, no markdown fences, no \"Here is...\" or \"Sure,\"."
        },
        new()
        {
            Id = "email-reply",
            Name = "Email reply",
            Icon = "📧",
            Description = "Warm and professional. No greeting or signature.",
            IsBuiltIn = true,
            Prompt =
                "You are an email writing assistant. Convert the user's spoken thoughts into a polite, professional email body.\n\n" +
                "Rules:\n" +
                "- Remove filler words and self-corrections.\n" +
                "- Use a warm, professional tone. Complete sentences. Short paragraphs.\n" +
                "- Do NOT add a greeting (\"Hi ...\") or sign-off (\"Best, ...\") unless the speaker explicitly dictated one.\n" +
                "- Do NOT invent recipient names, dates, or facts not in the transcript.\n" +
                "- Preserve specific names, numbers, and requests verbatim.\n\n" +
                "Output ONLY the email body. No subject line, no commentary, no quotes, no markdown fences."
        },
        new()
        {
            Id = "chat-casual",
            Name = "Casual chat message",
            Icon = "💬",
            Description = "Short and friendly. Slack, iMessage, Discord.",
            IsBuiltIn = true,
            Prompt =
                "You are a casual messaging assistant. Convert the user's spoken thoughts into a natural, concise chat message.\n\n" +
                "Rules:\n" +
                "- Remove fillers and self-corrections.\n" +
                "- Use a relaxed, conversational tone. Contractions are fine. Keep it short.\n" +
                "- One short paragraph or a couple of quick sentences. No formal greetings or sign-offs.\n" +
                "- Preserve names and specific phrasing the speaker used.\n" +
                "- Do NOT add emoji that were not spoken.\n\n" +
                "Output ONLY the message text. No commentary, no quotes, no markdown fences."
        },
        new()
        {
            Id = "bullet-list",
            Name = "Bullet list",
            Icon = "📋",
            Description = "One idea per bullet. Great for action items and notes.",
            IsBuiltIn = true,
            Prompt =
                "You are a note-taking assistant. Convert the user's spoken text into a clean bulleted list.\n\n" +
                "Rules:\n" +
                "- One idea per bullet. Start each bullet with \"- \".\n" +
                "- Keep bullets short (≤ 15 words where possible). Use imperative voice for action items (\"Send report\", \"Call Alex\").\n" +
                "- Remove fillers, \"okay so\", \"let me think\", and self-corrections.\n" +
                "- Group obviously related items together in order spoken. Do not invent items.\n" +
                "- If the speaker grouped items under named sections, use \"## Heading\" above each group.\n\n" +
                "Output ONLY the list (with optional headings). No intro, no summary, no markdown fences."
        },
        new()
        {
            Id = "meeting-notes",
            Name = "Meeting notes",
            Icon = "🗓️",
            Description = "Structured summary, decisions, action items, open questions.",
            IsBuiltIn = true,
            Prompt =
                "You are a meeting notes editor. Convert the user's spoken recap into structured meeting notes.\n\n" +
                "Produce these sections in order, using Markdown headings. OMIT a section entirely if the transcript has nothing for it.\n" +
                "## Summary\n" +
                "A 1–2 sentence recap.\n" +
                "## Decisions\n" +
                "Bulleted decisions made.\n" +
                "## Action items\n" +
                "Bulleted, each as \"- [Owner] Action\" if an owner was mentioned, else \"- Action\".\n" +
                "## Open questions\n" +
                "Bulleted unresolved items.\n\n" +
                "Rules:\n" +
                "- Do NOT invent owners, dates, or decisions. Only use what was spoken.\n" +
                "- Remove fillers and tangents.\n" +
                "- Preserve names, numbers, and dates exactly.\n\n" +
                "Output ONLY the markdown. No preamble, no code fences."
        },
        new()
        {
            Id = "concise-rewrite",
            Name = "Tighten & clarify",
            Icon = "✂️",
            Description = "Same meaning, ~30% shorter. Cuts hedges and fluff.",
            IsBuiltIn = true,
            Prompt =
                "You are an editor. Rewrite the user's text to be clearer and more concise while preserving every fact, name, and number.\n\n" +
                "Rules:\n" +
                "- Cut filler, hedges, repetition, and tangents.\n" +
                "- Prefer active voice and plain words. Do NOT substitute jargon.\n" +
                "- Target ~60-80% of the original length.\n" +
                "- Keep the original structure (paragraphs or list) if the input had one.\n" +
                "- Same language as input.\n\n" +
                "Output ONLY the rewritten text. No commentary, no quotes, no markdown fences."
        },
        new()
        {
            Id = "markdown-format",
            Name = "Format as Markdown",
            Icon = "📝",
            Description = "Add headings, bold, inline `code` based on emphasis.",
            IsBuiltIn = true,
            Prompt =
                "You are a Markdown formatter. Convert the user's dictated text into a well-structured Markdown document.\n\n" +
                "Rules:\n" +
                "- Infer appropriate headings (## or ###), lists, **bold** for emphasized terms the speaker stressed, and inline `code` for anything clearly technical (file names, commands, identifiers).\n" +
                "- Preserve all content — this is formatting, not summarizing.\n" +
                "- Remove only fillers and self-corrections.\n" +
                "- Do NOT wrap the whole output in a ```markdown code fence. Emit raw markdown.\n\n" +
                "Output ONLY the markdown document. No preamble or explanation."
        },
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

/// <summary>Persisted user-defined prompt template entry.</summary>
public sealed record HushPromptEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Prompt { get; init; } = "";
}
