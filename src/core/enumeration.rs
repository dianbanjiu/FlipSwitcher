//! Window enumeration + filtering.
//!
//! Faithful port of `legacy/Services/WindowService.cs` with the cache layout
//! from `docs/rust-rewrite-design-step1-3.md` §2.2–§2.4. Cheap-first filtering
//! order is a **contract** — see §2.4; do not reorder. Topmost windows sink to
//! the tail of the result. Redundant modal-child dialogs owned by a visible
//! window are dropped after enumeration.

use std::collections::{HashMap, HashSet};

use crate::core::app_window::AppWindow;
use crate::core::win32::{Gw, Gwlp, Hmonitor, Hwnd, MonitorFlag, Rect, Win32Api};

/// `WS_EX_DLGMODALFRAME` (0x1). Local constant — only this module knows about
/// the Delphi-VCL compatibility path, mirroring the C# `WindowService`.
const WS_EX_DLGMODALFRAME: isize = 0x0000_0001;
const WS_EX_LAYERED: isize = 0x0008_0000;
const WS_EX_TOOLWINDOW: isize = 0x0000_0080;
const WS_EX_APPWINDOW: isize = 0x0004_0000;
const WS_EX_NOACTIVATE: isize = 0x0800_0000;
const WS_EX_TOPMOST: isize = 0x0000_0008;
const WS_CAPTION: isize = 0x00C0_0000;
const LWA_COLORKEY: u32 = 0x1;
const LWA_ALPHA: u32 = 0x2;
const WPF_RESTORETOMAXIMIZED: i32 = 0x2;
const MIN_WINDOW_SIZE: i32 = 50;
const MIN_CAPTION_STRIP_HEIGHT: i32 = 28;

const EXCLUDED_CLASSES: &[&str] = &[
    "Progman",
    "Button",
    "Shell_TrayWnd",
    "Shell_SecondaryTrayWnd",
    "DV2ControlHost",
    "MssgrIMWindow",
    "SysShadow",
    "Xaml_WindowedPopupClass",
    "Windows.UI.Core.CoreWindow",
];

const EXCLUDED_PROCESSES: &[&str] = &[
    "SearchHost",
    "ShellExperienceHost",
    "StartMenuExperienceHost",
    "SearchUI",
    "LockApp",
    "TextInputHost",
];

/// Cheap-filter harvest for one HWND. Does **not** resolve the process name or
/// read the title text (those happen after exclusion passes). Kept as a top-
/// level pure function so tests can drive it directly against a `MockWin32`.
#[derive(Debug, Clone)]
struct WindowInfo {
    class_name: String,
    process_id: u32,
    is_topmost: bool,
    owner_kept: Option<Hwnd>,
    /// Raw title length from `GetWindowTextLength` (used by the caller to
    /// decide whether to read the body, and to drop no-title noise).
    title_len: i32,
    /// Whether `WS_EX_DLGMODALFRAME` was set — title-less dialog-frame windows
    /// are kept (display falls back to process name via `FormattedTitle`).
    has_dlg_frame: bool,
}

/// Window enumeration service. Generic over the Win32 binding so tests can
/// drive a `MockWin32`. Not thread-safe — the scratch buffers are reused and
/// hold `&mut self`; serialize calls behind the `is_refreshing` gate (§2.3).
pub struct WindowService<A: Win32Api> {
    api: A,
    // block A — process metadata, long-lived
    process_name_cache: HashMap<u32, String>,
    elevation_cache: HashMap<u32, bool>,
    // block B — externalised "on-demand" state from the C# AppWindow
    window_elevation: HashMap<Hwnd, bool>,
    window_monitor: HashMap<Hwnd, u32>,
    window_icon: HashMap<Hwnd, Option<isize>>,
    // block C — instance reuse
    window_instance_cache: HashMap<Hwnd, AppWindow>,
    // reused scratch
    monitors: Vec<Hmonitor>,
    monitor_rects: Vec<Rect>,
    windows_scratch: Vec<AppWindow>,
    topmost_scratch: Vec<AppWindow>,
    seen_handles: HashSet<Hwnd>,
    seen_pids: HashSet<u32>,
    owned_dialog_owners: HashMap<Hwnd, Hwnd>,
    #[cfg(test)]
    created_count: usize,
}

