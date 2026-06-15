// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

use std::ffi::c_void;
use std::sync::{
    atomic::{AtomicBool, AtomicU32, Ordering},
    mpsc, Arc, Mutex,
};
use std::thread;
use std::time::Duration;

use anyhow::{bail, Context};
use windows::Win32::Foundation::{LPARAM, LRESULT, WPARAM};
use windows::Win32::System::Threading::GetCurrentThreadId;
use windows::Win32::UI::Input::KeyboardAndMouse::{
    GetAsyncKeyState, RegisterHotKey, UnregisterHotKey, MOD_ALT, MOD_CONTROL, MOD_NOREPEAT,
};
use windows::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, DispatchMessageW, GetMessageW, PostThreadMessageW, SetWindowsHookExW,
    TranslateMessage, UnhookWindowsHookEx, HHOOK, KBDLLHOOKSTRUCT, LLKHF_INJECTED, MSG,
    WH_KEYBOARD_LL, WM_HOTKEY, WM_KEYUP, WM_QUIT, WM_SYSKEYUP,
};

use super::windows_input_coordinator::is_hotkey_release_detection_suppressed;

const HOTKEY_ID_RAW: i32 = 9101;
const HOTKEY_ID_CLEAN: i32 = 9102;
const VK_CONTROL: i32 = 0x11;
const VK_LCONTROL: i32 = 0xA2;
const VK_RCONTROL: i32 = 0xA3;
const VK_MENU: i32 = 0x12;
const VK_LMENU: i32 = 0xA4;
const VK_RMENU: i32 = 0xA5;
const VK_H: u32 = 0x48;
const RELEASE_POLL_MS: u64 = 24;
const HELD_H: u8 = 0b001;
const HELD_CTRL: u8 = 0b010;
const HELD_ALT: u8 = 0b100;

static KEYBOARD_HOOK: Mutex<HookState> = Mutex::new(HookState {
    hook: None,
    held: 0,
    is_clean: false,
});

#[derive(Debug, Clone)]
pub enum HotkeyEvent {
    RawPressed,
    RawReleased,
    CleanPressed,
    CleanReleased,
    Error(String),
}

pub struct GlobalHotkey {
    thread_id: Arc<AtomicU32>,
    thread: Option<thread::JoinHandle<()>>,
}

impl GlobalHotkey {
    pub fn register_ctrl_h(events: mpsc::Sender<HotkeyEvent>) -> anyhow::Result<Self> {
        let thread_id = Arc::new(AtomicU32::new(0));
        let thread_id_for_thread = Arc::clone(&thread_id);
        let (ready_tx, ready_rx) = mpsc::channel();

        let thread = thread::Builder::new()
            .name("hush-rust-hotkey".to_string())
            .spawn(move || {
                let result = unsafe {
                    let tid = GetCurrentThreadId();
                    thread_id_for_thread.store(tid, Ordering::Release);
                    RegisterHotKey(None, HOTKEY_ID_RAW, MOD_CONTROL | MOD_NOREPEAT, VK_H)
                        .and_then(|_| {
                            RegisterHotKey(
                                None,
                                HOTKEY_ID_CLEAN,
                                MOD_CONTROL | MOD_ALT | MOD_NOREPEAT,
                                VK_H,
                            )
                        })
                };

                if let Err(err) = result {
                    let message = format!(
                        "RegisterHotKey failed: {err}. Another app may already own Ctrl+H or Ctrl+Alt+H."
                    );
                    let _ = ready_tx.send(Err(message.clone()));
                    let _ = events.send(HotkeyEvent::Error(message));
                    return;
                }

                let _ = ready_tx.send(Ok(()));
                message_loop(events);

                unsafe {
                    let _ = UnregisterHotKey(None, HOTKEY_ID_RAW);
                    let _ = UnregisterHotKey(None, HOTKEY_ID_CLEAN);
                }
            })
            .context("starting hotkey listener thread")?;

        match ready_rx
            .recv_timeout(Duration::from_secs(3))
            .context("waiting for hotkey registration")?
        {
            Ok(()) => Ok(Self {
                thread_id,
                thread: Some(thread),
            }),
            Err(message) => {
                let _ = thread.join();
                bail!(message)
            }
        }
    }
}

