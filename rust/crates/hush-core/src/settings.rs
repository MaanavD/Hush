// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

use anyhow::Context;
use serde::{Deserialize, Serialize};
use std::path::PathBuf;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct HushSettings {
    pub hotkey: String,
    pub clean_hotkey: String,
    pub microphone_name: String,
    pub language: String,
    pub transcription_model: String,
    pub partials_in_overlay: bool,
    pub post_processing_enabled: bool,
    pub post_processing_model: String,
    pub streaming_commit: bool,
    pub sound_effects: bool,
    pub auto_start: bool,
    pub auto_submit_key: String,
    pub overlay_opacity: f32,
    pub model_unload_timeout: String,
    pub active_prompt: String,
    pub custom_substitutions: Vec<TextSubstitution>,
}

impl Default for HushSettings {
    fn default() -> Self {
        Self {
            hotkey: "Ctrl+H".to_string(),
            clean_hotkey: "Ctrl+Alt+H".to_string(),
            microphone_name: SYSTEM_DEFAULT_MICROPHONE.to_string(),
            language: "en".to_string(),
            transcription_model: "nemotron-speech-streaming-en-0.6b".to_string(),
            partials_in_overlay: true,
            post_processing_enabled: true,
            post_processing_model: "qwen3-0.6b".to_string(),
            streaming_commit: true,
            sound_effects: true,
            auto_start: false,
            auto_submit_key: "None".to_string(),
            overlay_opacity: 0.85,
            model_unload_timeout: "After 5 min".to_string(),
            active_prompt: built_in_prompts()[0].prompt.to_string(),
            custom_substitutions: Vec::new(),
        }
    }
}

pub const SYSTEM_DEFAULT_MICROPHONE: &str = "System Default";

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct TextSubstitution {
    pub match_text: String,
    pub replace_text: String,
}

#[derive(Debug, Clone, Copy)]
pub struct BuiltInPrompt {
    pub id: &'static str,
    pub name: &'static str,
    pub description: &'static str,
    pub prompt: &'static str,
}

pub const CLEAN_DICTATION_PROMPT: &str =
    "You are a dictation cleanup engine. Rewrite the user's dictated transcript into clean, readable prose.\n\n\
Rules:\n\
- Remove filler words: \"um\", \"uh\", \"er\", \"ah\", \"like\" (when used as filler), \"you know\", \"I mean\" (when filler), \"sort of\", \"kind of\" (when filler), and stutter repetitions.\n\
- Honor self-corrections: if the speaker says \"I mean X\", \"no wait Y\", \"scratch that Z\", \"actually Z\", keep only the corrected version.\n\
- If the speaker gives two alternatives and then says the second is better, remove the first alternative entirely.\n\
- Add correct punctuation, capitalization, and sentence breaks.\n\
- Fix obvious transcription errors from context but do NOT invent facts or change meaning.\n\
- Cleanup rules override preservation for filler words, stutters, and self-corrections.\n\
- After cleanup, preserve the speaker's meaningful word choices, names, numbers, and technical terms exactly.\n\
- Keep the same language as the input.\n\n\
Before output, verify no standalone filler phrases such as \"um\", \"uh\", or \"you know\" remain.\n\n\
Output ONLY the cleaned text. No preamble, no explanation, no quotes, no markdown fences, no \"Here is...\" or \"Sure,\".";

const EMAIL_REPLY_PROMPT: &str =
    "You are an email writing assistant. Convert the user's spoken thoughts into a polite, professional email body.\n\n\
Rules:\n\
- Remove filler words and self-corrections.\n\
- Use a warm, professional tone. Complete sentences. Short paragraphs.\n\
- Do NOT add a greeting or sign-off unless the speaker explicitly dictated one.\n\
- Do NOT invent recipient names, dates, or facts not in the transcript.\n\
- Preserve specific names, numbers, and requests verbatim.\n\n\
Output ONLY the email body. No subject line, no commentary, no quotes, no markdown fences.";

const CHAT_CASUAL_PROMPT: &str =
    "You are a casual messaging assistant. Convert the user's spoken thoughts into a natural, concise chat message.\n\n\
Rules:\n\
- Remove fillers and self-corrections.\n\
- Use a relaxed, conversational tone. Contractions are fine. Keep it short.\n\
- One short paragraph or a couple of quick sentences. No formal greetings or sign-offs.\n\
- Preserve names and specific phrasing the speaker used.\n\
- Do NOT add emoji that were not spoken.\n\n\
Output ONLY the message text. No commentary, no quotes, no markdown fences.";

