// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

use std::ffi::{c_char, c_int, c_long, c_uint, c_ulong, c_void, CString};
use std::sync::{
    atomic::{AtomicBool, Ordering},
    mpsc, Arc,
};
use std::thread;
use std::time::Duration;

use anyhow::{bail, Context};

const KEY_PRESS: c_int = 2;
const KEY_RELEASE: c_int = 3;
const SHIFT_MASK: c_uint = 1;
const LOCK_MASK: c_uint = 2;
const CONTROL_MASK: c_uint = 4;
const MOD1_MASK: c_uint = 8;
const MOD2_MASK: c_uint = 16;
const MOD4_MASK: c_uint = 64;
const GRAB_MODE_ASYNC: c_int = 1;
const KEY_PRESS_MASK: c_long = 1;
const KEY_RELEASE_MASK: c_long = 2;

#[repr(C)]
#[derive(Clone, Copy)]
struct XEvent {
    event_type: c_int,
    serial: c_ulong,
    send_event: c_int,
    display: *mut c_void,
    window: c_ulong,
    root: c_ulong,
    subwindow: c_ulong,
    time: c_ulong,
    x: c_int,
    y: c_int,
    x_root: c_int,
    y_root: c_int,
    state: c_uint,
    keycode: c_uint,
    same_screen: c_int,
    padding: [c_long; 12],
}

#[link(name = "X11")]
extern "C" {
    fn XOpenDisplay(display_name: *const c_char) -> *mut c_void;
    fn XCloseDisplay(display: *mut c_void) -> c_int;
    fn XDefaultRootWindow(display: *mut c_void) -> c_ulong;
    fn XSelectInput(display: *mut c_void, window: c_ulong, event_mask: c_long) -> c_int;
    fn XGrabKey(
        display: *mut c_void,
        keycode: c_int,
        modifiers: c_uint,
        grab_window: c_ulong,
        owner_events: bool,
        pointer_mode: c_int,
        keyboard_mode: c_int,
    ) -> c_int;
    fn XUngrabKey(
        display: *mut c_void,
        keycode: c_int,
        modifiers: c_uint,
        grab_window: c_ulong,
    ) -> c_int;
    fn XNextEvent(display: *mut c_void, event_return: *mut XEvent) -> c_int;
    fn XPending(display: *mut c_void) -> c_int;
    fn XFlush(display: *mut c_void) -> c_int;
    fn XStringToKeysym(string: *const c_char) -> c_ulong;
    fn XKeysymToKeycode(display: *mut c_void, keysym: c_ulong) -> c_int;
}

#[derive(Debug, Clone)]
pub enum HotkeyEvent {
    RawPressed,
    RawReleased,
    CleanPressed,
    CleanReleased,
    Error(String),
}

pub struct GlobalHotkey {
    raw: GrabHandle,
    clean: GrabHandle,
}

impl GlobalHotkey {
    pub fn register_ctrl_h(events: mpsc::Sender<HotkeyEvent>) -> anyhow::Result<Self> {
        Ok(Self {
            raw: GrabHandle::spawn("Ctrl+H", false, events.clone())?,
            clean: GrabHandle::spawn("Ctrl+Alt+H", true, events)?,
        })
    }
}

struct GrabHandle {
    display: usize,
    root: c_ulong,
    keycode: c_int,
    mods: c_uint,
    stop: Arc<AtomicBool>,
    thread: Option<thread::JoinHandle<()>>,
}

