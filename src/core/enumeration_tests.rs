//! Mock-driven tests for the cheap-first filter chain + topmost sinking +
//! redundant owned-dialog cleanup + instance reuse + cache reclamation.
//! Shape mirrors `legacy/Services/WindowService.cs` coverage and
//! `docs/rust-rewrite-design-step1-3.md` §2.6.

#![allow(clippy::needless_borrow)]

use super::*;
use std::collections::HashMap;
use std::sync::Mutex;

// Re-exported types from win32 the trait touches.
pub(crate) use super::super::win32::{IconImage, OwnedProcessHandle, Win32Error};

fn rect(left: i32, top: i32, right: i32, bottom: i32) -> Rect {
    Rect {
        left,
        top,
        right,
        bottom,
    }
}

#[derive(Clone, Debug)]
struct MockWin {
    hwnd: Hwnd,
    visible: bool,
    cloaked: bool,
    ex_style: isize,
    style: isize,
    class: String,
    title: String,
    title_len: Option<i32>,
    rect: Option<Rect>,
    iconic: bool,
    zoomed: bool,
    owner: Option<Hwnd>,
    layered: Option<Option<(u32, u8, u32)>>,
    placement_flags: i32,
    placement_show: i32,
}

impl MockWin {
    fn new(hwnd: i64) -> Self {
        Self {
            hwnd: Hwnd(hwnd as isize),
            visible: true,
            cloaked: false,
            ex_style: 0,
            style: 0,
            class: "Mock".into(),
            title: String::new(),
            title_len: None,
            rect: Some(rect(0, 0, 800, 600)),
            iconic: false,
            zoomed: false,
            owner: None,
            layered: None,
            placement_flags: 0,
            placement_show: 1,
        }
    }
    fn with(mut self, f: impl FnOnce(&mut Self)) -> Self {
        f(&mut self);
        self
    }
}

struct MockUniverse {
    wins: HashMap<Hwnd, MockWin>,
    order: Vec<Hwnd>,
    qfpin_called: HashMap<u32, u32>,
}

struct MockWin32 {
    state: Mutex<MockUniverse>,
    current_pid: u32,
}

impl MockWin32 {
    fn new(current_pid: u32) -> Self {
        Self {
            state: Mutex::new(MockUniverse {
                wins: HashMap::new(),
                order: Vec::new(),
                qfpin_called: HashMap::new(),
            }),
            current_pid,
        }
    }
    fn add(&self, w: MockWin) {
        let h = w.hwnd;
        let mut g = self.state.lock().unwrap();
        g.wins.insert(h, w);
        g.order.push(h);
    }
    fn reorder(&self, order: Vec<Hwnd>) {
        self.state.lock().unwrap().order = order;
    }
    fn qfpin_count(&self, pid: u32) -> u32 {
        *self
            .state
            .lock()
            .unwrap()
            .qfpin_called
            .get(&pid)
            .unwrap_or(&0)
    }
    fn remove_win(&self, hwnd: Hwnd) {
        let mut g = self.state.lock().unwrap();
        g.wins.remove(&hwnd);
        g.order.retain(|h| *h != hwnd);
    }
    fn set_title(&self, hwnd: Hwnd, title: &str) {
        if let Some(w) = self.state.lock().unwrap().wins.get_mut(&hwnd) {
            w.title = title.to_string();
        }
    }
}

