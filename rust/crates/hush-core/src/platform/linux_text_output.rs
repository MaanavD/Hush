// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

use std::ffi::{c_char, c_int, c_uint, c_ulong, c_void, CString};

use anyhow::{bail, Context};

const XK_BACKSPACE: c_ulong = 0xFF08;

#[link(name = "X11")]
extern "C" {
    fn XOpenDisplay(display_name: *const c_char) -> *mut c_void;
    fn XCloseDisplay(display: *mut c_void) -> c_int;
    fn XSync(display: *mut c_void, discard: bool) -> c_int;
    fn XFlush(display: *mut c_void) -> c_int;
    fn XDisplayKeycodes(
        display: *mut c_void,
        min_keycodes_return: *mut c_int,
        max_keycodes_return: *mut c_int,
    ) -> c_int;
    fn XChangeKeyboardMapping(
        display: *mut c_void,
        first_keycode: c_int,
        keysyms_per_keycode: c_int,
        keysyms: *const c_ulong,
        num_codes: c_int,
    ) -> c_int;
    fn XStringToKeysym(string: *const c_char) -> c_ulong;
    fn XKeysymToKeycode(display: *mut c_void, keysym: c_ulong) -> c_int;
}

#[link(name = "Xtst")]
extern "C" {
    fn XTestFakeKeyEvent(
        display: *mut c_void,
        keycode: c_uint,
        is_press: bool,
        delay: c_ulong,
    ) -> c_int;
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
    if text.is_empty() {
        return Ok(());
    }
    with_display(|display, scratch| {
        for ch in text.chars() {
            let keysym = 0x0100_0000u64 | ch as u32 as u64;
            fake_mapped_key(display, scratch, keysym)?;
        }
        Ok(())
    })
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
        "Enter" => fake_named_key("Return"),
        "Ctrl+Enter" => with_display(|display, scratch| {
            let ctrl = keycode_for(display, "Control_L")?;
            let ret = keycode_for(display, "Return")?;
            unsafe {
                XTestFakeKeyEvent(display, ctrl as c_uint, true, 0);
                XTestFakeKeyEvent(display, ret as c_uint, true, 0);
                XTestFakeKeyEvent(display, ret as c_uint, false, 0);
                XTestFakeKeyEvent(display, ctrl as c_uint, false, 0);
                XFlush(display);
            }
            let _ = scratch;
            Ok(())
        }),
        other => bail!("unsupported auto-submit key '{other}'"),
    }
}

fn send_backspaces(count: usize) -> anyhow::Result<()> {
    with_display(|display, scratch| {
        for _ in 0..count {
            fake_mapped_key(display, scratch, XK_BACKSPACE)?;
        }
        Ok(())
    })
}

fn fake_named_key(name: &str) -> anyhow::Result<()> {
    with_display(|display, _| {
        let keycode = keycode_for(display, name)?;
        unsafe {
            XTestFakeKeyEvent(display, keycode as c_uint, true, 0);
            XTestFakeKeyEvent(display, keycode as c_uint, false, 0);
            XFlush(display);
        }
        Ok(())
    })
}

fn with_display<T>(f: impl FnOnce(*mut c_void, c_int) -> anyhow::Result<T>) -> anyhow::Result<T> {
    if std::env::var_os("WAYLAND_DISPLAY").is_some() && std::env::var_os("DISPLAY").is_none() {
        bail!("Wayland-only Linux sessions are not supported for Rust Hush text output; use an X11 session or XWayland with DISPLAY set");
    }
    let display = unsafe { XOpenDisplay(std::ptr::null()) };
    if display.is_null() {
        bail!("cannot connect to X11 display; ensure DISPLAY is set");
    }
    let mut min_keycode = 0;
    let mut max_keycode = 0;
    unsafe {
        XDisplayKeycodes(display, &mut min_keycode, &mut max_keycode);
    }
    let scratch = max_keycode;
    let result = f(display, scratch);
    unsafe {
        let clear = [0 as c_ulong, 0 as c_ulong];
        XChangeKeyboardMapping(display, scratch, 2, clear.as_ptr(), 1);
        XSync(display, false);
        XCloseDisplay(display);
    }
    result
}

fn fake_mapped_key(display: *mut c_void, scratch: c_int, keysym: c_ulong) -> anyhow::Result<()> {
    let mapping = [keysym, keysym];
    unsafe {
        XChangeKeyboardMapping(display, scratch, 2, mapping.as_ptr(), 1);
        XSync(display, false);
        XTestFakeKeyEvent(display, scratch as c_uint, true, 0);
        XTestFakeKeyEvent(display, scratch as c_uint, false, 0);
        XFlush(display);
    }
    Ok(())
}

fn keycode_for(display: *mut c_void, name: &str) -> anyhow::Result<c_int> {
    let c = CString::new(name).context("building X11 keysym name")?;
    let keysym = unsafe { XStringToKeysym(c.as_ptr()) };
    if keysym == 0 {
        bail!("unknown X11 keysym '{name}'");
    }
    let keycode = unsafe { XKeysymToKeycode(display, keysym) };
    if keycode == 0 {
        bail!("no keycode for X11 keysym '{name}'");
    }
    Ok(keycode)
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