const BULLET_LIST_PROMPT: &str =
    "You are a note-taking assistant. Convert the user's spoken text into a clean bulleted list.\n\n\
Rules:\n\
- One idea per bullet. Start each bullet with \"- \".\n\
- Keep bullets short where possible. Use imperative voice for action items.\n\
- Remove fillers and self-corrections.\n\
- Group obviously related items together in order spoken. Do not invent items.\n\n\
Output ONLY the list. No intro, no summary, no markdown fences.";

const MEETING_NOTES_PROMPT: &str =
    "You are a meeting notes editor. Convert the user's spoken recap into structured meeting notes.\n\n\
Produce these sections in order, using Markdown headings. Omit a section entirely if the transcript has nothing for it:\n\
## Summary\n\
## Decisions\n\
## Action items\n\
## Open questions\n\n\
Rules:\n\
- Do NOT invent owners, dates, or decisions. Only use what was spoken.\n\
- Remove fillers and tangents.\n\
- Preserve names, numbers, and dates exactly.\n\n\
Output ONLY the markdown. No preamble, no code fences.";

const CONCISE_REWRITE_PROMPT: &str =
    "You are an editor. Rewrite the user's text to be clearer and more concise while preserving every fact, name, and number.\n\n\
Rules:\n\
- Cut filler, hedges, repetition, and tangents.\n\
- Prefer active voice and plain words. Do NOT substitute jargon.\n\
- Target roughly 60-80% of the original length.\n\
- Keep the original structure if the input had one.\n\
- Same language as input.\n\n\
Output ONLY the rewritten text. No commentary, no quotes, no markdown fences.";

const MARKDOWN_FORMAT_PROMPT: &str =
    "You are a Markdown formatter. Convert the user's dictated text into a well-structured Markdown document.\n\n\
Rules:\n\
- Infer appropriate headings, lists, bold, and inline code from the dictated content.\n\
- Preserve all content. This is formatting, not summarizing.\n\
- Remove only fillers and self-corrections.\n\
- Do NOT wrap the whole output in a markdown code fence.\n\n\
Output ONLY the markdown document. No preamble or explanation.";

pub fn built_in_prompts() -> &'static [BuiltInPrompt] {
    &[
        BuiltInPrompt {
            id: "clean-dictation",
            name: "Clean up dictation",
            description: "Default cleanup with punctuation and self-correction handling.",
            prompt: CLEAN_DICTATION_PROMPT,
        },
        BuiltInPrompt {
            id: "email-reply",
            name: "Email reply",
            description: "Warm and professional email body.",
            prompt: EMAIL_REPLY_PROMPT,
        },
        BuiltInPrompt {
            id: "chat-casual",
            name: "Casual chat message",
            description: "Short, friendly chat for messaging apps.",
            prompt: CHAT_CASUAL_PROMPT,
        },
        BuiltInPrompt {
            id: "bullet-list",
            name: "Bullet list",
            description: "One idea or action per bullet.",
            prompt: BULLET_LIST_PROMPT,
        },
        BuiltInPrompt {
            id: "meeting-notes",
            name: "Meeting notes",
            description: "Summary, decisions, actions, and open questions.",
            prompt: MEETING_NOTES_PROMPT,
        },
        BuiltInPrompt {
            id: "concise-rewrite",
            name: "Tighten and clarify",
            description: "Same meaning, shorter and clearer.",
            prompt: CONCISE_REWRITE_PROMPT,
        },
        BuiltInPrompt {
            id: "markdown-format",
            name: "Format as Markdown",
            description: "Headings, lists, emphasis, and inline code.",
            prompt: MARKDOWN_FORMAT_PROMPT,
        },
    ]
}

impl HushSettings {
    pub fn load_or_default() -> anyhow::Result<Self> {
        let path = settings_path()?;
        if !path.exists() {
            return Ok(Self::default());
        }

        let json = std::fs::read_to_string(&path)
            .with_context(|| format!("reading settings from {}", path.display()))?;
        serde_json::from_str(&json)
            .with_context(|| format!("parsing settings from {}", path.display()))
    }

    pub fn save(&self) -> anyhow::Result<()> {
        let path = settings_path()?;
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)
                .with_context(|| format!("creating settings directory {}", parent.display()))?;
        }

        let json = serde_json::to_string_pretty(self)?;
        std::fs::write(&path, json)
            .with_context(|| format!("writing settings to {}", path.display()))
    }
}

pub fn settings_path() -> anyhow::Result<PathBuf> {
    let home = std::env::var_os("USERPROFILE")
        .or_else(|| std::env::var_os("HOME"))
        .context("USERPROFILE/HOME is not set")?;
    Ok(PathBuf::from(home).join(".hush").join("rust-settings.json"))
}
