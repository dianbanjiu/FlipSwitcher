//! Window-activation fallback chain — the project's most regression-prone path.
//!
//! 1:1 port of `legacy/Models/AppWindow.cs::Activate` + `ResolveActivationTarget`.
//! The full fallback chain is a **contract**: every link exists because some
//! window stops activating without it — see `docs/rust-rewrite-design.md` §3.3
//! and `docs/rust-rewrite-design-step4-5.md` §5-A.
//!
//! Key invariants mirrored from the C#:
//! - `target` (the resolved activation target — an owned popup when the root
//!   owns a visible one, else the root) is used for `BringWindowToTop` /
//!   `SetForegroundWindow` / `SwitchToThisWindow` / `SetWindowPos`.
//! - `root` is used for `GetWindowPlacement` / `ShowWindow` (restore/maximize
//!   operates on the top-level window).
//! - `was_maximized` / `was_minimized` are the OR of **three** sources — the
//!   placement `showCmd`, the live `IsZoomed`/`IsIconic` query, and the
//!   enumeration-time snapshot on [`AppWindow`] (which already folds in
//!   `WPF_RESTORETOMAXIMIZED`). Dropping any one mis-restores "minimized but
//!   restore-to-maximized" windows.
//! - `AttachThreadInput` pairs are detached in `finally`; here that's an RAII
//!   guard whose `Drop` runs even when the try block panics.

use std::panic::{catch_unwind, AssertUnwindSafe};

use crate::core::app_window::AppWindow;
use crate::core::win32::{
    Hwnd, HWND_NOTOPMOST, HWND_TOPMOST, KEYEVENTF_EXTENDEDKEY, KEYEVENTF_KEYUP, SW_RESTORE,
    SW_SHOWMAXIMIZED, SW_SHOWMINIMIZED, SW_SHOWNORMAL, SWP_NOMOVE, SWP_NOSIZE, SWP_SHOWWINDOW,
    VK_ALT, Win32Api,
};

/// Which window ended up targeted (popup or root) — useful for the bridge.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ActivationOutcome {
    pub target: Hwnd,
}

/// Resolve the HWND that should receive foreground focus when `root` is
/// activated. When the root owns a visible popup (modal dialog, login prompt,
/// open/save dialog, MessageBox, …), Windows' own Alt-Tab activates that popup
/// rather than the root — the popup is what the user types into. Mirror that by
/// targeting `GetLastActivePopup`'s result when it is a different, visible
/// window; otherwise fall back to the root. Hidden/stale popups never steal
/// activation. Mirrors `AppWindow.ResolveActivationTarget`.
pub fn resolve_activation_target<A: Win32Api>(api: &A, root: Hwnd) -> Hwnd {
    match api.get_last_active_popup(root) {
        Some(popup) if popup != root && api.is_window_visible(popup) => popup,
        _ => root,
    }
}

/// Activate `app`'s window, restoring/maximizing as needed and stealing
/// foreground through the full fallback chain. Mirrors `AppWindow.Activate`.
pub fn activate<A: Win32Api>(api: &A, app: &AppWindow) -> ActivationOutcome {
    let root = app.handle;
    let target = resolve_activation_target(api, root);

    api.allow_set_foreground_window_any();
    api.lock_set_foreground_window_unlock();

    let mut attached_fg = false;
    let mut attached_tgt = false;
    // Initialised up-front so the detach block below is sound even if the
    // try block panics before it reaches the assignment.
    let mut fg_tid: u32 = 0;
    let mut tgt_tid: u32 = 0;
    let mut cur_tid: u32 = 0;

    // try { … } catch { minimal fallback } finally { detach }.
    // `catch_unwind` plays the role of `try/catch`: a panic (or a hook-injected
    // failure) drops us into the minimal-fallback branch. Detach is handled
    // after the unwind — matching the C# `finally` — and reads only the threads
    // we actually attached, which the `attached_*` flags gate.
    let panicked = catch_unwind(AssertUnwindSafe(|| {
        let fg = api.get_foreground_window();
        fg_tid = fg
            .map(|h| api.get_window_thread_process_id(h).0)
            .unwrap_or(0);
        cur_tid = api.get_current_thread_id();
        tgt_tid = api.get_window_thread_process_id(root).0;

        if fg_tid != 0 && fg_tid != cur_tid {
            attached_fg = api.attach_thread_input(cur_tid, fg_tid, true);
        }
        if tgt_tid != 0 && tgt_tid != cur_tid && tgt_tid != fg_tid {
            attached_tgt = api.attach_thread_input(cur_tid, tgt_tid, true);
        }

        let show_cmd = api.get_window_placement_show_cmd(root).unwrap_or(SW_SHOWNORMAL);
        let was_maximized =
            show_cmd == SW_SHOWMAXIMIZED || api.is_zoomed(root) || app.is_maximized;
        let was_minimized =
            show_cmd == SW_SHOWMINIMIZED || api.is_iconic(root) || app.is_minimized;

        if was_minimized {
            let cmd = if was_maximized { SW_SHOWMAXIMIZED } else { SW_RESTORE };
            api.show_window(root, cmd);
        } else if was_maximized {
            api.show_window(root, SW_SHOWMAXIMIZED);
        }

        api.bring_window_to_top(target);
        api.set_foreground_window(target);

        if api.get_foreground_window() != Some(target) {
            // Fake an Alt tap so SetForegroundWindow is permitted, then retry.
            api.keybd_event(VK_ALT, KEYEVENTF_EXTENDEDKEY);
            api.keybd_event(VK_ALT, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP);
            api.set_foreground_window(target);
            api.bring_window_to_top(target);
        }

        if api.get_foreground_window() != Some(target) {
            api.switch_to_this_window(target, true);
        }

        if api.get_foreground_window() != Some(target) {
            let flags = SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW;
            api.set_window_pos(target, HWND_TOPMOST, 0, 0, 0, 0, flags);
            api.set_window_pos(target, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
        }
    }))
    .is_err();

    if panicked {
        // Minimal catch-all fallback (mirrors the C# `catch`): restore the root
        // if iconic, then SwitchToThisWindow on the target.
        if api.is_iconic(root) {
            api.show_window(root, SW_RESTORE);
        }
        api.switch_to_this_window(target, true);
    }

    // finally: detach the threads we actually attached, in attach order.
    if attached_fg {
        api.attach_thread_input(cur_tid, fg_tid, false);
    }
    if attached_tgt {
        api.attach_thread_input(cur_tid, tgt_tid, false);
    }

    ActivationOutcome { target }
}

#[cfg(test)]
#[path = "activation_tests.rs"]
mod activation_tests;
