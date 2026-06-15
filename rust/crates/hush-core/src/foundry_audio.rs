// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

use std::io::{self, Write};
use std::time::Instant;

use anyhow::Context;
use foundry_local_sdk::{FoundryLocalConfig, FoundryLocalManager};
use tokio_stream::StreamExt;

use crate::benchmark::{FileBenchmarkOptions, FileBenchmarkResult};

pub async fn run_file_benchmark(
    options: FileBenchmarkOptions,
) -> anyhow::Result<FileBenchmarkResult> {
    let runtime_start = Instant::now();
    let manager = FoundryLocalManager::create(FoundryLocalConfig::new("hush-rust-bench"))
        .context("initializing Foundry Local manager")?;
    let runtime_init_ms = runtime_start.elapsed().as_millis();

    let lookup_start = Instant::now();
    let model = manager
        .catalog()
        .get_model(&options.model_alias)
        .await
        .with_context(|| format!("resolving model '{}'", options.model_alias))?;
    let model_lookup_ms = lookup_start.elapsed().as_millis();

    let download_start = Instant::now();
    if !model
        .is_cached()
        .await
        .context("checking model cache state")?
    {
        model
            .download(Some(|progress: f64| {
                print!("\rDownloading model... {progress:5.1}%");
                let _ = io::stdout().flush();
            }))
            .await
            .context("downloading model")?;
        println!();
    }
    let model_download_ms = download_start.elapsed().as_millis();

    let load_start = Instant::now();
    model.load().await.context("loading model")?;
    let model_load_ms = load_start.elapsed().as_millis();

    let audio_client = model.create_audio_client().language(&options.language);

    let transcribe_start = Instant::now();
    let response = audio_client
        .transcribe(&options.audio_file)
        .await
        .with_context(|| format!("transcribing '{}'", options.audio_file))?;
    let transcription_ms = transcribe_start.elapsed().as_millis();
    let transcript = response.text;

    let (streaming_transcription_ms, streaming_transcript) = if options.streaming {
        let streaming_start = Instant::now();
        let mut stream = audio_client
            .transcribe_streaming(&options.audio_file)
            .await
            .with_context(|| format!("streaming transcription for '{}'", options.audio_file))?;
        let mut streaming_text = String::new();
        while let Some(chunk) = stream.next().await {
            streaming_text.push_str(&chunk.context("reading streaming transcription chunk")?.text);
        }
        (
            Some(streaming_start.elapsed().as_millis()),
            Some(streaming_text),
        )
    } else {
        (None, None)
    };

    model.unload().await.context("unloading model")?;

    Ok(FileBenchmarkResult {
        implementation: "rust-foundry-local".to_string(),
        model_alias: options.model_alias,
        audio_file: options.audio_file,
        language: options.language,
        runtime_init_ms,
        model_lookup_ms,
        model_download_ms,
        model_load_ms,
        transcription_ms,
        streaming_transcription_ms,
        transcript_chars: transcript.chars().count(),
        transcript,
        streaming_transcript,
    })
}