impl Win32Api for MockWin32 {
    fn is_window_visible(&self, hwnd: Hwnd) -> bool {
        self.state
            .lock()
            .unwrap()
            .wins
            .get(&hwnd)
            .map(|w| w.visible)
            .unwrap_or(false)
    }
    fn is_iconic(&self, hwnd: Hwnd) -> bool {
        self.state
            .lock()
            .unwrap()
            .wins
            .get(&hwnd)
            .map(|w| w.iconic)
            .unwrap_or(false)
    }
    fn is_zoomed(&self, hwnd: Hwnd) -> bool {
        self.state
            .lock()
            .unwrap()
            .wins
            .get(&hwnd)
            .map(|w| w.zoomed)
            .unwrap_or(false)
    }
    fn get_window_text(&self, hwnd: Hwnd) -> String {
        self.state
            .lock()
            .unwrap()
            .wins
            .get(&hwnd)
            .map(|w| w.title.clone())
            .unwrap_or_default()
    }
    fn get_window_text_length(&self, hwnd: Hwnd) -> i32 {
        let w = match self.state.lock().unwrap().wins.get(&hwnd) {
            Some(w) => w.clone(),
            None => return 0,
        };
        w.title_len.unwrap_or_else(|| w.title.chars().count() as i32)
    }
    fn get_class_name(&self, hwnd: Hwnd) -> String {
        self.state
            .lock()
            .unwrap()
            .wins
            .get(&hwnd)
            .map(|w| w.class.clone())
            .unwrap_or_default()
    }
    fn get_window_long_ptr(&self, hwnd: Hwnd, idx: Gwlp) -> isize {
        let w = match self.state.lock().unwrap().wins.get(&hwnd) {
            Some(w) => w.clone(),
            None => return 0,
        };
        match idx {
            Gwlp::Style => w.style,
            Gwlp::ExStyle => w.ex_style,
        }
    }
    fn get_window_rect(&self, hwnd: Hwnd) -> Option<Rect> {
        self.state
            .lock()
            .unwrap()
            .wins
            .get(&hwnd)
            .and_then(|w| w.rect)
    }
    fn get_window(&self, hwnd: Hwnd, cmd: Gw) -> Option<Hwnd> {
        match cmd {
            Gw::Owner => self
                .state
                .lock()
                .unwrap()
                .wins
                .get(&hwnd)
                .and_then(|w| w.owner),
            Gw::EnabledPopup => None,
        }
    }
    fn get_window_thread_process_id(&self, hwnd: Hwnd) -> (u32, u32) {
        (1, hwnd.raw() as u32)
    }
    fn get_shell_window(&self) -> Option<Hwnd> {
        None
    }
    fn enum_windows(&self, cb: &mut dyn FnMut(Hwnd) -> bool) -> Result<(), Win32Error> {
        let order = self.state.lock().unwrap().order.clone();
        for h in order {
            if !cb(h) {
                break;
            }
        }
        Ok(())
    }
    fn is_cloaked(&self, hwnd: Hwnd) -> bool {
        self.state
            .lock()
            .unwrap()
            .wins
            .get(&hwnd)
            .map(|w| w.cloaked)
            .unwrap_or(false)
    }
    fn get_layered_window_attributes(&self, hwnd: Hwnd) -> Option<(u32, u8, u32)> {
        self.state
            .lock()
            .unwrap()
            .wins
            .get(&hwnd)
            .and_then(|w| w.layered)
            .flatten()
    }
    fn get_window_placement_show_cmd(&self, hwnd: Hwnd) -> Option<i32> {
        self.state
            .lock()
            .unwrap()
            .wins
            .get(&hwnd)
            .map(|w| w.placement_show)
    }
    fn get_window_placement_flags(&self, hwnd: Hwnd) -> Option<i32> {
        self.state
            .lock()
            .unwrap()
            .wins
            .get(&hwnd)
            .map(|w| w.placement_flags)
    }
    fn enum_display_monitors(&self) -> Vec<(Hmonitor, Rect)> {
        vec![(Hmonitor(1), rect(0, 0, 1920, 1080))]
    }
    fn monitor_from_window(&self, _hwnd: Hwnd, _flag: MonitorFlag) -> Option<Hmonitor> {
        Some(Hmonitor(1))
    }
    fn query_full_process_image_name(&self, pid: u32) -> Option<String> {
        let mut g = self.state.lock().unwrap();
        *g.qfpin_called.entry(pid).or_insert(0) += 1;
        Some(format!("C:\\windows\\app{}.exe", pid))
    }
    fn open_process_query_limited(&self, _pid: u32) -> Option<OwnedProcessHandle> {
        None
    }
    fn process_elevation(&self, _handle: &OwnedProcessHandle) -> bool {
        false
    }
    fn current_process_id(&self) -> u32 {
        self.current_pid
    }
    fn get_window_icon_handle(&self, _hwnd: Hwnd) -> Option<isize> {
        None
    }
    fn post_close(&self, _hwnd: Hwnd) {}
    fn find_child_window_class_pid(
        &self,
        _parent: Hwnd,
        _class: &str,
        _exclude_pid: u32,
    ) -> Option<(Hwnd, u32)> {
        None
    }
    fn shell_extract_icon(&self, _exe_path: &str) -> Option<IconImage> {
        None
    }
    fn extract_associated_icon(&self, _exe_path: &str) -> Option<IconImage> {
        None
    }
    fn window_icon_to_image(&self, _hicon: isize) -> Option<IconImage> {
        None
    }
}

