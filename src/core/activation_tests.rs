//! Mock-driven tests for the activation fallback chain.
//!
//! Verifies the contract from `docs/rust-rewrite-design-step4-5.md` §5-A:
//! attach/detach pairing, the three `was_maximized`/`was_minimized` sources,
//! each fallback tier firing only when the previous fails, `ResolveActivationTarget`,
//! and the minimal catch fallback. Shape mirrors `legacy/Models/AppWindow.cs::Activate`.

#![allow(clippy::needless_borrow)]

use super::*;
use crate::core::app_window::AppWindow;
use crate::core::win32::{Gw, Gwlp, Hmonitor, IconImage, MonitorFlag, OwnedProcessHandle, Rect, Win32Error};
use std::collections::HashMap;
use std::sync::Mutex;

/// One recorded Win32 call.
#[derive(Debug, Clone, PartialEq, Eq)]
enum Call {
    AllowSetForeground,
    LockSetForegroundUnlock,
    GetForeground,
    GetCurrentThread,
    GetWindowThreadPid(Hwnd),
    Attach(u32, u32, bool),
    Detach(u32, u32),
    GetPlacementShow(Hwnd),
    IsZoomed(Hwnd),
    IsIconic(Hwnd),
    ShowWindow(Hwnd, i32),
    BringToTop(Hwnd),
    SetForeground(Hwnd),
    KeybdEvent(u8, u32),
    SwitchToThisWindow(Hwnd),
    SetWindowPos(Hwnd, isize),
    GetLastActivePopup(Hwnd),
    IsWindowVisible(Hwnd),
}

#[derive(Default)]
struct Behavior {
    /// The HWND `get_foreground_window` reports. `None` ⇒ zero (no foreground).
    foreground: Option<Hwnd>,
    /// When `Some(true)`, every `get_foreground_window` after the first
    /// `set_foreground_window` reports `target` (success). When `Some(set)`
    /// containing the set of "successful" foreground states keyed by step,
    /// we instead consult `force_fail_foreground`.
    /// Simplified: `succeed_foreground` ⇒ after the first SetForeground the
    /// foreground becomes `target`. Otherwise it stays as `foreground`.
    succeed_foreground: bool,
    /// Inject a panic at this call kind (first occurrence).
    panic_at: Option<&'static str>,
}

struct MockUniverse {
    wins: HashMap<Hwnd, MockWin>,
    /// `GetLastActivePopup` mapping: hwnd → popup hwnd (defaults to itself).
    last_active_popup: HashMap<Hwnd, Hwnd>,
    /// Explicit thread id per HWND (for `GetWindowThreadProcessId`). Defaults
    /// to a per-HWND deterministic value via `default_tid` when absent.
    thread_ids: HashMap<Hwnd, u32>,
    calls: Vec<Call>,
    /// Track whether we've already "succeeded" foreground so the second
    /// SetForeground check flips to the target.
    foreground_succeeded: bool,
}

impl MockUniverse {
    fn tid_of(&self, hwnd: Hwnd) -> u32 {
        *self.thread_ids.get(&hwnd).unwrap_or(&default_tid(hwnd))
    }
}

/// Deterministic, non-zero thread id derived from an HWND when not overridden.
fn default_tid(hwnd: Hwnd) -> u32 {
    // Keep it distinct from typical `cur_tid` test values (we use 100) and from
    // each other, but avoid the high-bit OR that made equality unreachable.
    let h = (hwnd.0 as u32).wrapping_mul(2654435761);
    h | 1 // ensure non-zero
}

#[derive(Clone, Default)]
struct MockWin {
    visible: bool,
    zoomed: bool,
    iconic: bool,
    placement_show: i32,
}

struct MockWin32 {
    state: Mutex<MockUniverse>,
    behavior: Mutex<Behavior>,
    current_tid: u32,
}

impl MockWin32 {
    fn new(current_tid: u32) -> Self {
        Self {
            state: Mutex::new(MockUniverse {
                wins: HashMap::new(),
                last_active_popup: HashMap::new(),
                thread_ids: HashMap::new(),
                calls: Vec::new(),
                foreground_succeeded: false,
            }),
            behavior: Mutex::new(Behavior::default()),
            current_tid,
        }
    }

