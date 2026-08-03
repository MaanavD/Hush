// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

#[cfg(windows)]
mod windows_hotkey;
#[cfg(windows)]
mod windows_input_coordinator;
#[cfg(windows)]
mod windows_text_output;

#[cfg(target_os = "macos")]
mod macos_hotkey;
#[cfg(target_os = "macos")]
mod macos_text_output;

#[cfg(target_os = "linux")]
mod linux_hotkey;
#[cfg(target_os = "linux")]
mod linux_text_output;

#[cfg(windows)]
pub use windows_hotkey::{GlobalHotkey, HotkeyEvent};
#[cfg(windows)]
pub use windows_text_output::{
    capture_text_target, press_auto_submit_key, press_auto_submit_key_to_target,
    replace_text_to_target, type_text, type_text_to_target, TextTarget,
};

#[cfg(target_os = "macos")]
pub use macos_hotkey::{GlobalHotkey, HotkeyEvent};
#[cfg(target_os = "macos")]
pub use macos_text_output::{
    capture_text_target, press_auto_submit_key, press_auto_submit_key_to_target,
    replace_text_to_target, type_text, type_text_to_target, TextTarget,
};

#[cfg(target_os = "linux")]
pub use linux_hotkey::{GlobalHotkey, HotkeyEvent};
#[cfg(target_os = "linux")]
pub use linux_text_output::{
    capture_text_target, press_auto_submit_key, press_auto_submit_key_to_target,
    replace_text_to_target, type_text, type_text_to_target, TextTarget,
};

#[cfg(not(any(windows, target_os = "macos", target_os = "linux")))]
#[derive(Debug, Clone)]
pub enum HotkeyEvent {
    RawPressed,
    RawReleased,
    CleanPressed,
    CleanReleased,
    Error(String),
}

#[cfg(not(any(windows, target_os = "macos", target_os = "linux")))]
pub struct GlobalHotkey;

#[cfg(not(any(windows, target_os = "macos", target_os = "linux")))]
impl GlobalHotkey {
    pub fn register_ctrl_h(_: std::sync::mpsc::Sender<HotkeyEvent>) -> anyhow::Result<Self> {
        anyhow::bail!("global hotkeys are currently implemented only on Windows")
    }
}

#[cfg(not(any(windows, target_os = "macos", target_os = "linux")))]
pub fn type_text(_: &str) -> anyhow::Result<()> {
    anyhow::bail!("text injection is not implemented on this platform")
}

#[cfg(not(any(windows, target_os = "macos", target_os = "linux")))]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TextTarget;

#[cfg(not(any(windows, target_os = "macos", target_os = "linux")))]
pub fn capture_text_target() -> Option<TextTarget> {
    None
}

#[cfg(not(any(windows, target_os = "macos", target_os = "linux")))]
pub fn type_text_to_target(_: &str, _: Option<TextTarget>) -> anyhow::Result<()> {
    anyhow::bail!("text injection is not implemented on this platform")
}

#[cfg(not(any(windows, target_os = "macos", target_os = "linux")))]
pub fn replace_text_to_target(_: &str, _: &str, _: Option<TextTarget>) -> anyhow::Result<()> {
    anyhow::bail!("text replacement is not implemented on this platform")
}

#[cfg(not(any(windows, target_os = "macos", target_os = "linux")))]
pub fn press_auto_submit_key(_: &str) -> anyhow::Result<()> {
    anyhow::bail!("auto-submit key injection is not implemented on this platform")
}

#[cfg(not(any(windows, target_os = "macos", target_os = "linux")))]
pub fn press_auto_submit_key_to_target(_: &str, _: Option<TextTarget>) -> anyhow::Result<()> {
    anyhow::bail!("auto-submit key injection is not implemented on this platform")
}
