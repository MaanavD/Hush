// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

use std::collections::HashMap;
use std::ffi::c_void;
use std::sync::{mpsc, OnceLock};
use std::thread;

use anyhow::{bail, Context};

const K_CG_SESSION_EVENT_TAP: u32 = 1;
const K_CG_HEAD_INSERT_EVENT_TAP: u32 = 0;
const K_CG_EVENT_TAP_OPTION_LISTEN_ONLY: u32 = 1;
const K_CG_EVENT_KEY_DOWN: u32 = 10;
const K_CG_EVENT_KEY_UP: u32 = 11;
const K_CG_EVENT_FLAGS_CHANGED: u32 = 12;
const K_CG_KEYBOARD_EVENT_VIRTUAL_KEY: u32 = 9;
const K_MASK_SHIFT: u64 = 0x0002_0000;
const K_MASK_CONTROL: u64 = 0x0004_0000;
const K_MASK_OPTION: u64 = 0x0008_0000;
const K_MASK_COMMAND: u64 = 0x0010_0000;

type CGEventRef = *mut c_void;
type CFMachPortRef = *mut c_void;
type CFRunLoopRef = *mut c_void;
type CFRunLoopSourceRef = *mut c_void;
type CFStringRef = *const c_void;

#[link(name = "CoreGraphics", kind = "framework")]
extern "C" {
    fn CGEventTapCreate(
        tap: u32,
        place: u32,
        options: u32,
        events_of_interest: u64,
        callback: extern "C" fn(*mut c_void, u32, CGEventRef, *mut c_void) -> CGEventRef,
        user_info: *mut c_void,
    ) -> CFMachPortRef;
    fn CGEventGetIntegerValueField(event: CGEventRef, field: u32) -> i64;
    fn CGEventGetFlags(event: CGEventRef) -> u64;
    fn CGEventTapEnable(tap: CFMachPortRef, enable: bool);
}

#[link(name = "CoreFoundation", kind = "framework")]
extern "C" {
    static kCFRunLoopDefaultMode: CFStringRef;
    fn CFMachPortCreateRunLoopSource(
        allocator: *const c_void,
        port: CFMachPortRef,
        order: isize,
    ) -> CFRunLoopSourceRef;
    fn CFRunLoopGetCurrent() -> CFRunLoopRef;
    fn CFRunLoopAddSource(rl: CFRunLoopRef, source: CFRunLoopSourceRef, mode: CFStringRef);
    fn CFRunLoopRun();
    fn CFRunLoopStop(rl: CFRunLoopRef);
    fn CFRelease(cf: *const c_void);
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
    raw: TapHandle,
    clean: TapHandle,
}

impl GlobalHotkey {
    pub fn register_ctrl_h(events: mpsc::Sender<HotkeyEvent>) -> anyhow::Result<Self> {
        Ok(Self {
            raw: TapHandle::spawn("Ctrl+H", false, events.clone())?,
            clean: TapHandle::spawn("Ctrl+Alt+H", true, events)?,
        })
    }
}

struct TapHandle {
    run_loop: *mut c_void,
    mach_port: *mut c_void,
    thread: Option<thread::JoinHandle<()>>,
    state: *mut TapState,
}

impl TapHandle {
    fn spawn(
        hotkey: &str,
        is_clean: bool,
        events: mpsc::Sender<HotkeyEvent>,
    ) -> anyhow::Result<Self> {
        let (vk, mods) = parse_hotkey(hotkey)?;
        let state = Box::into_raw(Box::new(TapState {
            events,
            vk,
            mods,
            is_clean,
            held: false,
            run_loop: std::ptr::null_mut(),
            mach_port: std::ptr::null_mut(),
        }));
        let (ready_tx, ready_rx) = mpsc::channel();
        let state_value = state as usize;

        let thread = thread::Builder::new()
            .name(if is_clean {
                "hush-rust-macos-clean-hotkey"
            } else {
                "hush-rust-macos-hotkey"
            }            .to_string())
            .spawn(move || unsafe {
                let state = state_value as *mut TapState;
                let mask = (1u64 << K_CG_EVENT_KEY_DOWN)
                    | (1u64 << K_CG_EVENT_KEY_UP)
                    | (1u64 << K_CG_EVENT_FLAGS_CHANGED);
                let tap = CGEventTapCreate(
                    K_CG_SESSION_EVENT_TAP,
                    K_CG_HEAD_INSERT_EVENT_TAP,
                    K_CG_EVENT_TAP_OPTION_LISTEN_ONLY,
                    mask,
                    tap_callback,
                    state.cast(),
                );
                if tap.is_null() {
                    let _ = ready_tx.send(Err(
                        "macOS Accessibility permission is required for Hush hotkeys. Enable Hush in System Settings > Privacy & Security > Accessibility, then restart.".to_string(),
                    ));
                    return;
                }

                let run_loop = CFRunLoopGetCurrent();
                (*state).run_loop = run_loop;
                (*state).mach_port = tap;
                let source = CFMachPortCreateRunLoopSource(std::ptr::null(), tap, 0);
                CFRunLoopAddSource(run_loop, source, kCFRunLoopDefaultMode);
                CFRelease(source.cast_const());
                CGEventTapEnable(tap, true);
                let _ = ready_tx.send(Ok((run_loop as usize, tap as usize)));
                CFRunLoopRun();
            })
            .context("starting macOS hotkey event tap thread")?;

        let (run_loop, mach_port) = ready_rx
            .recv()
            .context("waiting for macOS hotkey registration")?
            .map_err(|message| anyhow::anyhow!(message))?;

        Ok(Self {
            run_loop: run_loop as *mut c_void,
            mach_port: mach_port as *mut c_void,
            thread: Some(thread),
            state,
        })
    }
}

