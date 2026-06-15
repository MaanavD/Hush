// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

use hush_core::live_audio::{
    available_input_devices, run_live_transcription_with_model, LiveTranscriptionEvent,
    LiveTranscriptionOptions, LoadedTranscriptionModel,
};
use hush_core::platform::{
    capture_text_target, press_auto_submit_key_to_target, replace_text_to_target,
    type_text_to_target, GlobalHotkey, HotkeyEvent, TextTarget,
};
use hush_core::post_processing::LoadedPostProcessor;
use hush_core::settings::{built_in_prompts, HushSettings, SYSTEM_DEFAULT_MICROPHONE};
use serde::Serialize;
use std::sync::{
    atomic::{AtomicBool, Ordering},
    mpsc, Arc, Mutex,
};
use std::thread;
use std::time::Duration;
use tauri::image::Image;
use tauri::menu::{Menu, MenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{
    App, AppHandle, Emitter, Manager, PhysicalPosition, Position, WebviewUrl, WebviewWindow,
    WebviewWindowBuilder, WindowEvent,
};

const OVERLAY_WIDTH: u32 = 304;
const OVERLAY_HEIGHT: u32 = 124;
const SETTINGS_WIDTH: f64 = 520.0;
const SETTINGS_HEIGHT: f64 = 880.0;

type SharedController = Arc<Mutex<Controller>>;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum SessionMode {
    Raw,
    Clean,
    Preview,
}

enum AppEvent {
    Status(String),
    TranscriptionReady(LoadedTranscriptionModel),
    CleanerReady(LoadedPostProcessor),
    CleanerError(String),
    Cleaned(String),
}

#[derive(Serialize, Clone)]
struct OverlayPayload {
    kind: &'static str,
    title: String,
    detail: String,
    hint: String,
    mode: &'static str,
    accent: &'static str,
    intensity: f32,
}

#[derive(Serialize)]
struct SettingsResponse {
    settings: HushSettings,
    microphones: Vec<String>,
    prompts: Vec<PromptPayload>,
    language_options: Vec<String>,
    transcription_model_options: Vec<String>,
    post_processing_model_options: Vec<String>,
    auto_submit_options: Vec<String>,
    model_unload_options: Vec<String>,
}

#[derive(Serialize)]
struct PromptPayload {
    id: &'static str,
    name: &'static str,
    description: &'static str,
    prompt: &'static str,
}

struct Controller {
    settings: HushSettings,
    saved_settings: HushSettings,
    status: String,
    preparing_detail: String,
    transcript: String,
    draft: String,
    error: Option<String>,
    is_model_ready: bool,
    is_cleaning_model_ready: bool,
    is_finalizing: bool,
    live_receiver: Option<mpsc::Receiver<LiveTranscriptionEvent>>,
    app_sender: mpsc::Sender<AppEvent>,
    _hotkey: Option<GlobalHotkey>,
    stop_requested: Option<Arc<AtomicBool>>,
    worker: Option<thread::JoinHandle<anyhow::Result<()>>>,
    transcription_model: Option<LoadedTranscriptionModel>,
    cleaner: Option<LoadedPostProcessor>,
    active_mode: Option<SessionMode>,
    stop_in_progress: bool,
    text_target: Option<TextTarget>,
    session_buffer: String,
    raw_preview_text: String,
    microphones: Vec<String>,
    microphone_status: Option<String>,
    overlay_was_visible: bool,
}

impl Controller {
    fn new(app_sender: mpsc::Sender<AppEvent>, hotkey: anyhow::Result<GlobalHotkey>) -> Self {
        let mut settings = HushSettings::load_or_default().unwrap_or_default();
        if settings.microphone_name.trim().is_empty() {
            settings.microphone_name = SYSTEM_DEFAULT_MICROPHONE.to_string();
        }
        if settings.active_prompt.trim().is_empty() {
            settings.active_prompt = built_in_prompts()[0].prompt.to_string();
        }

        let (mut microphones, microphone_status) = match available_input_devices() {
            Ok(devices) => (devices, None),
            Err(err) => (
                vec![SYSTEM_DEFAULT_MICROPHONE.to_string()],
                Some(format!("Could not list microphones: {err}")),
            ),
        };
        if !microphones
            .iter()
            .any(|name| name == &settings.microphone_name)
        {
            microphones.push(settings.microphone_name.clone());
        }

        let (status, hotkey, hotkey_error) = match hotkey {
            Ok(hotkey) => ("Ready".to_string(), Some(hotkey), None),
            Err(err) => (
                "Hotkey unavailable".to_string(),
                None,
                Some(format!("Hotkey unavailable: {err}")),
            ),
        };

        let saved_settings = settings.clone();
        Self {
            settings,
            saved_settings,
            status,
            preparing_detail: "Starting Foundry Local".to_string(),
            transcript: String::new(),
            draft: String::new(),
            error: hotkey_error.or(microphone_status.clone()),
            is_model_ready: false,
            is_cleaning_model_ready: false,
            is_finalizing: false,
            live_receiver: None,
            app_sender,
            _hotkey: hotkey,
            stop_requested: None,
            worker: None,
            transcription_model: None,
            cleaner: None,
            active_mode: None,
            stop_in_progress: false,
            text_target: None,
            session_buffer: String::new(),
            raw_preview_text: String::new(),
            microphones,
            microphone_status,
            overlay_was_visible: false,
        }
    }

    fn preload_models(&self) {
        let settings = self.settings.clone();
        let sender = self.app_sender.clone();
        thread::spawn(move || {
            let runtime = match tokio::runtime::Runtime::new() {
                Ok(runtime) => runtime,
                Err(err) => {
                    let _ = sender.send(AppEvent::Status(format!("Runtime failed: {err}")));
                    return;
                }
            };

            runtime.block_on(async move {
                let (status_tx, status_rx) = mpsc::channel();
                let status_bridge = sender.clone();
                let bridge = thread::spawn(move || {
                    while let Ok(event) = status_rx.recv() {
                        if let LiveTranscriptionEvent::Status(status) = event {
                            let _ = status_bridge.send(AppEvent::Status(status));
                        }
                    }
                });

                match LoadedTranscriptionModel::load(&settings.transcription_model, &status_tx)
                    .await
                {
                    Ok(model) => {
                        let _ = sender.send(AppEvent::TranscriptionReady(model));
                    }
                    Err(err) => {
                        let _ = sender.send(AppEvent::Status(format!("Model failed: {err}")));
                        drop(status_tx);
                        let _ = bridge.join();
                        return;
                    }
                }

                if settings.post_processing_enabled {
                    match LoadedPostProcessor::load(&settings.post_processing_model, &status_tx)
                        .await
                    {
                        Ok(cleaner) => {
                            let _ = sender.send(AppEvent::CleanerReady(cleaner));
                        }
                        Err(err) => {
                            let _ = sender.send(AppEvent::CleanerError(format!("{err}")));
                        }
                    }
                }

                drop(status_tx);
                let _ = bridge.join();
            });
        });
    }

    fn drain_app_events(&mut self, app_rx: &mpsc::Receiver<AppEvent>) {
        while let Ok(event) = app_rx.try_recv() {
            match event {
                AppEvent::Status(status) => {
                    self.preparing_detail = status.clone();
                    self.status = status;
                }
                AppEvent::TranscriptionReady(model) => {
                    self.transcription_model = Some(model);
                    self.is_model_ready = true;
                    self.status = "Ready".to_string();
                }
                AppEvent::CleanerReady(cleaner) => {
                    self.cleaner = Some(cleaner);
                    self.is_cleaning_model_ready = true;
                }
                AppEvent::CleanerError(error) => {
                    self.is_finalizing = false;
                    self.error = Some(format!("Clean mode unavailable: {error}"));
                }
                AppEvent::Cleaned(cleaned) => {
                    self.is_finalizing = false;
                    self.commit_text(&cleaned, true);
                    self.text_target = None;
                    self.status = "Clean text committed".to_string();
                }
            }
        }
    }

    fn drain_hotkeys(&mut self, hotkey_rx: &mpsc::Receiver<HotkeyEvent>) {
        while let Ok(event) = hotkey_rx.try_recv() {
            match event {
                HotkeyEvent::RawPressed => {
                    if !self.is_running() {
                        self.start_live(SessionMode::Raw);
                    }
                }
                HotkeyEvent::RawReleased => self.stop_live(),
                HotkeyEvent::CleanPressed => {
                    if !self.is_running() {
                        self.start_live(SessionMode::Clean);
                    }
                }
                HotkeyEvent::CleanReleased => self.stop_live(),
                HotkeyEvent::Error(error) => self.error = Some(error),
            }
        }
    }

    fn drain_live_events(&mut self) {
        if let Some(receiver) = self.live_receiver.take() {
            let mut keep_receiver = true;
            while let Ok(event) = receiver.try_recv() {
                match event {
                    LiveTranscriptionEvent::Status(status) => self.status = status,
                    LiveTranscriptionEvent::Transcript { text, is_final, .. } => {
                        if is_final {
                            self.handle_final_text(text.trim());
                        } else {
                            self.handle_draft_text(text.trim());
                        }
                    }
                    LiveTranscriptionEvent::Error(error) => self.error = Some(error),
                    LiveTranscriptionEvent::Stopped => {
                        keep_receiver = false;
                        self.finish_session();
                    }
                }
            }
            if keep_receiver {
                self.live_receiver = Some(receiver);
            }
        }
    }

    fn start_live(&mut self, mode: SessionMode) {
        self.stop_live();
        let Some(model) = self.transcription_model.clone() else {
            self.error = Some("Transcription model is still loading.".to_string());
            return;
        };

        self.status = match mode {
            SessionMode::Raw => "Listening...".to_string(),
            SessionMode::Clean => "Listening clean...".to_string(),
            SessionMode::Preview => "Preview listening...".to_string(),
        };
        self.error = None;
        self.transcript.clear();
        self.draft.clear();
        self.session_buffer.clear();
        self.raw_preview_text.clear();
        self.active_mode = Some(mode);
        self.stop_in_progress = false;
        self.text_target = match mode {
            SessionMode::Preview => None,
            SessionMode::Raw | SessionMode::Clean => capture_text_target(),
        };

        let (tx, rx) = mpsc::channel();
        let stop_requested = Arc::new(AtomicBool::new(false));
        let worker_stop = Arc::clone(&stop_requested);
        let options = LiveTranscriptionOptions {
            model_alias: self.settings.transcription_model.clone(),
            language: self.settings.language.clone(),
            microphone_name: Some(self.settings.microphone_name.clone()),
            duration: None,
        };
        let worker = thread::spawn(move || {
            let runtime = tokio::runtime::Runtime::new()?;
            runtime.block_on(run_live_transcription_with_model(
                model.model(),
                options,
                tx,
                worker_stop,
            ))
        });

        self.live_receiver = Some(rx);
        self.stop_requested = Some(stop_requested);
        self.worker = Some(worker);
    }

    fn stop_live(&mut self) {
        if let Some(stop_requested) = &self.stop_requested {
            stop_requested.store(true, Ordering::Relaxed);
            self.stop_in_progress = true;
        }
        self.collect_finished_worker();
    }

    fn collect_finished_worker(&mut self) {
        if let Some(worker) = self.worker.take() {
            if worker.is_finished() {
                if let Err(err) = worker.join().unwrap_or_else(|_| {
                    anyhow::Result::Err(anyhow::anyhow!("live transcription worker panicked"))
                }) {
                    self.error = Some(format!("Live transcription stopped with error: {err}"));
                }
                self.stop_requested = None;
            } else {
                self.worker = Some(worker);
            }
        }
    }

    fn handle_final_text(&mut self, text: &str) {
        if text.is_empty() {
            return;
        }
        self.append_transcript(text);
        self.merge_session_buffer(text);
        self.draft.clear();
    }

    fn handle_draft_text(&mut self, text: &str) {
        self.draft = text.to_string();
        if self.active_mode == Some(SessionMode::Raw) && self.settings.streaming_commit {
            self.replace_raw_preview(text, true);
        }
    }

    fn finish_session(&mut self) {
        let mode = self.active_mode.take();
        match mode {
            Some(SessionMode::Raw) => {
                if self.settings.streaming_commit {
                    if let Some(final_text) =
                        raw_final_text(&self.session_buffer, &self.raw_preview_text)
                    {
                        if !self.replace_raw_preview_exact(&final_text) {
                            return;
                        }
                        self.submit_current_session();
                    }
                    self.raw_preview_text.clear();
                } else {
                    let raw = self.session_buffer.clone();
                    self.commit_text(&raw, true);
                }
                self.text_target = None;
                self.stop_in_progress = false;
                self.status = "Ready".to_string();
            }
            Some(SessionMode::Clean) => {
                if self.settings.post_processing_enabled {
                    self.start_clean_rewrite();
                } else {
                    let raw = self.session_buffer.clone();
                    self.commit_text(&raw, true);
                    self.text_target = None;
                    self.stop_in_progress = false;
                    self.status = "Ready".to_string();
                }
            }
            Some(SessionMode::Preview) => {
                self.text_target = None;
                self.stop_in_progress = false;
                self.status = "Preview stopped".to_string();
            }
            None => {}
        }
    }

    fn start_clean_rewrite(&mut self) {
        let Some(cleaner) = self.cleaner.clone() else {
            self.error = Some("Clean-mode model is not ready.".to_string());
            return;
        };
        let raw = self.session_buffer.clone();
        if raw.trim().is_empty() {
            self.status = "Ready".to_string();
            self.text_target = None;
            self.stop_in_progress = false;
            return;
        }
        let prompt = self.settings.active_prompt.clone();
        let sender = self.app_sender.clone();
        self.status = "Rewriting clean text...".to_string();
        self.is_finalizing = true;
        thread::spawn(move || {
            let runtime = match tokio::runtime::Runtime::new() {
                Ok(runtime) => runtime,
                Err(err) => {
                    let _ = sender.send(AppEvent::CleanerError(format!("{err}")));
                    return;
                }
            };
            let result = runtime.block_on(cleaner.rewrite(&raw, &prompt));
            match result {
                Ok(cleaned) => {
                    let _ = sender.send(AppEvent::Cleaned(cleaned));
                }
                Err(err) => {
                    let _ = sender.send(AppEvent::CleanerError(format!("{err}")));
                }
            }
        });
    }

    fn commit_text(&mut self, text: &str, submit_after: bool) {
        let output = self.apply_custom_substitutions(text);
        if output.trim().is_empty() {
            return;
        }
        if let Err(err) = type_text_to_target(&output, self.text_target) {
            self.error = Some(format!("Text output failed: {err}"));
            return;
        }
        if submit_after {
            self.submit_current_session();
        }
    }

    fn replace_raw_preview(&mut self, text: &str, keep_as_preview: bool) {
        let merged = merge_preview_fragment(&self.raw_preview_text, text);
        let output = self.apply_custom_substitutions(&merged);
        if output.trim().is_empty() {
            return;
        }
        if output == self.raw_preview_text {
            if !keep_as_preview {
                self.raw_preview_text.clear();
            }
            return;
        }

        let result = if self.raw_preview_text.is_empty() {
            type_text_to_target(&output, self.text_target)
        } else {
            replace_text_to_target(&self.raw_preview_text, &output, self.text_target)
        };

        match result {
            Ok(()) => {
                if keep_as_preview {
                    self.raw_preview_text = output;
                } else {
                    self.raw_preview_text.clear();
                }
            }
            Err(err) => {
                self.error = Some(format!("Live text preview failed: {err}"));
            }
        }
    }

    fn replace_raw_preview_exact(&mut self, text: &str) -> bool {
        let output = self.apply_custom_substitutions(text);
        if output.trim().is_empty() {
            return true;
        }
        if output == self.raw_preview_text {
            return true;
        }

        let result = if self.raw_preview_text.is_empty() {
            type_text_to_target(&output, self.text_target)
        } else {
            replace_text_to_target(&self.raw_preview_text, &output, self.text_target)
        };

        match result {
            Ok(()) => {
                self.raw_preview_text = output;
                true
            }
            Err(err) => {
                self.error = Some(format!("Final text replacement failed: {err}"));
                false
            }
        }
    }

    fn submit_current_session(&mut self) {
        if let Err(err) =
            press_auto_submit_key_to_target(&self.settings.auto_submit_key, self.text_target)
        {
            self.error = Some(format!("Auto-submit failed: {err}"));
        }
    }

    fn apply_custom_substitutions(&self, text: &str) -> String {
        let mut output = text.to_string();
        for rule in &self.settings.custom_substitutions {
            if !rule.match_text.trim().is_empty() {
                output = output.replace(&rule.match_text, &rule.replace_text);
            }
        }
        output
    }

    fn append_transcript(&mut self, text: &str) {
        if !self.transcript.is_empty() {
            self.transcript.push(' ');
        }
        self.transcript.push_str(text);
    }

    fn merge_session_buffer(&mut self, text: &str) {
        self.session_buffer = merge_preview_fragment(&self.session_buffer, text);
    }

    fn is_running(&self) -> bool {
        self.worker
            .as_ref()
            .map(|worker| !worker.is_finished())
            .unwrap_or(false)
    }

    fn should_show_overlay(&self) -> bool {
        self.error.is_some() || self.is_finalizing || (self.is_running() && !self.stop_in_progress)
    }

    fn overlay_payload(&self) -> OverlayPayload {
        if let Some(error) = &self.error {
            return OverlayPayload {
                kind: "error",
                title: "Error".to_string(),
                detail: error.clone(),
                hint: "Open Settings from the tray if this persists.".to_string(),
                mode: "raw",
                accent: "#c94040",
                intensity: 0.32,
            };
        }

        if self.is_finalizing {
            return OverlayPayload {
                kind: "finalizing",
                title: "Rewriting".to_string(),
                detail: "Clean mode is preparing final text.".to_string(),
                hint: "Stay focused in the target app.".to_string(),
                mode: "clean",
                accent: "#f59e0b",
                intensity: 0.72,
            };
        }

        let clean = self.active_mode == Some(SessionMode::Clean);
        OverlayPayload {
            kind: "listening",
            title: if clean {
                "Listening clean"
            } else {
                "Listening"
            }
            .to_string(),
            detail: if self.settings.partials_in_overlay && !self.draft.trim().is_empty() {
                self.draft.clone()
            } else {
                self.status.clone()
            },
            hint: if clean {
                "Release to rewrite into the focused app."
            } else {
                "Typing live into the focused app."
            }
            .to_string(),
            mode: if clean { "clean" } else { "raw" },
            accent: if clean { "#f59e0b" } else { "#9b7feb" },
            intensity: if self.draft.trim().is_empty() {
                0.58
            } else {
                0.96
            },
        }
    }

    fn settings_response(&self) -> SettingsResponse {
        let mut microphones = self.microphones.clone();
        if !microphones
            .iter()
            .any(|name| name == &self.settings.microphone_name)
        {
            microphones.push(self.settings.microphone_name.clone());
        }

        SettingsResponse {
            settings: self.settings.clone(),
            microphones,
            prompts: built_in_prompts()
                .iter()
                .map(|prompt| PromptPayload {
                    id: prompt.id,
                    name: prompt.name,
                    description: prompt.description,
                    prompt: prompt.prompt,
                })
                .collect(),
            language_options: include_current(
                &self.settings.language,
                &["en", "en-US", "es", "fr", "de", "ja", "ko", "zh"],
            ),
            transcription_model_options: include_current(
                &self.settings.transcription_model,
                &["nemotron-speech-streaming-en-0.6b"],
            ),
            post_processing_model_options: include_current(
                &self.settings.post_processing_model,
                &["qwen3-0.6b"],
            ),
            auto_submit_options: vec![
                "None".to_string(),
                "Enter".to_string(),
                "Ctrl+Enter".to_string(),
            ],
            model_unload_options: vec![
                "Never".to_string(),
                "After 2 min".to_string(),
                "After 5 min".to_string(),
                "After 15 min".to_string(),
            ],
        }
    }

    fn apply_settings(&mut self, settings: HushSettings) -> anyhow::Result<()> {
        let model_changed = settings.transcription_model != self.saved_settings.transcription_model
            || settings.post_processing_model != self.saved_settings.post_processing_model
            || settings.post_processing_enabled != self.saved_settings.post_processing_enabled;

        self.settings = settings;
        self.settings.save()?;
        self.saved_settings = self.settings.clone();

        if model_changed {
            self.is_model_ready = false;
            self.is_cleaning_model_ready = false;
            self.transcription_model = None;
            self.cleaner = None;
            self.status = "Reloading models...".to_string();
            self.preparing_detail = "Reloading Foundry Local models".to_string();
            self.preload_models();
        }

        Ok(())
    }

    fn refresh_microphones(&mut self) -> anyhow::Result<Vec<String>> {
        let mut devices = available_input_devices()?;
        if !devices
            .iter()
            .any(|name| name == &self.settings.microphone_name)
        {
            devices.push(self.settings.microphone_name.clone());
        }
        self.microphones = devices.clone();
        self.microphone_status = None;
        Ok(devices)
    }
}

#[tauri::command]
fn get_settings(state: tauri::State<'_, SharedController>) -> Result<SettingsResponse, String> {
    let controller = state.lock().map_err(|err| err.to_string())?;
    Ok(controller.settings_response())
}

#[tauri::command]
fn save_settings(
    settings: HushSettings,
    state: tauri::State<'_, SharedController>,
) -> Result<(), String> {
    let mut controller = state.lock().map_err(|err| err.to_string())?;
    controller
        .apply_settings(settings)
        .map_err(|err| err.to_string())
}

#[tauri::command]
fn refresh_microphones(state: tauri::State<'_, SharedController>) -> Result<Vec<String>, String> {
    let mut controller = state.lock().map_err(|err| err.to_string())?;
    controller
        .refresh_microphones()
        .map_err(|err| err.to_string())
}

#[tauri::command]
fn show_settings_window(app: AppHandle) -> Result<(), String> {
    show_settings(&app).map_err(|err| err.to_string())
}

#[tauri::command]
fn hide_settings_window(app: AppHandle) -> Result<(), String> {
    if let Some(window) = app.get_webview_window("settings") {
        window.hide().map_err(|err| err.to_string())?;
    }
    Ok(())
}

#[tauri::command]
fn start_preview(state: tauri::State<'_, SharedController>) -> Result<(), String> {
    let mut controller = state.lock().map_err(|err| err.to_string())?;
    controller.start_live(SessionMode::Preview);
    Ok(())
}

#[tauri::command]
fn stop_preview(state: tauri::State<'_, SharedController>) -> Result<(), String> {
    let mut controller = state.lock().map_err(|err| err.to_string())?;
    controller.stop_live();
    Ok(())
}

fn main() {
    let (app_tx, app_rx) = mpsc::channel();
    let (hotkey_tx, hotkey_rx) = mpsc::channel();
    let hotkey = GlobalHotkey::register_ctrl_h(hotkey_tx);
    let controller = Arc::new(Mutex::new(Controller::new(app_tx, hotkey)));

    tauri::Builder::default()
        .manage(controller.clone())
        .invoke_handler(tauri::generate_handler![
            get_settings,
            save_settings,
            refresh_microphones,
            show_settings_window,
            hide_settings_window,
            start_preview,
            stop_preview
        ])
        .setup(move |app| {
            create_windows(app)?;
            create_tray(app)?;
            if let Ok(controller) = controller.lock() {
                controller.preload_models();
            }
            spawn_event_loop(app.handle().clone(), controller.clone(), app_rx, hotkey_rx);
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("error while running Hush");
}

fn create_windows(app: &mut App) -> tauri::Result<()> {
    let overlay = WebviewWindowBuilder::new(app, "overlay", WebviewUrl::App("overlay.html".into()))
        .title("Hush")
        .inner_size(OVERLAY_WIDTH as f64, OVERLAY_HEIGHT as f64)
        .decorations(false)
        .shadow(false)
        .transparent(true)
        .always_on_top(true)
        .skip_taskbar(true)
        .resizable(false)
        .visible(false)
        .build()?;
    let _ = overlay.set_ignore_cursor_events(false);

    let settings =
        WebviewWindowBuilder::new(app, "settings", WebviewUrl::App("settings.html".into()))
            .title("Hush Settings")
            .inner_size(SETTINGS_WIDTH, SETTINGS_HEIGHT)
            .min_inner_size(480.0, 640.0)
            .resizable(true)
            .visible(false)
            .build()?;
    let settings_clone = settings.clone();
    settings.on_window_event(move |event| {
        if let WindowEvent::CloseRequested { api, .. } = event {
            api.prevent_close();
            let _ = settings_clone.hide();
        }
    });

    Ok(())
}

fn create_tray(app: &mut App) -> tauri::Result<()> {
    let settings_item = MenuItem::with_id(app, "settings", "Settings", true, None::<&str>)?;
    let quit_item = MenuItem::with_id(app, "quit", "Quit", true, None::<&str>)?;
    let menu = Menu::with_items(app, &[&settings_item, &quit_item])?;
    let icon = Image::from_bytes(include_bytes!(
        "../../../../dotnet/src/Hush.App/Assets/hush-icon.png"
    ))?;

    TrayIconBuilder::new()
        .icon(icon)
        .menu(&menu)
        .show_menu_on_left_click(false)
        .on_menu_event(|app, event| match event.id().as_ref() {
            "settings" => {
                let _ = show_settings(app);
            }
            "quit" => app.exit(0),
            _ => {}
        })
        .on_tray_icon_event(|tray, event| {
            if let TrayIconEvent::Click {
                button: MouseButton::Left,
                button_state: MouseButtonState::Up,
                ..
            } = event
            {
                let _ = show_settings(tray.app_handle());
            }
        })
        .build(app)?;

    Ok(())
}

fn show_settings(app: &AppHandle) -> tauri::Result<()> {
    if let Some(window) = app.get_webview_window("settings") {
        window.show()?;
        window.set_focus()?;
    }
    Ok(())
}

fn spawn_event_loop(
    app: AppHandle,
    controller: SharedController,
    app_rx: mpsc::Receiver<AppEvent>,
    hotkey_rx: mpsc::Receiver<HotkeyEvent>,
) {
    thread::spawn(move || loop {
        let (show_overlay, payload, was_visible) = {
            let Ok(mut controller) = controller.lock() else {
                thread::sleep(Duration::from_millis(50));
                continue;
            };
            controller.drain_app_events(&app_rx);
            controller.drain_live_events();
            controller.drain_hotkeys(&hotkey_rx);
            controller.collect_finished_worker();
            let show_overlay = controller.should_show_overlay();
            let payload = controller.overlay_payload();
            let was_visible = controller.overlay_was_visible;
            controller.overlay_was_visible = show_overlay;
            (show_overlay, payload, was_visible)
        };

        sync_overlay_window(&app, show_overlay, was_visible, &payload);
        thread::sleep(if show_overlay {
            Duration::from_millis(16)
        } else {
            Duration::from_millis(50)
        });
    });
}

fn sync_overlay_window(
    app: &AppHandle,
    show_overlay: bool,
    was_visible: bool,
    payload: &OverlayPayload,
) {
    let Some(window) = app.get_webview_window("overlay") else {
        return;
    };

    if show_overlay {
        let _ = position_overlay(&window);
        let _ = window.emit("overlay-state", payload);
        if !was_visible {
            let _ = window.show();
        }
    } else if was_visible {
        let _ = window.hide();
    }
}

fn position_overlay(window: &WebviewWindow) -> tauri::Result<()> {
    if let Some(monitor) = window.current_monitor()? {
        let size = monitor.size();
        let pos = monitor.position();
        let x = pos.x + ((size.width.saturating_sub(OVERLAY_WIDTH)) / 2) as i32;
        let y = pos.y + size.height.saturating_sub(OVERLAY_HEIGHT + 54) as i32;
        window.set_position(Position::Physical(PhysicalPosition { x, y }))?;
    }
    Ok(())
}

fn include_current(current: &str, defaults: &[&str]) -> Vec<String> {
    let mut values = defaults
        .iter()
        .map(|value| value.to_string())
        .collect::<Vec<_>>();
    if !current.trim().is_empty() && !values.iter().any(|value| value == current) {
        values.insert(0, current.to_string());
    }
    values
}

fn raw_final_text(session_buffer: &str, raw_preview_text: &str) -> Option<String> {
    let final_text = if session_buffer.trim().is_empty() {
        raw_preview_text.trim()
    } else {
        session_buffer.trim()
    };

    (!final_text.is_empty()).then(|| final_text.to_string())
}

fn merge_preview_fragment(current: &str, fragment: &str) -> String {
    let current = current.trim();
    let fragment = fragment.trim();

    if current.is_empty() {
        return fragment.to_string();
    }
    if fragment.is_empty()
        || current == fragment
        || current.ends_with(fragment)
        || normalized_text_contains(current, fragment)
    {
        return current.to_string();
    }
    if fragment.starts_with(current) {
        return fragment.to_string();
    }

    if let Some(byte_idx) = word_overlap_suffix_prefix_byte_index(current, fragment) {
        return append_word_suffix(current, &fragment[byte_idx..]);
    }

    if let Some(byte_idx) = overlap_suffix_prefix_byte_index(current, fragment) {
        return format!("{}{}", current, &fragment[byte_idx..]);
    }

    format!("{current} {fragment}")
}

fn append_word_suffix(current: &str, suffix: &str) -> String {
    let suffix = suffix.trim_start();
    if suffix.is_empty() {
        current.to_string()
    } else if suffix.starts_with(['.', ',', ';', ':', '!', '?']) {
        format!("{current}{suffix}")
    } else {
        format!("{current} {suffix}")
    }
}

fn normalized_text_contains(current: &str, fragment: &str) -> bool {
    let current = current.split_whitespace().collect::<Vec<_>>().join(" ");
    let fragment = fragment.split_whitespace().collect::<Vec<_>>().join(" ");
    !fragment.is_empty() && current.to_lowercase().contains(&fragment.to_lowercase())
}

fn word_overlap_suffix_prefix_byte_index(current: &str, fragment: &str) -> Option<usize> {
    let current_words = word_spans(current);
    let fragment_words = word_spans(fragment);
    let max_overlap = current_words.len().min(fragment_words.len()).min(8);

    for overlap in (1..=max_overlap).rev() {
        let current_tail = &current_words[(current_words.len() - overlap)..];
        let fragment_head = &fragment_words[..overlap];
        if current_tail
            .iter()
            .zip(fragment_head.iter())
            .all(|(left, right)| left.normalized == right.normalized)
        {
            return Some(skip_whitespace(fragment, fragment_head[overlap - 1].end));
        }
    }

    None
}

fn skip_whitespace(text: &str, mut byte_idx: usize) -> usize {
    while byte_idx < text.len() {
        let Some(ch) = text[byte_idx..].chars().next() else {
            break;
        };
        if !ch.is_whitespace() {
            break;
        }
        byte_idx += ch.len_utf8();
    }
    byte_idx
}

fn word_spans(text: &str) -> Vec<WordSpan> {
    let mut spans = Vec::new();
    let mut start = None;

    for (idx, ch) in text.char_indices() {
        if ch.is_alphanumeric() || ch == '\'' {
            start.get_or_insert(idx);
        } else if let Some(start_idx) = start.take() {
            spans.push(WordSpan::new(start_idx, idx, text));
        }
    }

    if let Some(start_idx) = start {
        spans.push(WordSpan::new(start_idx, text.len(), text));
    }

    spans
}

struct WordSpan {
    end: usize,
    normalized: String,
}

impl WordSpan {
    fn new(start: usize, end: usize, source: &str) -> Self {
        Self {
            end,
            normalized: source[start..end].to_lowercase(),
        }
    }
}

fn overlap_suffix_prefix_byte_index(current: &str, fragment: &str) -> Option<usize> {
    let mut best = None;
    for (byte_idx, _) in fragment.char_indices() {
        if byte_idx == 0 {
            continue;
        }

        let prefix = &fragment[..byte_idx];
        if prefix.chars().count() >= 3 && current.ends_with(prefix) {
            best = Some(byte_idx);
        }
    }

    if current.ends_with(fragment) {
        Some(fragment.len())
    } else {
        best
    }
}

#[cfg(test)]
mod tests {
    use super::{merge_preview_fragment, raw_final_text};

    #[test]
    fn raw_final_text_prefers_final_session_buffer_over_preview() {
        let preview =
            "This is a test of me talking into the thingy does it work well . It looks like it";
        let final_text =
            "This is a test of me talking into the thingy does it work well. It looks like it does";

        assert_eq!(
            raw_final_text(final_text, preview).as_deref(),
            Some(final_text)
        );
    }

    #[test]
    fn raw_final_text_falls_back_to_owned_preview_when_no_final_exists() {
        let preview = "This is a live preview";

        assert_eq!(raw_final_text("", preview).as_deref(), Some(preview));
    }

    #[test]
    fn preview_merge_extends_rolling_partials_without_repeating_overlap() {
        let first = "This is a test of me talking";
        let second = "me talking into the thingy";

        assert_eq!(
            merge_preview_fragment(first, second),
            "This is a test of me talking into the thingy"
        );
    }
}