impl Drop for GlobalHotkey {
    fn drop(&mut self) {
        let thread_id = self.thread_id.load(Ordering::Acquire);
        if thread_id != 0 {
            unsafe {
                let _ = PostThreadMessageW(thread_id, WM_QUIT, WPARAM(0), LPARAM(0));
            }
        }

        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

fn message_loop(events: mpsc::Sender<HotkeyEvent>) {
    let held = Arc::new(AtomicBool::new(false));
    let mut msg = MSG::default();

    while unsafe { GetMessageW(&mut msg, None, 0, 0).as_bool() } {
        if msg.message == WM_HOTKEY && msg.wParam.0 == HOTKEY_ID_RAW as usize {
            if !held.swap(true, Ordering::AcqRel) {
                install_keyboard_suppression_hook(false);
                let _ = events.send(HotkeyEvent::RawPressed);
                spawn_release_poller(events.clone(), Arc::clone(&held), false);
            }
        }

        if msg.message == WM_HOTKEY && msg.wParam.0 == HOTKEY_ID_CLEAN as usize {
            if !held.swap(true, Ordering::AcqRel) {
                install_keyboard_suppression_hook(true);
                let _ = events.send(HotkeyEvent::CleanPressed);
                spawn_release_poller(events.clone(), Arc::clone(&held), true);
            }
        }

        unsafe {
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    uninstall_keyboard_suppression_hook();
}

fn spawn_release_poller(events: mpsc::Sender<HotkeyEvent>, held: Arc<AtomicBool>, is_clean: bool) {
    thread::spawn(move || {
        let mut release_count = 0;
        loop {
            thread::sleep(Duration::from_millis(RELEASE_POLL_MS));
            if is_hotkey_release_detection_suppressed() {
                continue;
            }

            if !is_chord_physically_held(is_clean) {
                release_count += 1;
                if release_count >= 2 {
                    held.store(false, Ordering::Release);
                    uninstall_keyboard_suppression_hook();
                    let _ = events.send(if is_clean {
                        HotkeyEvent::CleanReleased
                    } else {
                        HotkeyEvent::RawReleased
                    });
                    break;
                }
            } else {
                release_count = 0;
            }
        }
    });
}

fn is_key_down(key: i32) -> bool {
    unsafe { (GetAsyncKeyState(key) & 0x8000u16 as i16) != 0 }
}

fn install_keyboard_suppression_hook(is_clean: bool) {
    let Ok(mut state) = KEYBOARD_HOOK.lock() else {
        return;
    };
    state.is_clean = is_clean;
    state.held = snapshot_held_chord(is_clean);
    if state.hook.is_some() {
        return;
    }

    if let Ok(hook) =
        unsafe { SetWindowsHookExW(WH_KEYBOARD_LL, Some(suppress_keyboard_input), None, 0) }
    {
        state.hook = Some(hook.0 as isize);
    }
}

fn uninstall_keyboard_suppression_hook() {
    let Ok(mut state) = KEYBOARD_HOOK.lock() else {
        return;
    };
    if let Some(hook) = state.hook.take() {
        unsafe {
            let _ = UnhookWindowsHookEx(HHOOK(hook as *mut c_void));
        }
    }
    state.held = 0;
    state.is_clean = false;
}

unsafe extern "system" fn suppress_keyboard_input(
    code: i32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    if code < 0 {
        return unsafe { CallNextHookEx(None, code, wparam, lparam) };
    }

    let keyboard = unsafe { *(lparam.0 as *const KBDLLHOOKSTRUCT) };
    if keyboard.flags.contains(LLKHF_INJECTED) {
        return unsafe { CallNextHookEx(None, code, wparam, lparam) };
    }

    if wparam.0 as u32 == WM_KEYUP || wparam.0 as u32 == WM_SYSKEYUP {
        record_physical_key_up(keyboard.vkCode);
        if is_tracked_chord_key(keyboard.vkCode) {
            return unsafe { CallNextHookEx(None, code, wparam, lparam) };
        }
    }

    LRESULT(1)
}

fn snapshot_held_chord(is_clean: bool) -> u8 {
    let mut held = 0;
    if is_key_down(VK_H as i32) {
        held |= HELD_H;
    }
    if is_key_down(VK_CONTROL) || is_key_down(VK_LCONTROL) || is_key_down(VK_RCONTROL) {
        held |= HELD_CTRL;
    }
    if is_clean && (is_key_down(VK_MENU) || is_key_down(VK_LMENU) || is_key_down(VK_RMENU)) {
        held |= HELD_ALT;
    }
    held
}

fn is_chord_physically_held(is_clean: bool) -> bool {
    let Ok(state) = KEYBOARD_HOOK.lock() else {
        return fallback_chord_held(is_clean);
    };
    if state.hook.is_some() {
        state.held != 0
    } else {
        fallback_chord_held(is_clean)
    }
}

fn fallback_chord_held(is_clean: bool) -> bool {
    is_key_down(VK_CONTROL) || is_key_down(VK_H as i32) || (is_clean && is_key_down(VK_MENU))
}

fn record_physical_key_up(vk_code: u32) {
    let Ok(mut state) = KEYBOARD_HOOK.lock() else {
        return;
    };
    match vk_code as i32 {
        key if key == VK_H as i32 => state.held &= !HELD_H,
        VK_CONTROL | VK_LCONTROL | VK_RCONTROL => state.held &= !HELD_CTRL,
        VK_MENU | VK_LMENU | VK_RMENU => state.held &= !HELD_ALT,
        _ => {}
    }
}

fn is_tracked_chord_key(vk_code: u32) -> bool {
    matches!(
        vk_code as i32,
        VK_CONTROL | VK_LCONTROL | VK_RCONTROL | VK_MENU | VK_LMENU | VK_RMENU
    ) || vk_code == VK_H
}

struct HookState {
    hook: Option<isize>,
    held: u8,
    is_clean: bool,
}