unsafe impl Send for TapHandle {}
unsafe impl Send for GlobalHotkey {}

impl Drop for TapHandle {
    fn drop(&mut self) {
        unsafe {
            if !self.run_loop.is_null() {
                CFRunLoopStop(self.run_loop);
            }
            if !self.mach_port.is_null() {
                CGEventTapEnable(self.mach_port, false);
                CFRelease(self.mach_port.cast_const());
            }
        }
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
        if !self.state.is_null() {
            unsafe {
                drop(Box::from_raw(self.state));
            }
        }
    }
}

struct TapState {
    events: mpsc::Sender<HotkeyEvent>,
    vk: u16,
    mods: u64,
    is_clean: bool,
    held: bool,
    run_loop: CFRunLoopRef,
    mach_port: CFMachPortRef,
}

extern "C" fn tap_callback(
    _: *mut c_void,
    event_type: u32,
    event: CGEventRef,
    user_info: *mut c_void,
) -> CGEventRef {
    if event.is_null() || user_info.is_null() {
        return event;
    }
    let state = unsafe { &mut *(user_info as *mut TapState) };
    let vk = unsafe { CGEventGetIntegerValueField(event, K_CG_KEYBOARD_EVENT_VIRTUAL_KEY) as u16 };
    let flags = unsafe { CGEventGetFlags(event) }
        & (K_MASK_SHIFT | K_MASK_CONTROL | K_MASK_OPTION | K_MASK_COMMAND);

    match event_type {
        K_CG_EVENT_KEY_DOWN if vk == state.vk && flags == state.mods && !state.held => {
            state.held = true;
            let _ = state.events.send(if state.is_clean {
                HotkeyEvent::CleanPressed
            } else {
                HotkeyEvent::RawPressed
            });
        }
        K_CG_EVENT_KEY_UP if vk == state.vk && state.held => {
            state.held = false;
            let _ = state.events.send(if state.is_clean {
                HotkeyEvent::CleanReleased
            } else {
                HotkeyEvent::RawReleased
            });
        }
        K_CG_EVENT_FLAGS_CHANGED if state.held && (flags & state.mods) != state.mods => {
            state.held = false;
            let _ = state.events.send(if state.is_clean {
                HotkeyEvent::CleanReleased
            } else {
                HotkeyEvent::RawReleased
            });
        }
        _ => {}
    }

    event
}

fn parse_hotkey(hotkey: &str) -> anyhow::Result<(u16, u64)> {
    let mut vk = None;
    let mut mods = 0;
    for part in hotkey.split('+').map(str::trim) {
        match part.to_ascii_lowercase().as_str() {
            "ctrl" | "control" => mods |= K_MASK_CONTROL,
            "shift" => mods |= K_MASK_SHIFT,
            "alt" | "option" => mods |= K_MASK_OPTION,
            "cmd" | "command" => mods |= K_MASK_COMMAND,
            key => vk = mac_key_codes().get(key).copied(),
        }
    }

    let Some(vk) = vk else {
        bail!("could not parse macOS hotkey '{hotkey}'");
    };
    Ok((vk, mods))
}

fn mac_key_codes() -> &'static HashMap<&'static str, u16> {
    static CODES: OnceLock<HashMap<&'static str, u16>> = OnceLock::new();
    CODES.get_or_init(|| {
        HashMap::from([
            ("space", 49),
            ("return", 36),
            ("tab", 48),
            ("escape", 53),
            ("delete", 51),
            ("a", 0),
            ("b", 11),
            ("c", 8),
            ("d", 2),
            ("e", 14),
            ("f", 3),
            ("g", 5),
            ("h", 4),
            ("i", 34),
            ("j", 38),
            ("k", 40),
            ("l", 37),
            ("m", 46),
            ("n", 45),
            ("o", 31),
            ("p", 35),
            ("q", 12),
            ("r", 15),
            ("s", 1),
            ("t", 17),
            ("u", 32),
            ("v", 9),
            ("w", 13),
            ("x", 7),
            ("y", 16),
            ("z", 6),
        ])
    })
}
