// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

use std::{ffi::c_void, thread, time::Duration};

use anyhow::{bail, Context};
use windows::Win32::Foundation::HWND;
use windows::Win32::System::Threading::{AttachThreadInput, GetCurrentThreadId};
use windows::Win32::UI::Input::KeyboardAndMouse::{
    GetAsyncKeyState, SendInput, INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT, KEYBD_EVENT_FLAGS,
    KEYEVENTF_KEYUP, KEYEVENTF_SCANCODE, KEYEVENTF_UNICODE, VIRTUAL_KEY, VK_BACK, VK_CONTROL,
    VK_LCONTROL, VK_LMENU, VK_LSHIFT, VK_LWIN, VK_RCONTROL, VK_RETURN,
};
use windows::Win32::UI::WindowsAndMessaging::{
    BringWindowToTop, GetForegroundWindow, GetWindowThreadProcessId, IsIconic, IsWindow,
    SetForegroundWindow, ShowWindow, SW_RESTORE,
};

use super::windows_input_coordinator::suppress_hotkey_release_detection;

const VK_RSHIFT_KEY: VIRTUAL_KEY = VIRTUAL_KEY(0xA1);
const VK_RMENU_KEY: VIRTUAL_KEY = VIRTUAL_KEY(0xA5);
const VK_RWIN_KEY: VIRTUAL_KEY = VIRTUAL_KEY(0x5C);
const VK_H_KEY: VIRTUAL_KEY = VIRTUAL_KEY(0x48);
const BACKSPACE_SCAN_CODE: u16 = 0x0E;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TextTarget {
    hwnd: isize,
}

pub fn capture_text_target() -> Option<TextTarget> {
    let hwnd = unsafe { GetForegroundWindow() };
    (!hwnd.is_invalid()).then_some(TextTarget {
        hwnd: hwnd.0 as isize,
    })
}

pub fn type_text(text: &str) -> anyhow::Result<()> {
    type_text_to_target(text, None)
}

pub fn type_text_to_target(text: &str, target: Option<TextTarget>) -> anyhow::Result<()> {
    if text.is_empty() {
        return Ok(());
    }

    ensure_target_foreground(target).context("restoring target text window")?;
    let _suppression = suppress_hotkey_release_detection();
    release_hotkey_character_key()?;
    let modifiers = release_pressed_modifiers()?;
    let result = send_unicode_text(text);
    restore_modifiers(&modifiers)?;
    result
}

pub fn replace_text_to_target(
    previous_text: &str,
    replacement_text: &str,
    target: Option<TextTarget>,
) -> anyhow::Result<()> {
    if previous_text == replacement_text {
        return Ok(());
    }

    ensure_target_foreground(target).context("restoring target text window")?;
    let (backspace_count, suffix) = replacement_delta(previous_text, replacement_text);

    let _suppression = suppress_hotkey_release_detection();
    release_hotkey_character_key()?;
    let modifiers = release_pressed_modifiers()?;
    let result = send_backspaces(backspace_count).and_then(|_| send_unicode_text(&suffix));
    restore_modifiers(&modifiers)?;
    result
}

fn send_unicode_text(text: &str) -> anyhow::Result<()> {
    for unit in text.encode_utf16() {
        let inputs = [
            unicode_input(unit, KEYEVENTF_UNICODE),
            unicode_input(unit, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP),
        ];
        let sent = unsafe { SendInput(&inputs, std::mem::size_of::<INPUT>() as i32) };
        if sent != inputs.len() as u32 {
            bail!(
                "SendInput failed while typing dictated text (sent {sent}/{}, Win32={:?})",
                inputs.len(),
                windows::core::Error::from_win32()
            );
        }
    }

    Ok(())
}

pub fn press_auto_submit_key(key: &str) -> anyhow::Result<()> {
    press_auto_submit_key_to_target(key, None)
}

