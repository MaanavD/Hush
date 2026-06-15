// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

use std::sync::{
    atomic::{AtomicBool, Ordering},
    mpsc, Arc,
};
use std::time::Duration;

use anyhow::{bail, Context};
use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
use foundry_local_sdk::{FoundryLocalConfig, FoundryLocalManager, Model};
use tokio_stream::StreamExt;

use crate::settings::SYSTEM_DEFAULT_MICROPHONE;

const TARGET_SAMPLE_RATE: u32 = 16_000;
const TARGET_CHANNELS: u32 = 1;
const TARGET_BITS_PER_SAMPLE: u32 = 16;
const DEFAULT_PUSH_QUEUE_CAPACITY: usize = 12;
const AUDIO_QUEUE_CAPACITY: usize = 48;

#[derive(Debug, Clone)]
pub struct LiveTranscriptionOptions {
    pub model_alias: String,
    pub language: String,
    pub microphone_name: Option<String>,
    pub duration: Option<Duration>,
}

#[derive(Clone)]
pub struct LoadedTranscriptionModel {
    model: Arc<Model>,
}

impl LoadedTranscriptionModel {
    pub async fn load(
        model_alias: &str,
        events: &mpsc::Sender<LiveTranscriptionEvent>,
    ) -> anyhow::Result<Self> {
        send_status(events, "Initializing Foundry Local...");
        let manager = FoundryLocalManager::create(FoundryLocalConfig::new("hush-rust-live"))
            .context("initializing Foundry Local manager")?;

        send_status(events, format!("Resolving model '{model_alias}'..."));
        let model = manager
            .catalog()
            .get_model(model_alias)
            .await
            .with_context(|| format!("resolving model '{model_alias}'"))?;

        if !model
            .is_cached()
            .await
            .context("checking transcription model cache state")?
        {
            send_status(events, "Downloading transcription model...");
            model
                .download(Some(|_| {}))
                .await
                .context("downloading transcription model")?;
        }

        send_status(events, "Loading transcription model...");
        model.load().await.context("loading transcription model")?;
        send_status(events, "Ready - hold Ctrl+H to dictate");
        Ok(Self { model })
    }

    pub fn model(&self) -> &Model {
        &self.model
    }
}

impl Default for LiveTranscriptionOptions {
    fn default() -> Self {
        Self {
            model_alias: "nemotron-speech-streaming-en-0.6b".to_string(),
            language: "en".to_string(),
            microphone_name: None,
            duration: None,
        }
    }
}

#[derive(Debug, Clone)]
pub enum LiveTranscriptionEvent {
    Status(String),
    Transcript {
        text: String,
        is_final: bool,
        start_time: Option<f64>,
        end_time: Option<f64>,
    },
    Error(String),
    Stopped,
}

pub fn run_live_transcription_blocking(
    options: LiveTranscriptionOptions,
    events: mpsc::Sender<LiveTranscriptionEvent>,
    stop_requested: Arc<AtomicBool>,
) -> anyhow::Result<()> {
    let runtime = tokio::runtime::Runtime::new().context("creating Tokio runtime")?;
    runtime.block_on(run_live_transcription(options, events, stop_requested))
}

pub async fn run_live_transcription(
    options: LiveTranscriptionOptions,
    events: mpsc::Sender<LiveTranscriptionEvent>,
    stop_requested: Arc<AtomicBool>,
) -> anyhow::Result<()> {
    let model = LoadedTranscriptionModel::load(&options.model_alias, &events).await?;
    run_live_transcription_with_model(model.model(), options, events, stop_requested).await
}