    fn record(&self, c: Call) {
        let b = self.behavior.lock().unwrap();
        if let Some(trigger) = b.panic_at {
            let matches = matches!(
                (&c, trigger),
                (Call::BringToTop(_), "BringToTop")
                    | (Call::SetForeground(_), "SetForeground")
                    | (Call::SetWindowPos(_, _), "SetWindowPos")
                    | (Call::SwitchToThisWindow(_), "SwitchToThisWindow")
            );
            if matches {
                // Only panic once, then clear so subsequent calls (fallback)
                // can proceed.
                drop(b);
                self.behavior.lock().unwrap().panic_at = None;
                panic!("injected panic at {trigger}");
            }
        }
        drop(b);
        self.state.lock().unwrap().calls.push(c);
    }

    fn calls(&self) -> Vec<Call> {
        self.state.lock().unwrap().calls.clone()
    }

    fn add_win(&self, hwnd: Hwnd, w: MockWin) {
        self.state.lock().unwrap().wins.insert(hwnd, w);
    }

    fn set_last_active_popup(&self, root: Hwnd, popup: Hwnd) {
        self.state.lock().unwrap().last_active_popup.insert(root, popup);
    }

    fn set_thread_id(&self, hwnd: Hwnd, tid: u32) {
        self.state.lock().unwrap().thread_ids.insert(hwnd, tid);
    }

    fn set_behavior(&self, f: impl FnOnce(&mut Behavior)) {
        f(&mut self.behavior.lock().unwrap());
    }

    fn win(&self, hwnd: Hwnd) -> MockWin {
        self.state
            .lock()
            .unwrap()
            .wins
            .get(&hwnd)
            .cloned()
            .unwrap_or_default()
    }
}

