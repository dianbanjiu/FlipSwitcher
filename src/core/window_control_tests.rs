//! Mock-driven tests for window close + process-tree termination.
//!
//! Verifies the contract from `docs/rust-rewrite-design-step4-5.md` §5-B:
//! WM_CLOSE retargeting to the enabled popup, the `CloseResult` keep/remove
//! decision, and the kill-tree post-order + per-pid failure swallowing.

#![allow(clippy::needless_borrow)]

use super::*;
use crate::core::app_window::AppWindow;
use crate::core::win32::{
    Gw, Gwlp, Hmonitor, IconImage, MonitorFlag, OwnedProcessHandle, Rect, Win32Error,
};
use std::collections::HashMap;
use std::sync::Mutex;

#[derive(Debug, Clone, PartialEq, Eq)]
enum Call {
    GetWindow(Hwnd, Gw),
    IsWindowVisible(Hwnd),
    PostClose(Hwnd),
    EnumerateProcessTree(u32),
    OpenProcessTerminate(u32),
    TerminateProcess(u32),
}

#[derive(Clone, Default)]
struct MockWin {
    visible: bool,
    enabled_popup: Option<Hwnd>,
}

#[derive(Default)]
struct KillMock {
    /// Pre-baked pid tree returned by `enumerate_process_tree`.
    tree: HashMap<u32, Vec<u32>>,
    /// Pids whose `open_process_terminate` should fail (return None).
    fail_open: std::collections::HashSet<u32>,
    /// Pids whose `terminate_process` should fail (return false).
    fail_terminate: std::collections::HashSet<u32>,
}

struct MockUniverse {
    wins: HashMap<Hwnd, MockWin>,
    calls: Vec<Call>,
    killed: Vec<u32>,
    kill: KillMock,
}

struct MockWin32 {
    state: Mutex<MockUniverse>,
}

impl MockWin32 {
    fn new() -> Self {
        Self {
            state: Mutex::new(MockUniverse {
                wins: HashMap::new(),
                calls: Vec::new(),
                killed: Vec::new(),
                kill: KillMock::default(),
            }),
        }
    }
    fn add_win(&self, hwnd: Hwnd, w: MockWin) {
        self.state.lock().unwrap().wins.insert(hwnd, w);
    }
    fn set_tree(&self, root: u32, children: Vec<u32>) {
        self.state.lock().unwrap().kill.tree.insert(root, children);
    }
    fn fail_open(&self, pid: u32) {
        self.state.lock().unwrap().kill.fail_open.insert(pid);
    }
    fn calls(&self) -> Vec<Call> {
        self.state.lock().unwrap().calls.clone()
    }
    fn killed(&self) -> Vec<u32> {
        self.state.lock().unwrap().killed.clone()
    }
}

impl crate::core::win32::Win32Api for MockWin32 {
    fn is_window_visible(&self, hwnd: Hwnd) -> bool {
        self.state.lock().unwrap().calls.push(Call::IsWindowVisible(hwnd));
        self.state
            .lock()
            .unwrap()
            .wins
            .get(&hwnd)
            .map(|w| w.visible)
            .unwrap_or(false)
    }
    fn get_window(&self, hwnd: Hwnd, cmd: Gw) -> Option<Hwnd> {
        self.state.lock().unwrap().calls.push(Call::GetWindow(hwnd, cmd));
        if cmd == Gw::EnabledPopup {
            self.state
                .lock()
                .unwrap()
                .wins
                .get(&hwnd)
                .and_then(|w| w.enabled_popup)
        } else {
            None
        }
    }
    fn post_close(&self, hwnd: Hwnd) {
        self.state.lock().unwrap().calls.push(Call::PostClose(hwnd));
    }
    fn enumerate_process_tree(&self, root_pid: u32) -> Vec<u32> {
        self.state.lock().unwrap().calls.push(Call::EnumerateProcessTree(root_pid));
        // Resolve the full tree via the pre-baked adjacency, post-order.
        let g = self.state.lock().unwrap();
        let mut out = Vec::new();
        let mut seen = std::collections::HashSet::new();
        fn walk(
            pid: u32,
            tree: &HashMap<u32, Vec<u32>>,
            seen: &mut std::collections::HashSet<u32>,
            out: &mut Vec<u32>,
        ) {
            if !seen.insert(pid) {
                return;
            }
            if let Some(children) = tree.get(&pid) {
                for &c in children {
                    walk(c, tree, seen, out);
                }
            }
            out.push(pid);
        }
        walk(root_pid, &g.kill.tree, &mut seen, &mut out);
        out
    }
    fn open_process_terminate(&self, pid: u32) -> Option<OwnedProcessHandle> {
        self.state.lock().unwrap().calls.push(Call::OpenProcessTerminate(pid));
        let g = self.state.lock().unwrap();
        if g.kill.fail_open.contains(&pid) {
            return None;
        }
        // Synthesize an owned handle. We can't make a real HANDLE here, so use
        // a sentinel raw pointer; Drop will CloseHandle it — unsafe but the
        // value is a non-null sentinel and CloseHandle on an invalid handle
        // simply returns false. To stay fully safe in tests we avoid Drop by
        // leaking: but OwnedProcessHandle always closes on drop. Instead,
        // return a handle wrapping pid in the low bits so terminate_process can
        // recover which pid it was.
        let raw = windows::Win32::Foundation::HANDLE(pid as *mut _);
        Some(OwnedProcessHandle(raw))
    }
    fn terminate_process(&self, handle: &OwnedProcessHandle, _exit_code: u32) -> bool {
        // Recover the pid we stashed in the handle.
        let pid = handle.raw().0 as u32;
        let mut g = self.state.lock().unwrap();
        g.calls.push(Call::TerminateProcess(pid));
        if g.kill.fail_terminate.contains(&pid) {
            return false;
        }
        g.killed.push(pid);
        true
    }