pub async fn run_live_transcription_with_model(
    model: &Model,
    options: LiveTranscriptionOptions,
    events: mpsc::Sender<LiveTranscriptionEvent>,
    stop_requested: Arc<AtomicBool>,
) -> anyhow::Result<()> {
    let audio_client = model.create_audio_client();
    let mut session = audio_client.create_live_transcription_session();
    session.settings.sample_rate = TARGET_SAMPLE_RATE;
    session.settings.channels = TARGET_CHANNELS;
    session.settings.bits_per_sample = TARGET_BITS_PER_SAMPLE;
    session.settings.language = Some(options.language.clone());
    session.settings.push_queue_capacity = DEFAULT_PUSH_QUEUE_CAPACITY;

    send_status(&events, "Starting live transcription session...");
    session
        .start(None)
        .await
        .context("starting live transcription session")?;
    let session = Arc::new(session);
    let result_stream = session
        .get_stream()
        .await
        .context("opening live transcription result stream")?;

    let (audio_tx, mut audio_rx) = tokio::sync::mpsc::channel::<Vec<u8>>(AUDIO_QUEUE_CAPACITY);
    let input_stream = start_microphone_stream(audio_tx, options.microphone_name.as_deref())
        .context("starting microphone capture")?;

    let append_session = Arc::clone(&session);
    let append_events = events.clone();
    let append_task = tokio::spawn(async move {
        while let Some(pcm) = audio_rx.recv().await {
            if let Err(err) = append_session.append(&pcm, None).await {
                let _ = append_events.send(LiveTranscriptionEvent::Error(format!(
                    "Audio append failed: {err}"
                )));
                break;
            }
        }
    });

    let result_events = events.clone();
    let result_task = tokio::spawn(async move {
        let mut stream = result_stream;
        while let Some(item) = stream.next().await {
            match item {
                Ok(response) => {
                    if let Some(content) = response.content.first() {
                        if !content.text.trim().is_empty() {
                            let _ = result_events.send(LiveTranscriptionEvent::Transcript {
                                text: content.text.clone(),
                                is_final: response.is_final,
                                start_time: response.start_time,
                                end_time: response.end_time,
                            });
                        }
                    }
                }
                Err(err) => {
                    let _ = result_events.send(LiveTranscriptionEvent::Error(format!(
                        "Stream failed: {err}"
                    )));
                    break;
                }
            }
        }
    });

    send_status(&events, "Listening...");
    wait_for_stop(stop_requested, options.duration).await;

    drop(input_stream);

    let _ = append_task.await;
    session
        .stop(None)
        .await
        .context("stopping live transcription session")?;
    let _ = result_task.await;
    let _ = events.send(LiveTranscriptionEvent::Stopped);

    Ok(())
}

pub fn available_input_devices() -> anyhow::Result<Vec<String>> {
    let host = cpal::default_host();
    let mut devices = vec![SYSTEM_DEFAULT_MICROPHONE.to_string()];

    for device in host
        .input_devices()
        .context("enumerating microphone input devices")?
    {
        if let Ok(name) = device.name() {
            if !name.trim().is_empty() && !devices.iter().any(|existing| existing == &name) {
                devices.push(name);
            }
        }
    }

    Ok(devices)
}

fn start_microphone_stream(
    audio_tx: tokio::sync::mpsc::Sender<Vec<u8>>,
    microphone_name: Option<&str>,
) -> anyhow::Result<cpal::Stream> {
    let host = cpal::default_host();
    let device = resolve_input_device(&host, microphone_name)?;
    let supported_config = device
        .default_input_config()
        .with_context(|| format!("reading input config for {}", device_display_name(&device)))?;
    let sample_rate = supported_config.sample_rate().0;
    let channels = supported_config.channels();
    let config: cpal::StreamConfig = supported_config.clone().into();
    let err_fn = |err| eprintln!("microphone stream error: {err}");

    let stream = match supported_config.sample_format() {
        cpal::SampleFormat::F32 => {
            build_stream::<f32>(&device, &config, sample_rate, channels, audio_tx, err_fn)
        }
        cpal::SampleFormat::F64 => {
            build_stream::<f64>(&device, &config, sample_rate, channels, audio_tx, err_fn)
        }
        cpal::SampleFormat::I8 => {
            build_stream::<i8>(&device, &config, sample_rate, channels, audio_tx, err_fn)
        }
        cpal::SampleFormat::I16 => {
            build_stream::<i16>(&device, &config, sample_rate, channels, audio_tx, err_fn)
        }
        cpal::SampleFormat::I32 => {
            build_stream::<i32>(&device, &config, sample_rate, channels, audio_tx, err_fn)
        }
        cpal::SampleFormat::U8 => {
            build_stream::<u8>(&device, &config, sample_rate, channels, audio_tx, err_fn)
        }
        cpal::SampleFormat::U16 => {
            build_stream::<u16>(&device, &config, sample_rate, channels, audio_tx, err_fn)
        }
        cpal::SampleFormat::U32 => {
            build_stream::<u32>(&device, &config, sample_rate, channels, audio_tx, err_fn)
        }
        sample_format => bail!("unsupported microphone sample format '{sample_format}'"),
    }?;

    stream.play().context("starting microphone input stream")?;
    Ok(stream)
}