// A blanket impl of the whole Win32Api trait for the mock. Only the methods
// the activation path touches are meaningful; the rest return defaults.
impl crate::core::win32::Win32Api for MockWin32 {
    fn is_window_visible(&self, hwnd: Hwnd) -> bool {
        self.record(Call::IsWindowVisible(hwnd));
        self.win(hwnd).visible
    }
    fn is_iconic(&self, hwnd: Hwnd) -> bool {
        self.record(Call::IsIconic(hwnd));
        self.win(hwnd).iconic
    }
    fn is_zoomed(&self, hwnd: Hwnd) -> bool {
        self.record(Call::IsZoomed(hwnd));
        self.win(hwnd).zoomed
    }
    fn get_window_text(&self, _hwnd: Hwnd) -> String { String::new() }
    fn get_window_text_length(&self, _hwnd: Hwnd) -> i32 { 0 }
    fn get_class_name(&self, _hwnd: Hwnd) -> String { String::new() }
    fn get_window_long_ptr(&self, _hwnd: Hwnd, _idx: Gwlp) -> isize { 0 }
    fn get_window_rect(&self, _hwnd: Hwnd) -> Option<Rect> { None }
    fn get_window(&self, _hwnd: Hwnd, _cmd: Gw) -> Option<Hwnd> { None }
    fn get_window_thread_process_id(&self, hwnd: Hwnd) -> (u32, u32) {
        self.record(Call::GetWindowThreadPid(hwnd));
        (self.state.lock().unwrap().tid_of(hwnd), 0)
    }
    fn get_shell_window(&self) -> Option<Hwnd> { None }
    fn enum_windows(
        &self,
        _cb: &mut dyn FnMut(Hwnd) -> bool,
    ) -> Result<(), Win32Error> {
        Ok(())
    }
    fn is_cloaked(&self, _hwnd: Hwnd) -> bool { false }
    fn get_layered_window_attributes(&self, _hwnd: Hwnd) -> Option<(u32, u8, u32)> { None }
    fn get_window_placement_show_cmd(&self, hwnd: Hwnd) -> Option<i32> {
        self.record(Call::GetPlacementShow(hwnd));
        Some(self.win(hwnd).placement_show)
    }
    fn get_window_placement_flags(&self, _hwnd: Hwnd) -> Option<i32> { Some(0) }
    fn enum_display_monitors(&self) -> Vec<(Hmonitor, Rect)> { Vec::new() }
    fn monitor_from_window(&self, _hwnd: Hwnd, _flag: MonitorFlag) -> Option<Hmonitor> { None }
    fn query_full_process_image_name(&self, _pid: u32) -> Option<String> { None }
    fn open_process_query_limited(&self, _pid: u32) -> Option<OwnedProcessHandle> { None }
    fn process_elevation(&self, _handle: &OwnedProcessHandle) -> bool { false }
    fn current_process_id(&self) -> u32 { 1 }
    fn get_window_icon_handle(&self, _hwnd: Hwnd) -> Option<isize> { None }
    fn post_close(&self, _hwnd: Hwnd) {}
    fn get_foreground_window(&self) -> Option<Hwnd> {
        self.record(Call::GetForeground);
        let b = self.behavior.lock().unwrap();
        if b.succeed_foreground {
            // Before any SetForeground: report the configured foreground window
            // (so fg_tid is real and the fg-attach path is exercised). After a
            // SetForeground: flip to the target, simulating success.
            let fg = b.foreground;
            drop(b);
            let g = self.state.lock().unwrap();
            if g.foreground_succeeded {
                // Return the most recent SetForeground target.
                for c in g.calls.iter().rev() {
                    if let Call::SetForeground(h) = c {
                        return Some(*h);
                    }
                }
            }
            fg
        } else {
            b.foreground
        }
    }
    fn get_current_thread_id(&self) -> u32 {
        self.record(Call::GetCurrentThread);
        self.current_tid
    }
    fn attach_thread_input(&self, id_attach: u32, id_attach_to: u32, attach: bool) -> bool {
        if attach {
            self.record(Call::Attach(id_attach, id_attach_to, true));
        } else {
            self.record(Call::Detach(id_attach, id_attach_to));
        }
        true
    }
    fn allow_set_foreground_window_any(&self) -> bool {
        self.record(Call::AllowSetForeground);
        true
    }
    fn lock_set_foreground_window_unlock(&self) -> bool {
        self.record(Call::LockSetForegroundUnlock);
        true
    }
    fn show_window(&self, hwnd: Hwnd, cmd: i32) -> bool {
        self.record(Call::ShowWindow(hwnd, cmd));
        true
    }
    fn bring_window_to_top(&self, hwnd: Hwnd) -> bool {
        self.record(Call::BringToTop(hwnd));
        true
    }
    fn set_foreground_window(&self, hwnd: Hwnd) -> bool {
        self.record(Call::SetForeground(hwnd));
        // Flip the "succeeded" latch so the next get_foreground_window reports
        // this hwnd (when succeed_foreground behavior is on).
        self.state.lock().unwrap().foreground_succeeded = true;
        true
    }
    fn switch_to_this_window(&self, hwnd: Hwnd, _alt_tab: bool) {
        self.record(Call::SwitchToThisWindow(hwnd));
    }
    fn set_window_pos(
        &self,
        hwnd: Hwnd,
        hwnd_insert_after: isize,
        _x: i32,
        _y: i32,
        _cx: i32,
        _cy: i32,
        _flags: u32,
    ) -> bool {
        self.record(Call::SetWindowPos(hwnd, hwnd_insert_after));
        true
    }
    fn keybd_event(&self, vk: u8, flags: u32) {
        self.record(Call::KeybdEvent(vk, flags));
    }
    fn get_last_active_popup(&self, hwnd: Hwnd) -> Option<Hwnd> {
        self.record(Call::GetLastActivePopup(hwnd));
        let g = self.state.lock().unwrap();
        Some(*g.last_active_popup.get(&hwnd).unwrap_or(&hwnd))
    }
    fn open_process_terminate(&self, _pid: u32) -> Option<OwnedProcessHandle> { None }
    fn terminate_process(&self, _handle: &OwnedProcessHandle, _exit_code: u32) -> bool { false }
    fn enumerate_process_tree(&self, _root_pid: u32) -> Vec<u32> { Vec::new() }
    fn is_key_down_async(&self, _vk: u32) -> bool { false }
    fn find_child_window_class_pid(
        &self,
        _parent: Hwnd,
        _class: &str,
        _exclude_pid: u32,
    ) -> Option<(Hwnd, u32)> {
        None
    }
    fn shell_extract_icon(&self, _exe_path: &str) -> Option<IconImage> { None }
    fn extract_associated_icon(&self, _exe_path: &str) -> Option<IconImage> { None }
    fn window_icon_to_image(&self, _hicon: isize) -> Option<IconImage> { None }
}

// —— helpers ——

fn app(hwnd: i64, min: bool, max: bool) -> AppWindow {
    AppWindow {
        handle: Hwnd(hwnd as isize),
        title: String::new(),
        class_name: String::new(),
        process_id: 1,
        process_name: "mock".into(),
        is_minimized: min,
        is_maximized: max,
        is_topmost: false,
        owner_kept: None,
    }
}