    // —— everything else: defaults (not exercised here) ——
    fn is_iconic(&self, _h: Hwnd) -> bool { false }
    fn is_zoomed(&self, _h: Hwnd) -> bool { false }
    fn get_window_text(&self, _h: Hwnd) -> String { String::new() }
    fn get_window_text_length(&self, _h: Hwnd) -> i32 { 0 }
    fn get_class_name(&self, _h: Hwnd) -> String { String::new() }
    fn get_window_long_ptr(&self, _h: Hwnd, _i: Gwlp) -> isize { 0 }
    fn get_window_rect(&self, _h: Hwnd) -> Option<Rect> { None }
    fn get_window_thread_process_id(&self, _h: Hwnd) -> (u32, u32) { (0, 0) }
    fn get_shell_window(&self) -> Option<Hwnd> { None }
    fn enum_windows(&self, _cb: &mut dyn FnMut(Hwnd) -> bool) -> Result<(), Win32Error> { Ok(()) }
    fn is_cloaked(&self, _h: Hwnd) -> bool { false }
    fn get_layered_window_attributes(&self, _h: Hwnd) -> Option<(u32, u8, u32)> { None }
    fn get_window_placement_show_cmd(&self, _h: Hwnd) -> Option<i32> { Some(1) }
    fn get_window_placement_flags(&self, _h: Hwnd) -> Option<i32> { Some(0) }
    fn enum_display_monitors(&self) -> Vec<(Hmonitor, Rect)> { Vec::new() }
    fn monitor_from_window(&self, _h: Hwnd, _f: MonitorFlag) -> Option<Hmonitor> { None }
    fn query_full_process_image_name(&self, _p: u32) -> Option<String> { None }
    fn open_process_query_limited(&self, _p: u32) -> Option<OwnedProcessHandle> { None }
    fn process_elevation(&self, _h: &OwnedProcessHandle) -> bool { false }
    fn current_process_id(&self) -> u32 { 1 }
    fn get_window_icon_handle(&self, _h: Hwnd) -> Option<isize> { None }
    fn get_foreground_window(&self) -> Option<Hwnd> { None }
    fn get_current_thread_id(&self) -> u32 { 0 }
    fn attach_thread_input(&self, _a: u32, _b: u32, _attach: bool) -> bool { false }
    fn allow_set_foreground_window_any(&self) -> bool { false }
    fn lock_set_foreground_window_unlock(&self) -> bool { false }
    fn show_window(&self, _h: Hwnd, _c: i32) -> bool { false }
    fn bring_window_to_top(&self, _h: Hwnd) -> bool { false }
    fn set_foreground_window(&self, _h: Hwnd) -> bool { false }
    fn switch_to_this_window(&self, _h: Hwnd, _alt_tab: bool) {}
    fn set_window_pos(
        &self,
        _h: Hwnd,
        _a: isize,
        _x: i32,
        _y: i32,
        _cx: i32,
        _cy: i32,
        _f: u32,
    ) -> bool {
        false
    }
    fn keybd_event(&self, _vk: u8, _flags: u32) {}
    fn get_last_active_popup(&self, hwnd: Hwnd) -> Option<Hwnd> { Some(hwnd) }
    fn is_key_down_async(&self, _vk: u32) -> bool { false }
    fn find_child_window_class_pid(
        &self,
        _parent: Hwnd,
        _class: &str,
        _exclude_pid: u32,
    ) -> Option<(Hwnd, u32)> {
        None
    }
    fn shell_extract_icon(&self, _e: &str) -> Option<IconImage> { None }
    fn extract_associated_icon(&self, _e: &str) -> Option<IconImage> { None }
    fn window_icon_to_image(&self, _h: isize) -> Option<IconImage> { None }
}