fn resolve_input_device(
    host: &cpal::Host,
    microphone_name: Option<&str>,
) -> anyhow::Result<cpal::Device> {
    let selected = microphone_name
        .map(str::trim)
        .filter(|name| !name.is_empty())
        .unwrap_or(SYSTEM_DEFAULT_MICROPHONE);

    if selected == SYSTEM_DEFAULT_MICROPHONE {
        return host
            .default_input_device()
            .context("no default input microphone is available");
    }

    let mut available = Vec::new();
    for device in host
        .input_devices()
        .context("enumerating microphone input devices")?
    {
        let name = device_display_name(&device);
        if name == selected {
            return Ok(device);
        }
        available.push(name);
    }

    bail!(
        "selected microphone '{selected}' is not available. Choose one of: {}",
        available.join(", ")
    );
}

fn device_display_name(device: &cpal::Device) -> String {
    device
        .name()
        .unwrap_or_else(|_| "Unknown microphone".to_string())
}

fn build_stream<T>(
    device: &cpal::Device,
    config: &cpal::StreamConfig,
    input_sample_rate: u32,
    input_channels: u16,
    audio_tx: tokio::sync::mpsc::Sender<Vec<u8>>,
    err_fn: impl FnMut(cpal::StreamError) + Send + 'static,
) -> anyhow::Result<cpal::Stream>
where
    T: cpal::SizedSample + ToMonoF32,
{
    let mut converter = Pcm16kMonoConverter::new(input_sample_rate, input_channels);
    let stream = device.build_input_stream(
        config,
        move |data: &[T], _: &_| {
            let pcm = converter.convert(data);
            if !pcm.is_empty() {
                let _ = audio_tx.try_send(pcm);
            }
        },
        err_fn,
        None,
    )?;
    Ok(stream)
}

async fn wait_for_stop(stop_requested: Arc<AtomicBool>, duration: Option<Duration>) {
    let started = std::time::Instant::now();
    loop {
        if stop_requested.load(Ordering::Relaxed) {
            break;
        }

        if let Some(duration) = duration {
            if started.elapsed() >= duration {
                break;
            }
        }

        tokio::time::sleep(Duration::from_millis(50)).await;
    }
}

fn send_status(events: &mpsc::Sender<LiveTranscriptionEvent>, status: impl Into<String>) {
    let _ = events.send(LiveTranscriptionEvent::Status(status.into()));
}

struct Pcm16kMonoConverter {
    input_sample_rate: u32,
    input_channels: usize,
    resample_accumulator: u32,
}

impl Pcm16kMonoConverter {
    fn new(input_sample_rate: u32, input_channels: u16) -> Self {
        Self {
            input_sample_rate,
            input_channels: usize::from(input_channels.max(1)),
            resample_accumulator: 0,
        }
    }

    fn convert<T: ToMonoF32>(&mut self, data: &[T]) -> Vec<u8> {
        let frame_count = data.len() / self.input_channels;
        let mut out = Vec::with_capacity(frame_count * 2);

        for frame in data.chunks_exact(self.input_channels) {
            let mono =
                frame.iter().map(ToMonoF32::to_mono_f32).sum::<f32>() / self.input_channels as f32;
            self.resample_accumulator += TARGET_SAMPLE_RATE;
            while self.resample_accumulator >= self.input_sample_rate {
                self.resample_accumulator -= self.input_sample_rate;
                let sample = f32_to_i16(mono);
                out.extend_from_slice(&sample.to_le_bytes());
            }
        }

        out
    }
}

fn f32_to_i16(sample: f32) -> i16 {
    (sample.clamp(-1.0, 1.0) * i16::MAX as f32).round() as i16
}

trait ToMonoF32 {
    fn to_mono_f32(&self) -> f32;
}

impl ToMonoF32 for f32 {
    fn to_mono_f32(&self) -> f32 {
        *self
    }
}

impl ToMonoF32 for f64 {
    fn to_mono_f32(&self) -> f32 {
        *self as f32
    }
}

impl ToMonoF32 for i8 {
    fn to_mono_f32(&self) -> f32 {
        *self as f32 / i8::MAX as f32
    }
}

impl ToMonoF32 for i16 {
    fn to_mono_f32(&self) -> f32 {
        *self as f32 / i16::MAX as f32
    }
}

impl ToMonoF32 for i32 {
    fn to_mono_f32(&self) -> f32 {
        *self as f32 / i32::MAX as f32
    }
}

impl ToMonoF32 for u8 {
    fn to_mono_f32(&self) -> f32 {
        (*self as f32 / u8::MAX as f32) * 2.0 - 1.0
    }
}

impl ToMonoF32 for u16 {
    fn to_mono_f32(&self) -> f32 {
        (*self as f32 / u16::MAX as f32) * 2.0 - 1.0
    }
}

impl ToMonoF32 for u32 {
    fn to_mono_f32(&self) -> f32 {
        (*self as f32 / u32::MAX as f32) * 2.0 - 1.0
    }
}