pub fn press_auto_submit_key_to_target(
    key: &str,
    target: Option<TextTarget>,
) -> anyhow::Result<()> {
    ensure_target_foreground(target).context("restoring target text window for auto-submit")?;
    let _suppression = suppress_hotkey_release_detection();
    release_hotkey_character_key()?;
    let modifiers = release_pressed_modifiers()?;

    let result = match key {
        "" | "None" => Ok(()),
        "Enter" => send_virtual_keys(&[
            key_input(VK_RETURN, KEYBD_EVENT_FLAGS(0)),
            key_input(VK_RETURN, KEYEVENTF_KEYUP),
        ]),
        "Ctrl+Enter" => send_virtual_keys(&[
            key_input(VK_CONTROL, KEYBD_EVENT_FLAGS(0)),
            key_input(VK_RETURN, KEYBD_EVENT_FLAGS(0)),
            key_input(VK_RETURN, KEYEVENTF_KEYUP),
            key_input(VK_CONTROL, KEYEVENTF_KEYUP),
        ]),
        other => bail!("unsupported auto-submit key '{other}'"),
    };

    restore_modifiers(&modifiers)?;
    result
}

fn ensure_target_foreground(target: Option<TextTarget>) -> anyhow::Result<()> {
    let Some(target) = target else {
        return Ok(());
    };

    let hwnd = target.hwnd();
    if hwnd.is_invalid() {
        return Ok(());
    }

    let foreground = unsafe { GetForegroundWindow() };
    if foreground == hwnd {
        return Ok(());
    }

    if !unsafe { IsWindow(Some(hwnd)).as_bool() } {
        bail!("target text window is no longer available");
    }

    let current_thread = unsafe { GetCurrentThreadId() };
    let foreground_thread = if foreground.is_invalid() {
        0
    } else {
        unsafe { GetWindowThreadProcessId(foreground, None) }
    };
    let target_thread = unsafe { GetWindowThreadProcessId(hwnd, None) };
    if target_thread == 0 {
        bail!("target text window has no input thread");
    }

    let attached_foreground = foreground_thread != 0
        && foreground_thread != current_thread
        && unsafe { AttachThreadInput(current_thread, foreground_thread, true).as_bool() };
    let attached_target = target_thread != current_thread
        && target_thread != foreground_thread
        && unsafe { AttachThreadInput(current_thread, target_thread, true).as_bool() };

    let focus_result = try_restore_foreground(hwnd);

    if attached_target {
        unsafe {
            let _ = AttachThreadInput(current_thread, target_thread, false);
        }
    }
    if attached_foreground {
        unsafe {
            let _ = AttachThreadInput(current_thread, foreground_thread, false);
        }
    }

    focus_result
}

fn try_restore_foreground(hwnd: HWND) -> anyhow::Result<()> {
    unsafe {
        if IsIconic(hwnd).as_bool() {
            let _ = ShowWindow(hwnd, SW_RESTORE);
        }
        let _ = BringWindowToTop(hwnd);
        let _ = SetForegroundWindow(hwnd);
    }

    for _ in 0..5 {
        if unsafe { GetForegroundWindow() } == hwnd {
            return Ok(());
        }
        thread::sleep(Duration::from_millis(10));
    }

    bail!("could not restore target text window before typing")
}

impl TextTarget {
    fn hwnd(self) -> HWND {
        HWND(self.hwnd as *mut c_void)
    }
}

fn send_virtual_keys(inputs: &[INPUT]) -> anyhow::Result<()> {
    let sent = unsafe { SendInput(inputs, std::mem::size_of::<INPUT>() as i32) };
    if sent != inputs.len() as u32 {
        bail!(
            "SendInput failed while pressing auto-submit key (sent {sent}/{}, Win32={:?})",
            inputs.len(),
            windows::core::Error::from_win32()
        );
    }

    Ok(())
}

