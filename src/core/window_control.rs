//! Window close (WM_CLOSE → enabled popup) and process-tree termination.
//!
//! 1:1 port of `legacy/Models/AppWindow.cs::Close` + the kill-tree behaviour
//! of `MainViewModel.StopSelectedProcess` (which delegates to .NET's
//! `Process.Kill(entireProcessTree: true)`). See
//! `docs/rust-rewrite-design-step4-5.md` §5-B.

use crate::core::app_window::AppWindow;
use crate::core::win32::{Gw, Hwnd, Win32Api};

/// Outcome of [`close`], mirroring the bool contract of `AppWindow.Close()` +
/// the keep/remove decision in `MainViewModel.CloseSelectedWindow`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CloseResult {
    /// `WM_CLOSE` was posted to the root window — the caller may remove the
    /// entry from the switcher list.
    RootClosed,
    /// Only an owned, visible modal dialog was dismissed; the root is still
    /// open, so the caller must **keep** the entry (removing it desyncs the
    /// list from reality).
    OnlyDialogDismissed,
}

/// Close `app`'s window by posting `WM_CLOSE` to the enabled popup when the
/// root owns a visible one, else to the root. Mirrors `AppWindow.Close()`.
pub fn close<A: Win32Api>(api: &A, app: &AppWindow) -> CloseResult {
    let root = app.handle;
    let popup = api.get_window(root, Gw::EnabledPopup);
    let has_modal_popup = matches!(
        popup,
        Some(p) if p != root && api.is_window_visible(p)
    );
    let target: Hwnd = if has_modal_popup {
        popup.unwrap()
    } else {
        root
    };
    api.post_close(target);
    if has_modal_popup {
        CloseResult::OnlyDialogDismissed
    } else {
        CloseResult::RootClosed
    }
}

/// Kill the whole process tree rooted at `root_pid` (equivalent to .NET's
/// `Process.Kill(entireProcessTree: true)`). Returns `true` when the root pid
/// itself was terminated. Per-pid failures are swallowed — matching the legacy
/// `StopSelectedProcess` which wraps the whole thing in `try { … } catch {}`.
/// No elevation pre-check here (the legacy code has none at this layer either;
/// that guard lives in the bridge/UI).
pub fn terminate_process_tree<A: Win32Api>(api: &A, root_pid: u32) -> bool {
    let pids = api.enumerate_process_tree(root_pid);
    let mut root_terminated = false;
    for pid in pids {
        if let Some(handle) = api.open_process_terminate(pid) {
            if api.terminate_process(&handle, 1) && pid == root_pid {
                root_terminated = true;
            }
        }
    }
    root_terminated
}

#[cfg(test)]
#[path = "window_control_tests.rs"]
mod window_control_tests;