impl<A: Win32Api> WindowService<A> {
    pub fn new(api: A) -> Self {
        Self {
            api,
            process_name_cache: HashMap::new(),
            elevation_cache: HashMap::new(),
            window_elevation: HashMap::new(),
            window_monitor: HashMap::new(),
            window_icon: HashMap::new(),
            window_instance_cache: HashMap::new(),
            monitors: Vec::new(),
            monitor_rects: Vec::new(),
            windows_scratch: Vec::new(),
            topmost_scratch: Vec::new(),
            seen_handles: HashSet::new(),
            seen_pids: HashSet::new(),
            owned_dialog_owners: HashMap::new(),
            #[cfg(test)]
            created_count: 0,
        }
    }

    pub fn api(&self) -> &A {
        &self.api
    }

    pub fn cached_window_icon(&self, hwnd: Hwnd) -> Option<Option<isize>> {
        self.window_icon.get(&hwnd).copied()
    }

    pub fn set_cached_window_icon(&mut self, hwnd: Hwnd, icon: Option<isize>) {
        self.window_icon.insert(hwnd, icon);
    }

    pub fn monitors(&self) -> &[Hmonitor] {
        &self.monitors
    }

    pub fn monitor_index_of(&self, mon: Hmonitor) -> Option<u32> {
        self.monitors
            .iter()
            .position(|m| *m == mon)
            .map(|i| (i as u32) + 1)
    }

    pub fn monitor_number(&self, hwnd: Hwnd) -> u32 {
        match self.api.monitor_from_window(hwnd, MonitorFlag::DefaultToNearest) {
            Some(m) => self.monitor_index_of(m).unwrap_or(1),
            None => 1,
        }
    }

    pub fn is_elevated(&mut self, hwnd: Hwnd) -> bool {
        if let Some(v) = self.window_elevation.get(&hwnd) {
            return *v;
        }
        let (_, pid) = self.api.get_window_thread_process_id(hwnd);
        let v = if let Some(cached) = self.elevation_cache.get(&pid) {
            *cached
        } else {
            let h = self.api.open_process_query_limited(pid);
            let e = h.map(|h| self.api.process_elevation(&h)).unwrap_or(false);
            self.elevation_cache.insert(pid, e);
            e
        };
        self.window_elevation.insert(hwnd, v);
        v
    }