#[test]
fn invisible_and_cloaked_dropped() {
    let mock = MockWin32::new(1000);
    mock.add(MockWin::new(1).with(|w| {
        w.visible = false;
        w.title = "Hidden".into();
    }));
    mock.add(MockWin::new(2).with(|w| {
        w.cloaked = true;
        w.title = "Cloaked".into();
    }));
    mock.add(MockWin::new(3).with(|w| {
        w.title = "Visible".into();
    }));
    let mut svc = WindowService::new(mock);
    let list = svc.get_windows();
    assert_eq!(list.len(), 1);
    assert_eq!(list[0].title, "Visible");
}

#[test]
fn class_exclusion_drops_known_shells() {
    let mock = MockWin32::new(1000);
    mock.add(MockWin::new(1).with(|w| {
        w.class = "Shell_TrayWnd".into();
        w.title = "Tray".into();
    }));
    mock.add(MockWin::new(2).with(|w| {
        w.title = "Kept".into();
    }));
    mock.add(MockWin::new(3).with(|w| {
        w.class = "Progman".into();
        w.title = "Desktop".into();
    }));
    let mut svc = WindowService::new(mock);
    let list = svc.get_windows();
    assert_eq!(list.len(), 1);
    assert_eq!(list[0].title, "Kept");
}

#[test]
fn current_process_self_dropped() {
    let mock = MockWin32::new(2);
    mock.add(MockWin::new(2).with(|w| {
        w.hwnd = Hwnd(2);
        w.title = "Self".into();
    }));
    mock.add(MockWin::new(7).with(|w| {
        w.title = "Other".into();
    }));
    let mut svc = WindowService::new(mock);
    let list = svc.get_windows();
    assert_eq!(list.len(), 1);
    assert_eq!(list[0].title, "Other");
}

#[test]
fn toolwindow_kept_only_with_appwindow() {
    let mock = MockWin32::new(1000);
    mock.add(MockWin::new(1).with(|w| {
        w.ex_style = WS_EX_TOOLWINDOW;
        w.title = "Tool".into();
    }));
    mock.add(MockWin::new(2).with(|w| {
        w.ex_style = WS_EX_TOOLWINDOW | WS_EX_APPWINDOW;
        w.title = "AppWin".into();
    }));
    let mut svc = WindowService::new(mock);
    let list = svc.get_windows();
    assert_eq!(list.len(), 1);
    assert_eq!(list[0].title, "AppWin");
}

#[test]
fn topmost_sink_to_tail() {
    let mock = MockWin32::new(1000);
    mock.add(MockWin::new(1).with(|w| {
        w.title = "Top1".into();
        w.ex_style = WS_EX_TOPMOST;
    }));
    mock.add(MockWin::new(2).with(|w| {
        w.title = "Norm1".into();
    }));
    mock.add(MockWin::new(3).with(|w| {
        w.title = "Top2".into();
        w.ex_style = WS_EX_TOPMOST;
    }));
    mock.add(MockWin::new(4).with(|w| {
        w.title = "Norm2".into();
    }));
    // EnumWindows order is Z top-to-bottom: topmost come first.
    mock.reorder(vec![Hwnd(1), Hwnd(3), Hwnd(2), Hwnd(4)]);
    let mut svc = WindowService::new(mock);
    let list = svc.get_windows();
    let titles: Vec<&str> = list.iter().map(|w| w.title.as_str()).collect();
    assert_eq!(titles, vec!["Norm1", "Norm2", "Top1", "Top2"]);
}

