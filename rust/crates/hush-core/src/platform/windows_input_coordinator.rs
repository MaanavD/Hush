// Copyright (c) 2026 Maanav Dalal. Licensed under the MIT License.

use std::sync::atomic::{AtomicUsize, Ordering};

static SUPPRESSED_RELEASE_DETECTION_COUNT: AtomicUsize = AtomicUsize::new(0);

pub fn is_hotkey_release_detection_suppressed() -> bool {
    SUPPRESSED_RELEASE_DETECTION_COUNT.load(Ordering::Acquire) > 0
}

pub fn suppress_hotkey_release_detection() -> HotkeyReleaseSuppression {
    SUPPRESSED_RELEASE_DETECTION_COUNT.fetch_add(1, Ordering::AcqRel);
    HotkeyReleaseSuppression
}

pub struct HotkeyReleaseSuppression;

impl Drop for HotkeyReleaseSuppression {
    fn drop(&mut self) {
        SUPPRESSED_RELEASE_DETECTION_COUNT.fetch_sub(1, Ordering::AcqRel);
    }
}
