// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

use std::sync::mpsc;
use std::sync::Arc;

use anyhow::Context;
use foundry_local_sdk::{
    ChatCompletionRequestMessage, ChatCompletionRequestSystemMessage,
    ChatCompletionRequestUserMessage, FoundryLocalConfig, FoundryLocalManager, Model,
};

use crate::live_audio::LiveTranscriptionEvent;
use crate::settings::CLEAN_DICTATION_PROMPT;

const INPUT_PLACEHOLDER: &str = "{input}";
const MAX_POST_PROCESSING_TOKENS: u32 = 1024;

#[derive(Clone)]
pub struct LoadedPostProcessor {
    model: Arc<Model>,
}

impl LoadedPostProcessor {
    pub async fn load(
        model_alias: &str,
        events: &mpsc::Sender<LiveTranscriptionEvent>,
    ) -> anyhow::Result<Self> {
        let manager = FoundryLocalManager::create(FoundryLocalConfig::new("hush-rust-live"))
            .context("initializing Foundry Local manager for cleaning model")?;
        let model = manager
            .catalog()
            .get_model(model_alias)
            .await
            .with_context(|| format!("resolving cleaning model '{model_alias}'"))?;

        if !model
            .is_cached()
            .await
            .context("checking cleaning model cache state")?
        {
            let _ = events.send(LiveTranscriptionEvent::Status(
                "Downloading clean-mode model...".to_string(),
            ));
            model
                .download(Some(|_| {}))
                .await
                .context("downloading cleaning model")?;
        }

        let _ = events.send(LiveTranscriptionEvent::Status(
            "Loading clean-mode model...".to_string(),
        ));
        model.load().await.context("loading cleaning model")?;
        Ok(Self { model })
    }

    pub async fn rewrite(
        &self,
        raw_transcript: &str,
        system_prompt: &str,
    ) -> anyhow::Result<String> {
        let (system_message, user_message) = build_prompt_messages(raw_transcript, system_prompt);
        let client = self
            .model
            .create_chat_client()
            .temperature(0.0)
            .top_p(1.0)
            .random_seed(0)
            .max_tokens(MAX_POST_PROCESSING_TOKENS);
        let messages: Vec<ChatCompletionRequestMessage> = vec![
            ChatCompletionRequestSystemMessage::from(system_message).into(),
            ChatCompletionRequestUserMessage::from(user_message).into(),
        ];

        let response = client
            .complete_chat(&messages, None)
            .await
            .context("running clean-mode rewrite")?;
        let raw = response
            .choices
            .first()
            .and_then(|choice| choice.message.content.as_deref())
            .unwrap_or("")
            .trim();

        Ok(apply_clean_dictation_safeguards(
            &strip_thinking(raw),
            system_prompt,
        ))
    }
}

fn build_prompt_messages(raw_transcript: &str, system_prompt: &str) -> (String, String) {
    if contains_ignore_case(system_prompt, INPUT_PLACEHOLDER) {
        (
            "You are Hush's post-processing assistant. Follow the user's prompt exactly and output only the final transformed text.".to_string(),
            with_no_think(&replace_ignore_case(system_prompt, INPUT_PLACEHOLDER, raw_transcript)),
        )
    } else {
        (system_prompt.to_string(), with_no_think(raw_transcript))
    }
}

fn with_no_think(text: &str) -> String {
    format!("/no_think\n{text}")
}

fn strip_thinking(text: &str) -> String {
    let lower = text.to_lowercase();
    if let Some(idx) = lower.find("</think>") {
        return text[(idx + "</think>".len())..].trim().to_string();
    }

    let normalized_breaks = text.replace("\r\n\r\n", "\n\n");
    let paragraphs: Vec<_> = normalized_breaks
        .split("\n\n")
        .map(str::trim)
        .filter(|p| !p.is_empty())
        .collect();
    if paragraphs.len() > 1 {
        paragraphs.last().copied().unwrap_or("").to_string()
    } else {
        text.trim().to_string()
    }
}

fn apply_clean_dictation_safeguards(text: &str, system_prompt: &str) -> String {
    if system_prompt != CLEAN_DICTATION_PROMPT {
        return text.to_string();
    }

    let mut cleaned = text.to_string();
    for filler in [" um ", " uh ", " er ", " ah ", " you know "] {
        cleaned = cleaned.replace(filler, " ");
    }
    cleaned = cleaned.replace("I I", "I");
    cleaned = normalize_whitespace_and_punctuation(&cleaned);
    capitalize_first_ascii_letter(&cleaned)
}

fn normalize_whitespace_and_punctuation(text: &str) -> String {
    let mut normalized = text.split_whitespace().collect::<Vec<_>>().join(" ");
    for punctuation in [",", ".", ";", ":", "!", "?"] {
        normalized = normalized.replace(&format!(" {punctuation}"), punctuation);
    }
    normalized
        .trim_start_matches([',', ';', ':'])
        .trim()
        .to_string()
}

fn capitalize_first_ascii_letter(text: &str) -> String {
    let Some(first) = text.chars().next() else {
        return String::new();
    };
    if first.is_ascii_lowercase() {
        format!(
            "{}{}",
            first.to_ascii_uppercase(),
            &text[first.len_utf8()..]
        )
    } else {
        text.to_string()
    }
}

fn contains_ignore_case(haystack: &str, needle: &str) -> bool {
    haystack.to_lowercase().contains(&needle.to_lowercase())
}

fn replace_ignore_case(haystack: &str, needle: &str, replacement: &str) -> String {
    let lower_haystack = haystack.to_lowercase();
    let lower_needle = needle.to_lowercase();
    if let Some(idx) = lower_haystack.find(&lower_needle) {
        format!(
            "{}{}{}",
            &haystack[..idx],
            replacement,
            &haystack[(idx + needle.len())..]
        )
    } else {
        haystack.to_string()
    }
}