fn count(calls: &[Call], pred: impl Fn(&Call) -> bool) -> usize {
    calls.iter().filter(|c| pred(c)).count()
}

// —— tests ——

#[test]
fn standard_path_attaches_both_threads_and_detaches_in_order() {
    // fg thread != cur, target thread != cur && != fg → both attach.
    let api = MockWin32::new(100);
    let root = Hwnd(11);
    api.add_win(root, MockWin { visible: true, placement_show: 1, ..Default::default() });
    api.set_behavior(|b| {
        b.foreground = Some(Hwnd(22)); // different thread than cur (100)
        b.succeed_foreground = true; // first SetForeground wins
    });
    let a = app(11, false, false);
    let out = activate(&api, &a);
    assert_eq!(out.target, root);

    let calls = api.calls();
    // Two attaches (fg, target) and two detaches (fg, target) in that order.
    let attaches: Vec<(u32, u32)> = calls
        .iter()
        .filter_map(|c| match c { Call::Attach(a, b, true) => Some((*a, *b)), _ => None })
        .collect();
    let detaches: Vec<(u32, u32)> = calls
        .iter()
        .filter_map(|c| match c { Call::Detach(a, b) => Some((*a, *b)), _ => None })
        .collect();
    assert_eq!(attaches.len(), 2, "should attach both fg and target");
    assert_eq!(detaches.len(), 2, "should detach both");
    // detach order mirrors attach order.
    assert_eq!(attaches, detaches, "detach order must mirror attach order");
    // cur_tid is the first element of each pair.
    assert!(attaches.iter().all(|&(a, _)| a == 100));
}

#[test]
fn skips_attach_when_thread_equals_current() {
    // foreground thread == current thread → skip fg attach; target != cur → still attach target.
    let api = MockWin32::new(100);
    let root = Hwnd(11);
    api.add_win(root, MockWin { visible: true, placement_show: 1, ..Default::default() });
    let fg_hwnd = Hwnd(22);
    api.set_thread_id(fg_hwnd, 100); // fg tid == cur tid → fg attach skipped
    api.set_behavior(|b| {
        b.foreground = Some(fg_hwnd);
        b.succeed_foreground = true;
    });
    let a = app(11, false, false);
    let _ = activate(&api, &a);

    let calls = api.calls();
    let attaches: Vec<(u32, u32)> = calls
        .iter()
        .filter_map(|c| match c { Call::Attach(a, b, true) => Some((*a, *b)), _ => None })
        .collect();
    // Only the target attach; the fg attach is skipped (fg_tid == cur_tid).
    assert_eq!(attaches.len(), 1);
    // And exactly one detach (only the one we attached).
    assert_eq!(count(&calls, |c| matches!(c, Call::Detach(_, _))), 1);
}

#[test]
fn minimized_non_maximized_restores() {
    let api = MockWin32::new(100);
    let root = Hwnd(11);
    api.add_win(
        root,
        MockWin {
            visible: true,
            iconic: true,
            placement_show: 2, // SW_SHOWMINIMIZED
            ..Default::default()
        },
    );
    api.set_behavior(|b| {
        b.foreground = Some(Hwnd(22));
        b.succeed_foreground = true;
    });
    let a = app(11, true, false);
    let _ = activate(&api, &a);

    let calls = api.calls();
    // Exactly one ShowWindow, with SW_RESTORE (9).
    let shows: Vec<i32> = calls
        .iter()
        .filter_map(|c| match c { Call::ShowWindow(_, cmd) => Some(*cmd), _ => None })
        .collect();
    assert_eq!(shows, vec![SW_RESTORE]);
}

#[test]
fn minimized_and_maximized_showmaximized() {
    let api = MockWin32::new(100);
    let root = Hwnd(11);
    api.add_win(
        root,
        MockWin {
            visible: true,
            iconic: true,
            placement_show: 2, // minimized
            ..Default::default()
        },
    );
    api.set_behavior(|b| {
        b.foreground = Some(Hwnd(22));
        b.succeed_foreground = true;
    });
    // app snapshot says maximized → restore-to-maximized.
    let a = app(11, true, true);
    let _ = activate(&api, &a);

    let shows: Vec<i32> = api
        .calls()
        .iter()
        .filter_map(|c| match c { Call::ShowWindow(_, cmd) => Some(*cmd), _ => None })
        .collect();
    assert_eq!(shows, vec![SW_SHOWMAXIMIZED]);
}

