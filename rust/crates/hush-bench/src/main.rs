// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

use clap::Parser;
use hush_core::benchmark::FileBenchmarkOptions;

#[derive(Debug, Parser)]
#[command(about = "Run Rust/Foundry Local transcription benchmarks.")]
struct Args {
    #[arg(long)]
    audio_file: String,

    #[arg(long, default_value = "whisper-tiny")]
    model: String,

    #[arg(long, default_value = "en")]
    language: String,

    #[arg(long)]
    no_streaming: bool,

    #[arg(long)]
    json: bool,
}

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    let args = Args::parse();
    let mut options = FileBenchmarkOptions::new(args.audio_file, args.model);
    options.language = args.language;
    options.streaming = !args.no_streaming;

    let result = hush_core::foundry_audio::run_file_benchmark(options).await?;
    if args.json {
        println!("{}", serde_json::to_string_pretty(&result)?);
    } else {
        println!("Implementation     : {}", result.implementation);
        println!("Model              : {}", result.model_alias);
        println!("Audio file         : {}", result.audio_file);
        println!("Runtime init       : {} ms", result.runtime_init_ms);
        println!("Model lookup       : {} ms", result.model_lookup_ms);
        println!("Model download     : {} ms", result.model_download_ms);
        println!("Model load         : {} ms", result.model_load_ms);
        println!("Transcription      : {} ms", result.transcription_ms);
        if let Some(ms) = result.streaming_transcription_ms {
            println!("Streaming          : {ms} ms");
        }
        println!("Transcript chars   : {}", result.transcript_chars);
        println!("Transcript         : {}", result.transcript);
    }

    Ok(())
}
