// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct FileBenchmarkOptions {
    pub audio_file: String,
    pub model_alias: String,
    pub language: String,
    pub streaming: bool,
}

impl FileBenchmarkOptions {
    pub fn new(audio_file: impl Into<String>, model_alias: impl Into<String>) -> Self {
        Self {
            audio_file: audio_file.into(),
            model_alias: model_alias.into(),
            language: "en".to_string(),
            streaming: true,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct FileBenchmarkResult {
    pub implementation: String,
    pub model_alias: String,
    pub audio_file: String,
    pub language: String,
    pub runtime_init_ms: u128,
    pub model_lookup_ms: u128,
    pub model_download_ms: u128,
    pub model_load_ms: u128,
    pub transcription_ms: u128,
    pub streaming_transcription_ms: Option<u128>,
    pub transcript_chars: usize,
    pub transcript: String,
    pub streaming_transcript: Option<String>,
}