#[test]
fn snapshot_maximized_drives_showmaximized_even_when_placement_says_normal() {
    // Three-source OR: app.is_maximized=true but placement.showCmd=SW_SHOWNORMAL
    // and is_zoomed=false → still was_maximized → ShowWindow(SW_SHOWMAXIMIZED).
    let api = MockWin32::new(100);
    let root = Hwnd(11);
    api.add_win(
        root,
        MockWin {
            visible: true,
            placement_show: SW_SHOWNORMAL,
            ..Default::default()
        },
    );
    api.set_behavior(|b| {
        b.foreground = Some(Hwnd(22));
        b.succeed_foreground = true;
    });
    let a = app(11, false, true);
    let _ = activate(&api, &a);

    let shows: Vec<i32> = api
        .calls()
        .iter()
        .filter_map(|c| match c { Call::ShowWindow(_, cmd) => Some(*cmd), _ => None })
        .collect();
    assert_eq!(shows, vec![SW_SHOWMAXIMIZED]);
}

#[test]
fn first_setforeground_success_skips_keybd_and_fallbacks() {
    let api = MockWin32::new(100);
    let root = Hwnd(11);
    api.add_win(root, MockWin { visible: true, placement_show: 1, ..Default::default() });
    api.set_behavior(|b| {
        b.foreground = Some(Hwnd(22));
        b.succeed_foreground = true; // SetForeground wins immediately
    });
    let a = app(11, false, false);
    let _ = activate(&api, &a);

    let calls = api.calls();
    assert_eq!(
        count(&calls, |c| matches!(c, Call::KeybdEvent(_, _))),
        0,
        "no synthetic Alt when first SetForeground wins"
    );
    assert_eq!(count(&calls, |c| matches!(c, Call::SwitchToThisWindow(_))), 0);
    assert_eq!(count(&calls, |c| matches!(c, Call::SetWindowPos(_, _))), 0);
}

#[test]
fn first_failure_triggers_alt_tap_then_retry() {
    // succeed_foreground=false: foreground stays as the (non-target) value, so
    // the first check fails → keybd_event pair + retry SetForeground/BringToTop.
    // But the second check *also* fails (foreground unchanged), so it continues
    // to SwitchToThisWindow and SetWindowPos. To isolate the Alt-tap tier we
    // make the foreground become the target only AFTER the *second* SetForeground.
    let api = MockWin32::new(100);
    let root = Hwnd(11);
    api.add_win(root, MockWin { visible: true, placement_show: 1, ..Default::default() });
    api.set_behavior(|b| {
        b.foreground = Some(Hwnd(22));
        b.succeed_foreground = false; // never auto-succeed → all tiers fire
    });
    let a = app(11, false, false);
    let _ = activate(&api, &a);

    let calls = api.calls();
    // keybd_event pair: down then up.
    let keys: Vec<(u8, u32)> = calls
        .iter()
        .filter_map(|c| match c { Call::KeybdEvent(vk, f) => Some((*vk, *f)), _ => None })
        .collect();
    assert_eq!(
        keys,
        vec![(VK_ALT, KEYEVENTF_EXTENDEDKEY), (VK_ALT, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP)]
    );
    // Two SetForeground calls (initial + retry).
    assert_eq!(count(&calls, |c| matches!(c, Call::SetForeground(_))), 2);
    // Two BringToTop calls (initial + retry).
    assert_eq!(count(&calls, |c| matches!(c, Call::BringToTop(_))), 2);
}

#[test]
fn persistent_failure_fires_switch_and_topmost_pair() {
    let api = MockWin32::new(100);
    let root = Hwnd(11);
    api.add_win(root, MockWin { visible: true, placement_show: 1, ..Default::default() });
    api.set_behavior(|b| {
        b.foreground = Some(Hwnd(22));
        b.succeed_foreground = false; // never succeeds → every tier fires
    });
    let a = app(11, false, false);
    let _ = activate(&api, &a);

    let calls = api.calls();
    assert_eq!(count(&calls, |c| matches!(c, Call::SwitchToThisWindow(_))), 1);
    let pos: Vec<isize> = calls
        .iter()
        .filter_map(|c| match c { Call::SetWindowPos(_, after) => Some(*after), _ => None })
        .collect();
    assert_eq!(pos, vec![HWND_TOPMOST, HWND_NOTOPMOST], "topmost then notopmost");
    // Both SetWindowPos target the activation target (root here).
    assert!(calls
        .iter()
        .filter_map(|c| match c { Call::SetWindowPos(h, _) => Some(*h), _ => None })
        .all(|h| h == root));
}