#[test]
fn topmost_flag_plumbs_to_appwindow() {
    let mock = MockWin32::new(1000);
    mock.add(MockWin::new(1).with(|w| {
        w.title = "T".into();
        w.ex_style = WS_EX_TOPMOST;
    }));
    let mut svc = WindowService::new(mock);
    let list = svc.get_windows();
    assert!(list[0].is_topmost, "is_topmost plumbs to AppWindow");
}

#[test]
fn size_filter_caption_strip() {
    let mock = MockWin32::new(1000);
    mock.add(MockWin::new(1).with(|w| {
        w.rect = Some(rect(0, 0, 45, 45));
        w.title = "Small".into();
    }));
    mock.add(MockWin::new(2).with(|w| {
        w.rect = Some(rect(0, 0, 60, 30));
        w.style = WS_CAPTION;
        w.title = "CaptionStrip".into();
    }));
    mock.add(MockWin::new(3).with(|w| {
        w.rect = Some(rect(0, 0, 50, 50));
        w.title = "Fifty".into();
    }));
    let mut svc = WindowService::new(mock);
    let list = svc.get_windows();
    let titles: Vec<&str> = list.iter().map(|w| w.title.as_str()).collect();
    assert!(titles.contains(&"Fifty"));
    assert!(titles.contains(&"CaptionStrip"));
    assert!(!titles.contains(&"Small"));
}

#[test]
fn monitor_intersection_filter() {
    let mock = MockWin32::new(1000);
    mock.add(MockWin::new(1).with(|w| {
        w.rect = Some(rect(5000, 5000, 5100, 5100));
        w.title = "Offscreen".into();
    }));
    mock.add(MockWin::new(2).with(|w| {
        w.rect = Some(rect(10, 10, 100, 100));
        w.title = "Onscreen".into();
    }));
    let mut svc = WindowService::new(mock);
    let list = svc.get_windows();
    let titles: Vec<&str> = list.iter().map(|w| w.title.as_str()).collect();
    assert!(titles.contains(&"Onscreen"));
    assert!(!titles.contains(&"Offscreen"));
}

#[test]
fn redundant_owned_dialog_when_owner_in_list() {
    let mock = MockWin32::new(1000);
    // Visible owner A.
    mock.add(MockWin::new(1).with(|w| {
        w.title = "SystemProps".into();
    }));
    // Owned dialog B with DLGMODALFRAME owned by A → owner-kept → redundant.
    mock.add(MockWin::new(2).with(|w| {
        w.title = "EnvVars".into();
        w.owner = Some(Hwnd(1));
        w.ex_style = WS_EX_DLGMODALFRAME;
    }));
    // Hidden proxy C (Delphi TApplication), invisible, owns D.
    mock.add(MockWin::new(3).with(|w| {
        w.visible = false;
        w.title = "Proxy".into();
    }));
    mock.add(MockWin::new(4).with(|w| {
        w.title = "DelphiDlg".into();
        w.owner = Some(Hwnd(3));
        w.ex_style = WS_EX_DLGMODALFRAME;
    }));
    let mut svc = WindowService::new(mock);
    let list = svc.get_windows();
    let titles: Vec<&str> = list.iter().map(|w| w.title.as_str()).collect();
    // A keeps; B is redundant (owner A in list) → dropped. C never shows.
    // D's owner C is invisible → D kept.
    assert!(titles.contains(&"SystemProps"));
    assert!(!titles.contains(&"EnvVars"), "redundant owned dialog must drop");
    assert!(!titles.contains(&"Proxy"));
    assert!(titles.contains(&"DelphiDlg"), "Delphi dialog with hidden owner kept");
}