fn send_backspaces(count: usize) -> anyhow::Result<()> {
    for _ in 0..count {
        let inputs = [
            backspace_input(KEYBD_EVENT_FLAGS(0)),
            backspace_input(KEYEVENTF_KEYUP),
        ];
        send_virtual_keys(&inputs)?;
    }

    Ok(())
}

fn release_pressed_modifiers() -> anyhow::Result<Vec<VIRTUAL_KEY>> {
    let modifiers = pressed_modifier_keys();
    if modifiers.is_empty() {
        return Ok(modifiers);
    }

    let inputs = modifiers
        .iter()
        .map(|key| key_input(*key, KEYEVENTF_KEYUP))
        .collect::<Vec<_>>();
    send_virtual_keys(&inputs)?;
    Ok(modifiers)
}

fn release_hotkey_character_key() -> anyhow::Result<()> {
    if unsafe { (GetAsyncKeyState(VK_H_KEY.0 as i32) & 0x8000u16 as i16) != 0 } {
        send_virtual_keys(&[key_input(VK_H_KEY, KEYEVENTF_KEYUP)])?;
    }
    Ok(())
}

fn restore_modifiers(modifiers: &[VIRTUAL_KEY]) -> anyhow::Result<()> {
    if modifiers.is_empty() {
        return Ok(());
    }

    let inputs = modifiers
        .iter()
        .map(|key| key_input(*key, KEYBD_EVENT_FLAGS(0)))
        .collect::<Vec<_>>();
    send_virtual_keys(&inputs)
}

fn pressed_modifier_keys() -> Vec<VIRTUAL_KEY> {
    [
        VK_LSHIFT,
        VK_RSHIFT_KEY,
        VK_LCONTROL,
        VK_RCONTROL,
        VK_LMENU,
        VK_RMENU_KEY,
        VK_LWIN,
        VK_RWIN_KEY,
    ]
    .into_iter()
    .filter(|key| unsafe { (GetAsyncKeyState(key.0 as i32) & 0x8000u16 as i16) != 0 })
    .collect()
}

fn common_prefix_chars(left: &str, right: &str) -> usize {
    left.chars()
        .zip(right.chars())
        .take_while(|(left, right)| left == right)
        .count()
}

fn replacement_delta(previous_text: &str, replacement_text: &str) -> (usize, String) {
    let common_prefix = common_prefix_chars(previous_text, replacement_text);
    let backspace_count = previous_text.chars().count().saturating_sub(common_prefix);
    let suffix = replacement_text
        .chars()
        .skip(common_prefix)
        .collect::<String>();
    (backspace_count, suffix)
}

fn unicode_input(scan: u16, flags: KEYBD_EVENT_FLAGS) -> INPUT {
    INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT {
                wVk: VIRTUAL_KEY(0),
                wScan: scan,
                dwFlags: flags,
                time: 0,
                dwExtraInfo: 0,
            },
        },
    }
}

fn backspace_input(flags: KEYBD_EVENT_FLAGS) -> INPUT {
    INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT {
                wVk: VK_BACK,
                wScan: BACKSPACE_SCAN_CODE,
                dwFlags: KEYEVENTF_SCANCODE | flags,
                time: 0,
                dwExtraInfo: 0,
            },
        },
    }
}

fn key_input(key: VIRTUAL_KEY, flags: KEYBD_EVENT_FLAGS) -> INPUT {
    INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT {
                wVk: key,
                wScan: 0,
                dwFlags: flags,
                time: 0,
                dwExtraInfo: 0,
            },
        },
    }
}

#[cfg(test)]
mod tests {
    use super::replacement_delta;

    #[test]
    fn replacement_delta_only_backspaces_previous_hush_owned_text() {
        let previous =
            "This is a test of me talking into the thingy does it work well . It looks like it";
        let replacement =
            "This is a test of me talking into the thingy does it work well. It looks like it does";

        let (backspaces, suffix) = replacement_delta(previous, replacement);

        assert!(backspaces <= previous.chars().count());
        assert_eq!(suffix, ". It looks like it does");
    }
}