fn app_with(pid: u32, hwnd: i64) -> AppWindow {
    AppWindow {
        handle: Hwnd(hwnd as isize),
        title: String::new(),
        class_name: String::new(),
        process_id: pid,
        process_name: "mock".into(),
        is_minimized: false,
        is_maximized: false,
        is_topmost: false,
        owner_kept: None,
    }
}

#[test]
fn close_without_popup_targets_root_and_reports_root_closed() {
    let api = MockWin32::new();
    let root = Hwnd(11);
    api.add_win(root, MockWin { visible: true, enabled_popup: None });
    let res = close(&api, &app_with(1, 11));
    assert_eq!(res, CloseResult::RootClosed);
    assert!(api.calls().iter().any(|c| matches!(c, Call::PostClose(h) if *h == root)));
}

#[test]
fn close_when_popup_is_self_targets_root() {
    // GetWindow(GW_ENABLEDPOPUP) returning self (== root) → no modal popup.
    let api = MockWin32::new();
    let root = Hwnd(11);
    api.add_win(root, MockWin { visible: true, enabled_popup: Some(root) });
    let res = close(&api, &app_with(1, 11));
    assert_eq!(res, CloseResult::RootClosed);
    assert!(api.calls().iter().any(|c| matches!(c, Call::PostClose(h) if *h == root)));
}

#[test]
fn close_with_visible_popup_targets_popup_and_reports_dialog_dismissed() {
    let api = MockWin32::new();
    let root = Hwnd(11);
    let popup = Hwnd(42);
    api.add_win(root, MockWin { visible: true, enabled_popup: Some(popup) });
    api.add_win(popup, MockWin { visible: true, enabled_popup: None });
    let res = close(&api, &app_with(1, 11));
    assert_eq!(res, CloseResult::OnlyDialogDismissed);
    assert!(api.calls().iter().any(|c| matches!(c, Call::PostClose(h) if *h == popup)));
    // Root was NOT closed.
    assert!(!api.calls().iter().any(|c| matches!(c, Call::PostClose(h) if *h == root)));
}

#[test]
fn close_with_invisible_popup_targets_root() {
    let api = MockWin32::new();
    let root = Hwnd(11);
    let popup = Hwnd(42);
    api.add_win(root, MockWin { visible: true, enabled_popup: Some(popup) });
    api.add_win(popup, MockWin { visible: false, enabled_popup: None });
    let res = close(&api, &app_with(1, 11));
    assert_eq!(res, CloseResult::RootClosed);
    assert!(api.calls().iter().any(|c| matches!(c, Call::PostClose(h) if *h == root)));
}

#[test]
fn terminate_process_tree_kills_children_before_root_in_post_order() {
    // Tree: root(1) → {child(2), child(3)}, child(2) → grandchild(4).
    let api = MockWin32::new();
    api.set_tree(1, vec![2, 3]);
    api.set_tree(2, vec![4]);
    api.set_tree(3, vec![]);
    api.set_tree(4, vec![]);

    let ok = terminate_process_tree(&api, 1);
    assert!(ok, "root was terminated");

    let killed = api.killed();
    // All four killed.
    assert_eq!(killed.len(), 4);
    // Post-order: 4 and 3 (and 2) before 1.
    let pos_of = |pid: u32| killed.iter().position(|&p| p == pid).unwrap();
    assert!(pos_of(4) < pos_of(1));
    assert!(pos_of(2) < pos_of(1));
    assert!(pos_of(3) < pos_of(1));
    assert!(pos_of(4) < pos_of(2), "grandchild before its parent");
    // And open_process_terminate is called before terminate for each pid.
    let calls = api.calls();
    for pid in [1, 2, 3, 4] {
        let open = calls.iter().position(|c| matches!(c, Call::OpenProcessTerminate(p) if *p == pid)).unwrap();
        let term = calls.iter().position(|c| matches!(c, Call::TerminateProcess(p) if *p == pid)).unwrap();
        assert!(open < term, "open before terminate for pid {pid}");
    }
}

#[test]
fn terminate_process_tree_open_failure_skips_pid_but_continues_and_root_ok() {
    // child(2) fails to open; others succeed; root still terminated → true.
    let api = MockWin32::new();
    api.set_tree(1, vec![2, 3]);
    api.set_tree(2, vec![]);
    api.set_tree(3, vec![]);
    api.fail_open(2);

    let ok = terminate_process_tree(&api, 1);
    assert!(ok);
    let killed = api.killed();
    assert!(killed.contains(&1));
    assert!(killed.contains(&3));
    assert!(!killed.contains(&2), "pid 2 was skipped (open failed)");
}