    /// Top-level entry — enumerate, filter, cache, compose the final list in
    /// MRU-ish order with topmost windows sunk to the tail.
    pub fn get_windows(&mut self) -> Vec<AppWindow> {
        // (a) refresh monitors
        let monitors = self.api.enum_display_monitors();
        self.monitors.clear();
        self.monitor_rects.clear();
        for (m, r) in &monitors {
            self.monitors.push(*m);
            self.monitor_rects.push(*r);
        }

        // (b) bookkeeping
        let shell_window = self.api.get_shell_window();
        let current_pid = self.api.current_process_id();

        // (c) reset scratch
        self.windows_scratch.clear();
        self.topmost_scratch.clear();
        self.seen_handles.clear();
        self.seen_pids.clear();
        self.owned_dialog_owners.clear();

        // (d) collect HWNDs first so the callback never borrows `&mut self`.
        let mut hwnds: Vec<Hwnd> = Vec::new();
        {
            let mut cb = |hwnd: Hwnd| -> bool {
                hwnds.push(hwnd);
                true
            };
            let _ = self.api.enum_windows(&mut cb);
        }

        // (d') per HWND: run cheap filters + harvest info, push into the right
        // bucket. Caches updated in place.
        for hwnd in hwnds {
            let info = match try_get_window_info(
                &self.api,
                hwnd,
                shell_window,
                current_pid,
                &self.monitor_rects,
            ) {
                None => continue,
                Some(i) => i,
            };

            self.seen_handles.insert(hwnd);
            self.seen_pids.insert(info.process_id);

            if let Some(owner) = info.owner_kept {
                self.owned_dialog_owners.insert(hwnd, owner);
            }

            // Resolve process name (cached per pid) + process exclusion list.
            let process_name = match self.process_name_cache.get(&info.process_id) {
                Some(n) => n.clone(),
                None => {
                    let path = self.api.query_full_process_image_name(info.process_id);
                    let name = path
                        .as_deref()
                        .and_then(|p| {
                            std::path::Path::new(p)
                                .file_stem()
                                .map(|s| s.to_string_lossy().into_owned())
                        })
                        .unwrap_or_else(|| "Unknown".to_string());
                    self.process_name_cache.insert(info.process_id, name.clone());
                    name
                }
            };
            if EXCLUDED_PROCESSES
                .iter()
                .any(|p| p.eq_ignore_ascii_case(&process_name))
            {
                continue;
            }

            // Read title (whitespace-only titles are noise unless dlg-frame).
            let title = if info.title_len <= 0 {
                String::new()
            } else {
                let text = self.api.get_window_text(hwnd);
                if text.trim().is_empty() {
                    if !info.has_dlg_frame {
                        // Re-derive: a window that *had* a non-zero title_len but
                        // whose body is whitespace-only must be dropped — we returned
                        // it from the filter, so undo it here.
                        continue;
                    }
                    String::new()
                } else {
                    text
                }
            };

            let state = get_window_state(&self.api, hwnd);

            // Instance reuse: title + process + state match → reuse old instance.
            let reused = match self.window_instance_cache.get(&hwnd) {
                Some(existing)
                    if existing.title == title
                        && existing.process_name == process_name
                        && existing.is_minimized == state.0
                        && existing.is_maximized == state.1 =>
                {
                    Some(existing.clone())
                }
                _ => None,
            };
            let window = match reused {
                Some(existing) => existing,
                None => {
                    #[cfg(test)]
                    {
                        self.created_count += 1;
                    }
                    AppWindow {
                        handle: hwnd,
                        title,
                        class_name: info.class_name.clone(),
                        process_id: info.process_id,
                        process_name: process_name.clone(),
                        is_minimized: state.0,
                        is_maximized: state.1,
                        is_topmost: info.is_topmost,
                        owner_kept: info.owner_kept,
                    }
                }
            };
            self.window_instance_cache.insert(hwnd, window.clone());
            if info.is_topmost {
                self.topmost_scratch.push(window);
            } else {
                self.windows_scratch.push(window);
            }
        }

        // (e) cache reclamation
        self.reclaim_caches();

        // (f) compose final list: windows_scratch, then topmost_scratch,
        // skipping redundant owned dialogs.
        let mut result: Vec<AppWindow> =
            Vec::with_capacity(self.windows_scratch.len() + self.topmost_scratch.len());
        for w in &self.windows_scratch {
            if !self.is_redundant_owned_dialog(w.handle) {
                result.push(w.clone());
            }
        }
        for w in &self.topmost_scratch {
            if !self.is_redundant_owned_dialog(w.handle) {
                result.push(w.clone());
            }
        }
        result
    }