#[test]
fn resolve_activation_target_picks_visible_popup() {
    let api = MockWin32::new(100);
    let root = Hwnd(11);
    let popup = Hwnd(99);
    api.add_win(root, MockWin { visible: true, placement_show: 1, ..Default::default() });
    api.add_win(popup, MockWin { visible: true, ..Default::default() });
    api.set_last_active_popup(root, popup);
    api.set_behavior(|b| {
        b.foreground = Some(Hwnd(22));
        b.succeed_foreground = true;
    });
    let a = app(11, false, false);
    let out = activate(&api, &a);
    assert_eq!(out.target, popup);

    // BringToTop / SetForeground target the popup, not the root.
    let calls = api.calls();
    assert!(calls
        .iter()
        .filter_map(|c| match c { Call::BringToTop(h) | Call::SetForeground(h) => Some(*h), _ => None })
        .all(|h| h == popup));
}

#[test]
fn resolve_activation_target_falls_back_to_root_when_popup_invisible_or_self() {
    // Case A: popup == root.
    let api = MockWin32::new(100);
    let root = Hwnd(11);
    api.add_win(root, MockWin { visible: true, placement_show: 1, ..Default::default() });
    api.set_last_active_popup(root, root); // GetLastActivePopup returns self
    api.set_behavior(|b| {
        b.foreground = Some(Hwnd(22));
        b.succeed_foreground = true;
    });
    let out = activate(&api, &app(11, false, false));
    assert_eq!(out.target, root);

    // Case B: popup exists but is invisible.
    let api2 = MockWin32::new(100);
    let root2 = Hwnd(11);
    let popup2 = Hwnd(99);
    api2.add_win(root2, MockWin { visible: true, placement_show: 1, ..Default::default() });
    api2.add_win(popup2, MockWin { visible: false, ..Default::default() });
    api2.set_last_active_popup(root2, popup2);
    api2.set_behavior(|b| {
        b.foreground = Some(Hwnd(22));
        b.succeed_foreground = true;
    });
    let out2 = activate(&api2, &app(11, false, false));
    assert_eq!(out2.target, root2);
}

#[test]
fn catch_fallback_restores_iconic_and_switches_and_still_detaches() {
    // Inject a panic at the first SetForeground. The catch fallback should run
    // (IsIconic → SW_RESTORE, SwitchToThisWindow on target), and the finally
    // block should still detach the attached thread.
    let api = MockWin32::new(100);
    let root = Hwnd(11);
    api.add_win(
        root,
        MockWin {
            visible: true,
            iconic: true, // so the catch fallback's IsIconic → SW_RESTORE fires
            placement_show: 1,
            ..Default::default()
        },
    );
    api.set_behavior(|b| {
        b.foreground = Some(Hwnd(22)); // different thread → fg attach happens
        b.succeed_foreground = true;
        b.panic_at = Some("SetForeground");
    });
    let a = app(11, false, false);
    let out = activate(&api, &a);
    assert_eq!(out.target, root);

    let calls = api.calls();
    // Catch fallback: an extra ShowWindow(SW_RESTORE) and a SwitchToThisWindow.
    assert!(calls
        .iter()
        .any(|c| matches!(c, Call::ShowWindow(h, cmd) if *h == root && *cmd == SW_RESTORE)));
    assert_eq!(count(&calls, |c| matches!(c, Call::SwitchToThisWindow(_))), 1);
    // Finally: the fg attach was performed (before the panic) and must be detached.
    let attaches: Vec<_> = calls
        .iter()
        .filter_map(|c| match c { Call::Attach(a, b, true) => Some((*a, *b)), _ => None })
        .collect();
    let detaches: Vec<_> = calls
        .iter()
        .filter_map(|c| match c { Call::Detach(a, b) => Some((*a, *b)), _ => None })
        .collect();
    assert!(!attaches.is_empty(), "fg attach should have happened before panic");
    assert_eq!(attaches, detaches, "detach must mirror attach even after panic");
}
