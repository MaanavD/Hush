// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

#[cfg(windows)]
mod windows_hotkey;
#[cfg(windows)]
mod windows_input_coordinator;
#[cfg(windows)]
mod windows_text_output;

#[cfg(windows)]
pub use windows_hotkey::{GlobalHotkey, HotkeyEvent};
#[cfg(windows)]
pub use windows_text_output::{
    capture_text_target, press_auto_submit_key, press_auto_submit_key_to_target,
    replace_text_to_target, type_text, type_text_to_target, TextTarget,
};

#[cfg(not(windows))]
#[derive(Debug, Clone)]
pub enum HotkeyEvent {
    RawPressed,
    RawReleased,
    CleanPressed,
    CleanReleased,
    Error(String),
}

#[cfg(not(windows))]
pub struct GlobalHotkey;

#[cfg(not(windows))]
impl GlobalHotkey {
    pub fn register_ctrl_h(_: std::sync::mpsc::Sender<HotkeyEvent>) -> anyhow::Result<Self> {
        anyhow::bail!("global hotkeys are currently implemented only on Windows")
    }
}

#[cfg(not(windows))]
pub fn type_text(_: &str) -> anyhow::Result<()> {
    anyhow::bail!("text injection is currently implemented only on Windows")
}

#[cfg(not(windows))]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TextTarget;

#[cfg(not(windows))]
pub fn capture_text_target() -> Option<TextTarget> {
    None
}

#[cfg(not(windows))]
pub fn type_text_to_target(_: &str, _: Option<TextTarget>) -> anyhow::Result<()> {
    anyhow::bail!("text injection is currently implemented only on Windows")
}

#[cfg(not(windows))]
pub fn replace_text_to_target(_: &str, _: &str, _: Option<TextTarget>) -> anyhow::Result<()> {
    anyhow::bail!("text replacement is currently implemented only on Windows")
}

#[cfg(not(windows))]
pub fn press_auto_submit_key(_: &str) -> anyhow::Result<()> {
    anyhow::bail!("auto-submit key injection is currently implemented only on Windows")
}

#[cfg(not(windows))]
pub fn press_auto_submit_key_to_target(_: &str, _: Option<TextTarget>) -> anyhow::Result<()> {
    anyhow::bail!("auto-submit key injection is currently implemented only on Windows")
}