    fn reclaim_caches(&mut self) {
        // Drop per-window caches for HWNDs no longer seen.
        if self.window_instance_cache.len() > self.seen_handles.len() {
            let stale: Vec<Hwnd> = self
                .window_instance_cache
                .keys()
                .filter(|k| !self.seen_handles.contains(k))
                .copied()
                .collect();
            for k in stale {
                self.window_instance_cache.remove(&k);
                self.window_elevation.remove(&k);
                self.window_monitor.remove(&k);
                self.window_icon.remove(&k);
            }
        }
        // Drop process-name / elevation when the metacache has grown past 2x
        // the seen pids — keeps long sessions bounded.
        if !self.seen_pids.is_empty()
            && self.process_name_cache.len() > self.seen_pids.len() * 2
        {
            let stale: Vec<u32> = self
                .process_name_cache
                .keys()
                .filter(|p| !self.seen_pids.contains(p))
                .copied()
                .collect();
            for p in stale {
                self.process_name_cache.remove(&p);
                self.elevation_cache.remove(&p);
            }
            // Step 3 wires `icon_loader::trim_process_cache(seen_pids)` here.
        }
    }

    fn is_redundant_owned_dialog(&self, hwnd: Hwnd) -> bool {
        match self.owned_dialog_owners.get(&hwnd) {
            Some(owner) => self.seen_handles.contains(owner),
            None => false,
        }
    }

    /// Test seam: expose whether a window is redundant for unit tests.
    #[cfg(test)]
    pub(crate) fn is_redundant_owned_dialog_pub(&self, hwnd: Hwnd) -> bool {
        self.is_redundant_owned_dialog(hwnd)
    }

    /// Test seam: expose the owned-dialog-owners map for unit-test introspection.
    #[cfg(test)]
    pub(crate) fn owned_dialog_owners_snapshot(&self) -> &HashMap<Hwnd, Hwnd> {
        &self.owned_dialog_owners
    }

    /// Test seam: expose `seen_handles`.
    #[cfg(test)]
    pub(crate) fn seen_handles_snapshot(&self) -> &HashSet<Hwnd> {
        &self.seen_handles
    }

    /// Test seam: expose `seen_pids`.
    #[cfg(test)]
    pub(crate) fn seen_pids_snapshot(&self) -> &HashSet<u32> {
        &self.seen_pids
    }

    /// Test seam: number of brand-new `AppWindow` allocations the service
    /// produced — used to assert instance reuse hit.
    #[cfg(test)]
    pub(crate) fn created_count(&self) -> usize {
        self.created_count
    }
}

/// `(is_minimized, is_maximized)` taking `WPF_RESTORETOMAXIMIZED` into account.
fn get_window_state<A: Win32Api>(api: &A, hwnd: Hwnd) -> (bool, bool) {
    let is_minimized = api.is_iconic(hwnd);
    let mut is_maximized = api.is_zoomed(hwnd);
    if is_minimized {
        if let Some(flags) = api.get_window_placement_flags(hwnd) {
            if (flags & WPF_RESTORETOMAXIMIZED) != 0 {
                is_maximized = true;
            }
        }
    }
    (is_minimized, is_maximized)
}

