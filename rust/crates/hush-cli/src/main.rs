// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

use clap::{Parser, Subcommand};
use hush_core::live_audio::{
    run_live_transcription_blocking, LiveTranscriptionEvent, LiveTranscriptionOptions,
};
use std::sync::{
    atomic::{AtomicBool, Ordering},
    mpsc, Arc,
};
use std::time::{Duration, Instant};

#[derive(Debug, Parser)]
#[command(about = "Rust Hush CLI.")]
struct Args {
    #[command(subcommand)]
    command: Option<Command>,
}

#[derive(Debug, Subcommand)]
enum Command {
    Version,
    Live {
        #[arg(long, default_value = "nemotron-speech-streaming-en-0.6b")]
        model: String,

        #[arg(long, default_value = "en")]
        language: String,

        #[arg(long, default_value_t = 30)]
        seconds: u64,

        #[arg(long)]
        microphone: Option<String>,
    },
}

fn main() -> anyhow::Result<()> {
    let args = Args::parse();
    match args.command.unwrap_or(Command::Version) {
        Command::Version => println!("hush-cli {}", env!("CARGO_PKG_VERSION")),
        Command::Live {
            model,
            language,
            seconds,
            microphone,
        } => run_live(model, language, seconds, microphone)?,
    }

    Ok(())
}

fn run_live(
    model: String,
    language: String,
    seconds: u64,
    microphone: Option<String>,
) -> anyhow::Result<()> {
    let options = LiveTranscriptionOptions {
        model_alias: model,
        language,
        microphone_name: microphone,
        duration: Some(Duration::from_secs(seconds)),
    };
    let (tx, rx) = mpsc::channel();
    let stop_requested = Arc::new(AtomicBool::new(false));
    let worker_stop = Arc::clone(&stop_requested);
    let worker =
        std::thread::spawn(move || run_live_transcription_blocking(options, tx, worker_stop));

    println!("Listening for {seconds}s. Speak into your default microphone.");
    let started = Instant::now();
    while started.elapsed() < Duration::from_secs(seconds + 10) {
        match rx.recv_timeout(Duration::from_millis(100)) {
            Ok(LiveTranscriptionEvent::Status(status)) => println!("[status] {status}"),
            Ok(LiveTranscriptionEvent::Transcript { text, is_final, .. }) => {
                let label = if is_final { "final" } else { "draft" };
                println!("[{label}] {text}");
            }
            Ok(LiveTranscriptionEvent::Error(error)) => eprintln!("[error] {error}"),
            Ok(LiveTranscriptionEvent::Stopped) => break,
            Err(mpsc::RecvTimeoutError::Timeout) => {
                if worker.is_finished() {
                    break;
                }
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => break,
        }
    }

    stop_requested.store(true, Ordering::Relaxed);
    worker.join().unwrap_or_else(|_| {
        anyhow::Result::Err(anyhow::anyhow!("live transcription worker panicked"))
    })?;
    Ok(())
}
