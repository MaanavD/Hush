// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

use std::ffi::c_void;

use anyhow::bail;

const K_CG_HID_EVENT_TAP: u32 = 0;
const K_VK_DELETE: u16 = 0x33;
const K_VK_RETURN: u16 = 0x24;
const K_VK_CONTROL: u16 = 0x3B;

#[link(name = "CoreGraphics", kind = "framework")]
extern "C" {
    fn CGEventCreateKeyboardEvent(
        source: *mut c_void,
        virtual_key: u16,
        key_down: bool,
    ) -> *mut c_void;
    fn CGEventKeyboardSetUnicodeString(
        event: *mut c_void,
        string_length: usize,
        unicode_string: *const u16,
    );
    fn CGEventPost(tap: u32, event: *mut c_void);
}

#[link(name = "CoreFoundation", kind = "framework")]
extern "C" {
    fn CFRelease(cf: *const c_void);
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TextTarget;

pub fn capture_text_target() -> Option<TextTarget> {
    None
}

pub fn type_text(text: &str) -> anyhow::Result<()> {
    type_text_to_target(text, None)
}

pub fn type_text_to_target(text: &str, _: Option<TextTarget>) -> anyhow::Result<()> {
    for unit in text.encode_utf16() {
        post_unicode_unit(unit)?;
    }
    Ok(())
}

pub fn replace_text_to_target(
    previous_text: &str,
    replacement_text: &str,
    target: Option<TextTarget>,
) -> anyhow::Result<()> {
    let (backspaces, suffix) = replacement_delta(previous_text, replacement_text);
    send_backspaces(backspaces)?;
    type_text_to_target(&suffix, target)
}

pub fn press_auto_submit_key(key: &str) -> anyhow::Result<()> {
    press_auto_submit_key_to_target(key, None)
}

pub fn press_auto_submit_key_to_target(key: &str, _: Option<TextTarget>) -> anyhow::Result<()> {
    match key {
        "" | "None" => Ok(()),
        "Enter" => post_key(K_VK_RETURN),
        "Ctrl+Enter" => unsafe {
            let ctrl_down = CGEventCreateKeyboardEvent(std::ptr::null_mut(), K_VK_CONTROL, true);
            CGEventPost(K_CG_HID_EVENT_TAP, ctrl_down);
            CFRelease(ctrl_down.cast_const());
            post_key(K_VK_RETURN)?;
            let ctrl_up = CGEventCreateKeyboardEvent(std::ptr::null_mut(), K_VK_CONTROL, false);
            CGEventPost(K_CG_HID_EVENT_TAP, ctrl_up);
            CFRelease(ctrl_up.cast_const());
            Ok(())
        },
        other => bail!("unsupported auto-submit key '{other}'"),
    }
}

fn post_unicode_unit(unit: u16) -> anyhow::Result<()> {
    unsafe {
        let down = CGEventCreateKeyboardEvent(std::ptr::null_mut(), 0, true);
        if down.is_null() {
            bail!("CGEventCreateKeyboardEvent failed; grant Accessibility permission to Hush");
        }
        CGEventKeyboardSetUnicodeString(down, 1, &unit);
        CGEventPost(K_CG_HID_EVENT_TAP, down);
        CFRelease(down.cast_const());

        let up = CGEventCreateKeyboardEvent(std::ptr::null_mut(), 0, false);
        if up.is_null() {
            bail!("CGEventCreateKeyboardEvent failed; grant Accessibility permission to Hush");
        }
        CGEventKeyboardSetUnicodeString(up, 1, &unit);
        CGEventPost(K_CG_HID_EVENT_TAP, up);
        CFRelease(up.cast_const());
    }
    Ok(())
}

fn post_key(key: u16) -> anyhow::Result<()> {
    unsafe {
        let down = CGEventCreateKeyboardEvent(std::ptr::null_mut(), key, true);
        if down.is_null() {
            bail!("CGEventCreateKeyboardEvent failed; grant Accessibility permission to Hush");
        }
        CGEventPost(K_CG_HID_EVENT_TAP, down);
        CFRelease(down.cast_const());
        let up = CGEventCreateKeyboardEvent(std::ptr::null_mut(), key, false);
        CGEventPost(K_CG_HID_EVENT_TAP, up);
        CFRelease(up.cast_const());
    }
    Ok(())
}

fn send_backspaces(count: usize) -> anyhow::Result<()> {
    for _ in 0..count {
        post_key(K_VK_DELETE)?;
    }
    Ok(())
}

fn common_prefix_chars(left: &str, right: &str) -> usize {
    left.chars()
        .zip(right.chars())
        .take_while(|(l, r)| l == r)
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