/// Cheap-first filter + info harvest. Pure function of the API — no caches.
/// Steps cited are §2.4 of the step doc; do not reorder. `None` = dropped.
fn try_get_window_info<A: Win32Api>(
    api: &A,
    hwnd: Hwnd,
    shell_window: Option<Hwnd>,
    current_pid: u32,
    monitor_rects: &[Rect],
) -> Option<WindowInfo> {
    // 1) shell window / invisible
    if Some(hwnd) == shell_window || !api.is_window_visible(hwnd) {
        return None;
    }
    // 2) cloaked
    if api.is_cloaked(hwnd) {
        return None;
    }
    // 3) exStyle (reused below)
    let ex_style = api.get_window_long_ptr(hwnd, Gwlp::ExStyle);
    if is_fully_transparent_layered(api, hwnd, ex_style) {
        return None;
    }
    let is_app_window = (ex_style & WS_EX_APPWINDOW) != 0;
    // 5) toolwindow / noactivate that aren't APPWINDOW
    if (ex_style & WS_EX_TOOLWINDOW) != 0 && !is_app_window {
        return None;
    }
    if (ex_style & WS_EX_NOACTIVATE) != 0 && !is_app_window {
        return None;
    }
    // 6) iconic + size / monitor intersection
    let is_iconic = api.is_iconic(hwnd);
    if !is_iconic && !has_valid_window_size(api, hwnd) {
        return None;
    }
    if !is_iconic
        && !monitor_rects.is_empty()
        && !window_intersects_any_monitor(api, hwnd, monitor_rects)
    {
        return None;
    }
    // 7) owner chain
    let mut owner_kept: Option<Hwnd> = None;
    let owner = api.get_window(hwnd, Gw::Owner);
    if let Some(owner) = owner {
        if !is_app_window {
            let mut cur = Some(owner);
            while let Some(c) = cur {
                if api.is_window_visible(c) {
                    if is_likely_user_owned_dialog(api, hwnd, ex_style) {
                        owner_kept = Some(c);
                        break;
                    } else {
                        return None;
                    }
                }
                cur = api.get_window(c, Gw::Owner);
            }
        }
    }
    // 8) title length + dialog frame
    let title_len = api.get_window_text_length(hwnd);
    let has_dlg_frame = (ex_style & WS_EX_DLGMODALFRAME) != 0;
    if title_len == 0 && !has_dlg_frame {
        return None;
    }
    // 9) class name exclusion
    let class_name = api.get_class_name(hwnd);
    if EXCLUDED_CLASSES
        .iter()
        .any(|c| c.eq_ignore_ascii_case(&class_name))
    {
        return None;
    }
    // 10) pid + exclude current process
    let (_, pid) = api.get_window_thread_process_id(hwnd);
    if pid == current_pid {
        return None;
    }

    Some(WindowInfo {
        class_name,
        process_id: pid,
        is_topmost: (ex_style & WS_EX_TOPMOST) != 0,
        owner_kept,
        title_len,
        has_dlg_frame,
    })
}

fn is_fully_transparent_layered<A: Win32Api>(api: &A, hwnd: Hwnd, ex_style: isize) -> bool {
    if (ex_style & WS_EX_LAYERED) == 0 {
        return false;
    }
    match api.get_layered_window_attributes(hwnd) {
        None => false,
        Some((_key, alpha, flags)) => {
            if flags == 0 {
                return true;
            }
            if (flags & LWA_ALPHA) != 0 && alpha == 0 {
                return true;
            }
            if (flags & LWA_ALPHA) == 0 && (flags & LWA_COLORKEY) != 0 {
                return true;
            }
            false
        }
    }
}

fn is_likely_user_owned_dialog<A: Win32Api>(api: &A, hwnd: Hwnd, ex_style: isize) -> bool {
    if (ex_style & WS_EX_DLGMODALFRAME) != 0 {
        return true;
    }
    api.get_window_text_length(hwnd) > 0
}

fn has_valid_window_size<A: Win32Api>(api: &A, hwnd: Hwnd) -> bool {
    let Some(r) = api.get_window_rect(hwnd) else {
        return false;
    };
    let w = r.width();
    let h = r.height();
    if w >= MIN_WINDOW_SIZE && h >= MIN_WINDOW_SIZE {
        return true;
    }
    let style = api.get_window_long_ptr(hwnd, Gwlp::Style);
    let has_caption = (style & WS_CAPTION) == WS_CAPTION;
    has_caption && w >= MIN_WINDOW_SIZE && h >= MIN_CAPTION_STRIP_HEIGHT
}

fn window_intersects_any_monitor<A: Win32Api>(api: &A, hwnd: Hwnd, rects: &[Rect]) -> bool {
    let Some(r) = api.get_window_rect(hwnd) else {
        return false;
    };
    rects.iter().any(|m| r.intersects(*m))
}
#[cfg(test)]
#[path = "enumeration_tests.rs"]
mod mock_tests;