impl GrabHandle {
    fn spawn(
        hotkey: &str,
        is_clean: bool,
        events: mpsc::Sender<HotkeyEvent>,
    ) -> anyhow::Result<Self> {
        if std::env::var_os("WAYLAND_DISPLAY").is_some() && std::env::var_os("DISPLAY").is_none() {
            bail!("Wayland-only Linux sessions are not supported for Rust Hush global hotkeys; use an X11 session or XWayland with DISPLAY set");
        }

        let display = unsafe { XOpenDisplay(std::ptr::null()) };
        if display.is_null() {
            bail!("could not open X11 display; ensure DISPLAY is set. Wayland-only sessions are not supported");
        }
        let root = unsafe { XDefaultRootWindow(display) };
        let (keycode, mods) = parse_hotkey(display, hotkey)?;
        unsafe {
            XSelectInput(display, root, KEY_PRESS_MASK | KEY_RELEASE_MASK);
            for extra in lock_variants() {
                XGrabKey(
                    display,
                    keycode,
                    mods | extra,
                    root,
                    false,
                    GRAB_MODE_ASYNC,
                    GRAB_MODE_ASYNC,
                );
            }
            XFlush(display);
        }

        let stop = Arc::new(AtomicBool::new(false));
        let stop_thread = Arc::clone(&stop);
        let display_value = display as usize;
        let thread = thread::Builder::new()
            .name(
                if is_clean {
                    "hush-rust-x11-clean-hotkey"
                } else {
                    "hush-rust-x11-hotkey"
                }
                .to_string(),
            )
            .spawn(move || poll_loop(display_value, keycode, mods, is_clean, events, stop_thread))
            .context("starting X11 hotkey poll thread")?;

        Ok(Self {
            display: display_value,
            root,
            keycode,
            mods,
            stop,
            thread: Some(thread),
        })
    }
}

impl Drop for GrabHandle {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
        let display = self.display as *mut c_void;
        if !display.is_null() {
            unsafe {
                for extra in lock_variants() {
                    XUngrabKey(display, self.keycode, self.mods | extra, self.root);
                }
                XCloseDisplay(display);
            }
        }
    }
}

fn poll_loop(
    display_value: usize,
    keycode: c_int,
    mods: c_uint,
    is_clean: bool,
    events: mpsc::Sender<HotkeyEvent>,
    stop: Arc<AtomicBool>,
) {
    let display = display_value as *mut c_void;
    while !stop.load(Ordering::Acquire) {
        if unsafe { XPending(display) } > 0 {
            let mut event = XEvent {
                event_type: 0,
                serial: 0,
                send_event: 0,
                display: std::ptr::null_mut(),
                window: 0,
                root: 0,
                subwindow: 0,
                time: 0,
                x: 0,
                y: 0,
                x_root: 0,
                y_root: 0,
                state: 0,
                keycode: 0,
                same_screen: 0,
                padding: [0; 12],
            };
            unsafe {
                XNextEvent(display, &mut event);
            }
            let clean_state = event.state & !(LOCK_MASK | MOD2_MASK);
            if event.event_type == KEY_PRESS
                && event.keycode == keycode as c_uint
                && clean_state == mods
            {
                let _ = events.send(if is_clean {
                    HotkeyEvent::CleanPressed
                } else {
                    HotkeyEvent::RawPressed
                });
            } else if event.event_type == KEY_RELEASE && event.keycode == keycode as c_uint {
                let _ = events.send(if is_clean {
                    HotkeyEvent::CleanReleased
                } else {
                    HotkeyEvent::RawReleased
                });
            }
        } else {
            thread::sleep(Duration::from_millis(8));
        }
    }
}

fn parse_hotkey(display: *mut c_void, hotkey: &str) -> anyhow::Result<(c_int, c_uint)> {
    let mut keycode = 0;
    let mut mods = 0;
    for part in hotkey.split('+').map(str::trim) {
        match part.to_ascii_lowercase().as_str() {
            "ctrl" | "control" => mods |= CONTROL_MASK,
            "shift" => mods |= SHIFT_MASK,
            "alt" => mods |= MOD1_MASK,
            "super" | "win" => mods |= MOD4_MASK,
            key => {
                let xname = match key {
                    "return" => "Return",
                    "escape" | "esc" => "Escape",
                    "space" => "space",
                    other => other,
                };
                let c = CString::new(xname)?;
                let keysym = unsafe { XStringToKeysym(c.as_ptr()) };
                if keysym != 0 {
                    keycode = unsafe { XKeysymToKeycode(display, keysym) };
                }
            }
        }
    }

    if keycode == 0 {
        bail!("could not parse Linux hotkey '{hotkey}'");
    }
    Ok((keycode, mods))
}

fn lock_variants() -> [c_uint; 4] {
    [0, LOCK_MASK, MOD2_MASK, LOCK_MASK | MOD2_MASK]
}