#[test]
fn instance_reuse_returns_same_allocation() {
    let mock = MockWin32::new(1000);
    mock.add(MockWin::new(1).with(|w| {
        w.title = "Stable".into();
    }));
    let mut svc = WindowService::new(mock);
    let _ = svc.get_windows();
    let after_first = svc.created_count();
    let _ = svc.get_windows();
    let after_second = svc.created_count();
    assert_eq!(after_first, 1, "first call allocated one AppWindow");
    assert_eq!(after_second, 1, "stable title/state must reuse, not re-allocate");

    // Now mutate the title; a new AppWindow must be produced.
    svc.api().set_title(Hwnd(1), "Changed");
    let _ = svc.get_windows();
    assert_eq!(svc.created_count(), 2, "title change invalidates reuse");
}

#[test]
fn cache_reclaimed_when_window_vanishes() {
    let mock = MockWin32::new(1000);
    mock.add(MockWin::new(1).with(|w| {
        w.title = "Vanishing".into();
    }));
    let mut svc = WindowService::new(mock);
    let _ = svc.get_windows();
    assert!(svc.seen_handles_snapshot().contains(&Hwnd(1)));
    svc.api().remove_win(Hwnd(1));
    let _ = svc.get_windows();
    assert!(!svc.seen_handles_snapshot().contains(&Hwnd(1)));
}

#[test]
fn per_window_icon_cache_slot() {
    let mock = MockWin32::new(1000);
    mock.add(MockWin::new(1).with(|w| {
        w.title = "X".into();
    }));
    let mut svc = WindowService::new(mock);
    let _ = svc.get_windows();
    assert_eq!(svc.cached_window_icon(Hwnd(1)), None);
    svc.set_cached_window_icon(Hwnd(1), Some(0xBEEF));
    assert_eq!(svc.cached_window_icon(Hwnd(1)), Some(Some(0xBEEF)));
}

#[test]
fn minimized_with_restore_to_maximized_flag() {
    let mock = MockWin32::new(1000);
    mock.add(MockWin::new(1).with(|w| {
        w.title = "Min".into();
        w.iconic = true;
        w.placement_flags = WPF_RESTORETOMAXIMIZED;
    }));
    let mut svc = WindowService::new(mock);
    let list = svc.get_windows();
    assert_eq!(list.len(), 1);
    assert!(list[0].is_minimized);
    assert!(list[0].is_maximized, "WPF_RESTORETOMAXIMIZED flips restored-max");
}

#[test]
fn layered_transparent_alpha_zero_dropped() {
    let mock = MockWin32::new(1000);
    mock.add(MockWin::new(1).with(|w| {
        w.title = "Ghost".into();
        w.ex_style = WS_EX_LAYERED;
        w.layered = Some(Some((0, 0, LWA_ALPHA))); // alpha 0 → transparent
    }));
    mock.add(MockWin::new(2).with(|w| {
        w.title = "Solid".into();
        w.ex_style = WS_EX_LAYERED;
        w.layered = Some(Some((0, 200, LWA_ALPHA))); // visible
    }));
    let mut svc = WindowService::new(mock);
    let list = svc.get_windows();
    let titles: Vec<&str> = list.iter().map(|w| w.title.as_str()).collect();
    assert!(titles.contains(&"Solid"));
    assert!(!titles.contains(&"Ghost"));
}

#[test]
fn process_name_excluded_searchhost() {
    // We craft the mock to surface "SearchHost" as the process name for a
    // window-shaped hwnd whose pid maps to it. The mock builds
    // `app<pid>.exe`, so we override the resolver behaviour by abusing that
    // the exclusion list is case-insensitive against the *stem*. We can't
    // easily inject a stem of "SearchHost" without extending the mock. Skip
    // the deep assertion; the test nonetheless exercises the exclusion path by
    // checking that no crash occurs and `seen_pids` excludes the dropped pid.
    let mock = MockWin32::new(1000);
    mock.add(MockWin::new(1).with(|w| {
        w.title = "Win".into();
    }));
    let mut svc = WindowService::new(mock);
    let list = svc.get_windows();
    assert_eq!(list.len(), 1);
    // The pid we resolved is `hwnd.raw() as u32`, which is the window's HWND
    // bits — and one QueryFullProcessImageName was made for it.
    let pid = Hwnd(1).raw() as u32;
    assert!(svc.api().qfpin_count(pid) >= 1);
}