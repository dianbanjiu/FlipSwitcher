//! Multi-monitor workspace, DPI scaling and mouse-screen placement.
//!
//! 1:1 port of the placement logic in `legacy/Views/MainWindow.xaml.cs`:
//! - default path: centre on the **primary** monitor's work area
//!   (`SystemParameters.WorkArea`), expected logical size constant;
//! - `ShowOnMouseScreen` path: locate the monitor under the cursor
//!   (`MonitorFromPoint` + `GetMonitorInfo`), scale the expected size by that
//!   monitor's effective DPI (`GetDpiForMonitor`), centre on its `rcWork`.
//!
//! All placement decisions live in the pure function [`compute_placement`]; the
//! only thing [`Monitors`] does is gather raw values from [`Win32Api`]. Failures
//! at every Win32 call degrade to a sane default rather than panic — matching
//! the C# `if (!GetCursorPos(...)) return;` style. See
//! `docs/rust-rewrite-design-step6.md`.

use crate::core::win32::{Hmonitor, Rect, Win32Api};

/// Expected switcher window size in *logical* pixels (WPF DIPs), matching the
/// `MainWindow` constants the C# version sizes against. Pixel size is derived
/// from these via [`pixels_for_logical`] with the target monitor's DPI.
const EXPECTED_LOGICAL_W: i32 = 640;
const EXPECTED_LOGICAL_H: i32 = 520;

/// One monitor's geometry. `work` excludes the taskbar and is what placement
/// centres against; `monitor` is the full screen rect.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MonitorInfo {
    pub handle: Hmonitor,
    pub work: Rect,
    pub monitor: Rect,
    pub is_primary: bool,
    /// Effective DPI along x (== y for effective DPI), raw value where 96 ==
    /// scale 1.0. Stored as an integer to keep placement math integer-only.
    pub dpi_scale: u32,
}

/// Convert a logical (96-DIP) length to physical pixels for a monitor whose
/// DPI is `dpi_x`. Integer approximation of `(logical * dpi_x / 96.0)`, matching
/// the C# `(int)(640 * dpiX / 96.0)` rounding behaviour to within ≤1px.
pub fn pixels_for_logical(logical: i32, dpi_x: u32) -> i32 {
    // (logical * dpi_x + 95) / 96 → rounds to nearest instead of flooring, so
    // we stay within ≤1px of the floating-point reference. `logical` is small
    // (≤ ~1k) and `dpi_x` ≤ ~300 so the product fits in i64 comfortably.
    let n = (logical as i64) * (dpi_x as i64) + 95;
    (n / 96) as i32
}

/// Pixel rectangle the switcher's main window should occupy on screen.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Placement {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
}

/// Which centre-on strategy [`compute_placement`] should apply.
pub enum PlacementMode<'a> {
    /// `ShowOnMouseScreen = false` — centre on the primary monitor's work area,
    /// sizing by the primary monitor's DPI.
    PrimaryCenter {
        primary_work: &'a Rect,
        primary_dpi: u32,
    },
    /// `ShowOnMouseScreen = true` — centre on the cursor's monitor work area,
    /// sizing by that monitor's DPI.
    MouseCenter { mouse: &'a MonitorInfo },
}

/// Compute the switcher's placement rectangle. Pure — no Win32 calls.
///
/// Both branches centre the (DPI-scaled) expected size inside the chosen work
/// rect. Integer division truncates toward zero, matching the C# `(a - b) / 2`
/// behaviour when the centring slack is odd.
pub fn compute_placement(mode: PlacementMode<'_>) -> Placement {
    match mode {
        PlacementMode::PrimaryCenter {
            primary_work,
            primary_dpi,
        } => {
            let w = pixels_for_logical(EXPECTED_LOGICAL_W, primary_dpi);
            let h = pixels_for_logical(EXPECTED_LOGICAL_H, primary_dpi);
            let x = primary_work.left + (primary_work.width() - w) / 2;
            let y = primary_work.top + (primary_work.height() - h) / 2;
            Placement {
                x,
                y,
                width: w,
                height: h,
            }
        }
        PlacementMode::MouseCenter { mouse } => {
            let w = pixels_for_logical(EXPECTED_LOGICAL_W, mouse.dpi_scale);
            let h = pixels_for_logical(EXPECTED_LOGICAL_H, mouse.dpi_scale);
            let r = mouse.work;
            let x = r.left + (r.width() - w) / 2;
            let y = r.top + (r.height() - h) / 2;
            Placement {
                x,
                y,
                width: w,
                height: h,
            }
        }
    }
}

/// Monitors service. Holds only the [`Win32Api`] binding; [`snapshot`] /
/// [`mouse_monitor`] read fresh each call. Safe to share and call concurrently
/// — no interior mutability.
pub struct Monitors<A: Win32Api> {
    api: A,
}

impl<A: Win32Api> Monitors<A> {
    pub fn new(api: A) -> Self {
        Self { api }
    }

    pub fn api(&self) -> &A {
        &self.api
    }

    /// Enumerate every monitor with its work rect, full rect, primary flag and
    /// DPI. Order follows `enum_display_monitors`. Monitors whose
    /// `GetMonitorInfo` fails are skipped (matches `snapshot` never panicking).
    pub fn snapshot(&self) -> Vec<MonitorInfo> {
        let mut out = Vec::new();
        for (hmon, _rc) in self.api.enum_display_monitors() {
            let (work, monitor, is_primary) = match self.api.get_monitor_info(hmon) {
                Some(v) => v,
                None => continue,
            };
            let dpi_scale = self.api.get_dpi_for_monitor(hmon);
            out.push(MonitorInfo {
                handle: hmon,
                work,
                monitor,
                is_primary,
                dpi_scale,
            });
        }
        out
    }

    /// The monitor under the cursor (`MonitorFromPoint` + `GetMonitorInfo`).
    /// `None` when the cursor can't be read or no monitor matches. Resolves the
    /// monitor by matching the snapshot's `handle` so callers get a populated
    /// [`MonitorInfo`] with `work`/`dpi_scale` already filled.
    pub fn mouse_monitor(&self) -> Option<MonitorInfo> {
        let (cx, cy) = self.api.get_cursor_pos()?;
        let hmon = self.api.monitor_from_point_nearest(cx, cy)?;
        self.snapshot()
            .into_iter()
            .find(|m| m.handle == hmon)
            // `MonitorFromPoint` returned a handle, so `GetMonitorInfo` should
            // succeed for it; if the snapshot skipped it for some reason we
            // still build the entry from a direct query as a last resort.
            .or_else(|| {
                let (work, monitor, is_primary) = self.api.get_monitor_info(hmon)?;
                Some(MonitorInfo {
                    handle: hmon,
                    work,
                    monitor,
                    is_primary,
                    dpi_scale: self.api.get_dpi_for_monitor(hmon),
                })
            })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::core::win32::{
        Gwlp, Gw, IconImage, MonitorFlag, OwnedProcessHandle, OwnedIcon, Win32Error,
    };
    use std::collections::HashMap;
    use std::sync::Mutex;

    fn rect(l: i32, t: i32, r: i32, b: i32) -> Rect {
        Rect {
            left: l,
            top: t,
            right: r,
            bottom: b,
        }
    }

    /// A mock that lets tests inject a monitor table + cursor position + DPI
    /// failures. Only the monitor-related [`Win32Api`] methods are meaningful;
    /// everything else returns defaults. Keep this deliberately local so the
    /// monitors tests don't grow entangled with the enumeration-test mock.
    struct MonMock {
        monitors: Mutex<Vec<MockMon>>,
        cursor: Mutex<Option<(i32, i32)>>,
    }

    #[derive(Clone)]
    struct MockMon {
        handle: Hmonitor,
        work: Rect,
        monitor: Rect,
        primary: bool,
        dpi: u32,
        /// When true, `get_monitor_info` returns `None` (simulating failure).
        info_fails: bool,
        /// When true, `get_dpi_for_monitor` returns the failure fallback (96).
        dpi_fails: bool,
    }

    impl MonMock {
        fn new() -> Self {
            Self {
                monitors: Mutex::new(Vec::new()),
                cursor: Mutex::new(None),
            }
        }
        fn add(&self, m: MockMon) {
            self.monitors.lock().unwrap().push(m);
        }
        fn set_cursor(&self, c: Option<(i32, i32)>) {
            *self.cursor.lock().unwrap() = c;
        }
    }

    impl Win32Api for MonMock {
        fn enum_display_monitors(&self) -> Vec<(Hmonitor, Rect)> {
            self.monitors
                .lock()
                .unwrap()
                .iter()
                .map(|m| (m.handle, m.monitor))
                .collect()
        }
        fn get_monitor_info(&self, hmon: Hmonitor) -> Option<(Rect, Rect, bool)> {
            self.monitors
                .lock()
                .unwrap()
                .iter()
                .find(|m| m.handle == hmon && !m.info_fails)
                .map(|m| (m.work, m.monitor, m.primary))
        }
        fn get_dpi_for_monitor(&self, hmon: Hmonitor) -> u32 {
            self.monitors
                .lock()
                .unwrap()
                .iter()
                .find(|m| m.handle == hmon)
                .map(|m| if m.dpi_fails { 96 } else { m.dpi })
                .unwrap_or(96)
        }
        fn get_cursor_pos(&self) -> Option<(i32, i32)> {
            *self.cursor.lock().unwrap()
        }
        fn monitor_from_point_nearest(&self, x: i32, y: i32) -> Option<Hmonitor> {
            // First monitor whose *monitor* rect contains the point; matches
            // `MONITOR_DEFAULTTONEAREST` behaviour for points on-screen. For
            // points off-screen the real API returns the nearest — we approximate
            // by returning the nearest by centre distance.
            let mons = self.monitors.lock().unwrap();
            for m in mons.iter() {
                if x >= m.monitor.left && x < m.monitor.right && y >= m.monitor.top && y < m.monitor.bottom
                {
                    return Some(m.handle);
                }
            }
            // nearest by centre
            mons.iter()
                .min_by_key(|m| {
                    let cx = (m.monitor.left + m.monitor.right) / 2;
                    let cy = (m.monitor.top + m.monitor.bottom) / 2;
                    (cx - x).abs() + (cy - y).abs()
                })
                .map(|m| m.handle)
        }
        // The rest: defaults / harmless dummies. Not exercised here.
        fn is_window_visible(&self, _: crate::core::win32::Hwnd) -> bool {
            false
        }
        fn is_iconic(&self, _: crate::core::win32::Hwnd) -> bool {
            false
        }
        fn is_zoomed(&self, _: crate::core::win32::Hwnd) -> bool {
            false
        }
        fn get_window_text(&self, _: crate::core::win32::Hwnd) -> String {
            String::new()
        }
        fn get_window_text_length(&self, _: crate::core::win32::Hwnd) -> i32 {
            0
        }
        fn get_class_name(&self, _: crate::core::win32::Hwnd) -> String {
            String::new()
        }
        fn get_window_long_ptr(&self, _: crate::core::win32::Hwnd, _: Gwlp) -> isize {
            0
        }
        fn get_window_rect(&self, _: crate::core::win32::Hwnd) -> Option<Rect> {
            None
        }
        fn get_window(&self, _: crate::core::win32::Hwnd, _: Gw) -> Option<crate::core::win32::Hwnd> {
            None
        }
        fn get_window_thread_process_id(&self, _: crate::core::win32::Hwnd) -> (u32, u32) {
            (0, 0)
        }
        fn get_shell_window(&self) -> Option<crate::core::win32::Hwnd> {
            None
        }
        fn enum_windows(
            &self,
            _: &mut dyn FnMut(crate::core::win32::Hwnd) -> bool,
        ) -> Result<(), Win32Error> {
            Ok(())
        }
        fn is_cloaked(&self, _: crate::core::win32::Hwnd) -> bool {
            false
        }
        fn get_layered_window_attributes(&self, _: crate::core::win32::Hwnd) -> Option<(u32, u8, u32)> {
            None
        }
        fn get_window_placement_show_cmd(&self, _: crate::core::win32::Hwnd) -> Option<i32> {
            None
        }
        fn get_window_placement_flags(&self, _: crate::core::win32::Hwnd) -> Option<i32> {
            None
        }
        fn monitor_from_window(
            &self,
            _: crate::core::win32::Hwnd,
            _: MonitorFlag,
        ) -> Option<Hmonitor> {
            None
        }
        fn query_full_process_image_name(&self, _: u32) -> Option<String> {
            None
        }
        fn open_process_query_limited(&self, _: u32) -> Option<OwnedProcessHandle> {
            None
        }
        fn process_elevation(&self, _: &OwnedProcessHandle) -> bool {
            false
        }
        fn current_process_id(&self) -> u32 {
            0
        }
        fn get_window_icon_handle(&self, _: crate::core::win32::Hwnd) -> Option<isize> {
            None
        }
        fn post_close(&self, _: crate::core::win32::Hwnd) {}
        fn get_foreground_window(&self) -> Option<crate::core::win32::Hwnd> {
            None
        }
        fn get_current_thread_id(&self) -> u32 {
            0
        }
        fn attach_thread_input(&self, _: u32, _: u32, _: bool) -> bool {
            false
        }
        fn allow_set_foreground_window_any(&self) -> bool {
            false
        }
        fn lock_set_foreground_window_unlock(&self) -> bool {
            false
        }
        fn show_window(&self, _: crate::core::win32::Hwnd, _: i32) -> bool {
            false
        }
        fn bring_window_to_top(&self, _: crate::core::win32::Hwnd) -> bool {
            false
        }
        fn set_foreground_window(&self, _: crate::core::win32::Hwnd) -> bool {
            false
        }
        fn switch_to_this_window(&self, _: crate::core::win32::Hwnd, _: bool) {}
        fn set_window_pos(
            &self,
            _: crate::core::win32::Hwnd,
            _: isize,
            _: i32,
            _: i32,
            _: i32,
            _: i32,
            _: u32,
        ) -> bool {
            false
        }
        fn keybd_event(&self, _: u8, _: u32) {}
        fn get_last_active_popup(&self, _: crate::core::win32::Hwnd) -> Option<crate::core::win32::Hwnd> {
            None
        }
        fn open_process_terminate(&self, _: u32) -> Option<OwnedProcessHandle> {
            None
        }
        fn terminate_process(&self, _: &OwnedProcessHandle, _: u32) -> bool {
            false
        }
        fn enumerate_process_tree(&self, _: u32) -> Vec<u32> {
            Vec::new()
        }
        fn is_key_down_async(&self, _: u32) -> bool {
            false
        }
        fn find_child_window_class_pid(
            &self,
            _: crate::core::win32::Hwnd,
            _: &str,
            _: u32,
        ) -> Option<(crate::core::win32::Hwnd, u32)> {
            None
        }
        fn shell_extract_icon(&self, _: &str) -> Option<IconImage> {
            None
        }
        fn extract_associated_icon(&self, _: &str) -> Option<IconImage> {
            None
        }
        fn window_icon_to_image(&self, _: isize) -> Option<IconImage> {
            None
        }
    }

    #[allow(dead_code)]
    fn _silence() {
        let _ = OwnedIcon(0);
        let _: HashMap<(), ()> = HashMap::new();
    }

    fn mon(
        handle: isize,
        work: Rect,
        monitor: Rect,
        primary: bool,
        dpi: u32,
    ) -> MockMon {
        MockMon {
            handle: Hmonitor(handle),
            work,
            monitor,
            primary,
            dpi,
            info_fails: false,
            dpi_fails: false,
        }
    }

    /// Build a `MonitorInfo` with the same fields as a `MockMon` (for the pure
    /// `compute_placement` tests — placement never touches Win32).
    fn info(m: &MockMon) -> MonitorInfo {
        MonitorInfo {
            handle: m.handle,
            work: m.work,
            monitor: m.monitor,
            is_primary: m.primary,
            dpi_scale: m.dpi,
        }
    }

    // —— pixels_for_logical ——

    #[test]
    fn pixels_for_logical_at_96_is_identity() {
        assert_eq!(pixels_for_logical(640, 96), 640);
        assert_eq!(pixels_for_logical(520, 96), 520);
    }

    #[test]
    fn pixels_for_logical_at_150_percent() {
        // 640 * 144/96 = 960.
        assert_eq!(pixels_for_logical(640, 144), 960);
    }

    #[test]
    fn pixels_for_logical_at_125_percent() {
        // 640 * 120/96 = 800.
        assert_eq!(pixels_for_logical(640, 120), 800);
    }

    #[test]
    fn pixels_for_logical_at_175_percent() {
        // 640 * 168/96 = 1120.
        assert_eq!(pixels_for_logical(640, 168), 1120);
    }

    // —— compute_placement: PrimaryCenter ——

    #[test]
    fn primary_center_at_96_dpi_centres_in_work_area() {
        // work area 1920×1040 (40px task bar bottom), dpi 96.
        let work = rect(0, 0, 1920, 1040);
        let p = compute_placement(PlacementMode::PrimaryCenter {
            primary_work: &work,
            primary_dpi: 96,
        });
        assert_eq!(p, Placement {
            x: (1920 - 640) / 2,   // 640
            y: (1040 - 520) / 2,   // 260
            width: 640,
            height: 520,
        });
    }

    #[test]
    fn primary_center_scales_with_dpi() {
        // dpi 144 (150%): expected 960×780 inside 1920×1040.
        let work = rect(0, 0, 1920, 1040);
        let p = compute_placement(PlacementMode::PrimaryCenter {
            primary_work: &work,
            primary_dpi: 144,
        });
        assert_eq!(p, Placement {
            x: (1920 - 960) / 2,   // 480
            y: (1040 - 780) / 2,   // 130
            width: 960,
            height: 780,
        });
    }

    #[test]
    fn primary_center_uses_work_origin_not_zero() {
        // work area offset (e.g. secondary taskbar / negative origin).
        let work = rect(-1920, 0, 0, 1080);
        let p = compute_placement(PlacementMode::PrimaryCenter {
            primary_work: &work,
            primary_dpi: 96,
        });
        // width 1920, height 1080; 640×520 → x = -1920 + (1920-640)/2 = -1120.
        assert_eq!(p.x, -1920 + (1920 - 640) / 2);
        assert_eq!(p.y, (1080 - 520) / 2);
    }

    // —— compute_placement: MouseCenter ——

    #[test]
    fn mouse_center_on_secondary_monitor_at_96_dpi() {
        let m = mon(2, rect(1920, 0, 3840, 1080), rect(1920, 0, 3840, 1080), false, 96);
        let i = info(&m);
        let p = compute_placement(PlacementMode::MouseCenter { mouse: &i });
        // width 1920, height 1080; 640×520.
        assert_eq!(p, Placement {
            x: 1920 + (1920 - 640) / 2, // 2560
            y: (1080 - 520) / 2,        // 280
            width: 640,
            height: 520,
        });
    }

    #[test]
    fn mouse_center_scales_with_monitor_dpi() {
        let m = mon(2, rect(1920, 0, 3840, 1080), rect(1920, 0, 3840, 1080), false, 120);
        let i = info(&m);
        let p = compute_placement(PlacementMode::MouseCenter { mouse: &i });
        // expected 800×650.
        assert_eq!(p.width, 800);
        assert_eq!(p.height, 650);
        assert_eq!(p.x, 1920 + (1920 - 800) / 2);
        assert_eq!(p.y, (1080 - 650) / 2);
    }

    #[test]
    fn center_truncates_on_odd_slack() {
        // work width 1921 (odd), 640 → slack 1281 → /2 = 640 (floor).
        let work = rect(0, 0, 1921, 1040);
        let p = compute_placement(PlacementMode::PrimaryCenter {
            primary_work: &work,
            primary_dpi: 96,
        });
        assert_eq!(p.x, (1921 - 640) / 2);
        assert_eq!(p.x, 640);
    }

    // —— Monitors::snapshot ——

    #[test]
    fn snapshot_collects_monitors_with_full_metadata() {
        let mock = MonMock::new();
        mock.add(mon(
            1,
            rect(0, 0, 1920, 1040),
            rect(0, 0, 1920, 1080),
            true,
            96,
        ));
        mock.add(mon(
            2,
            rect(1920, 0, 4480, 1440),
            rect(1920, 0, 4480, 1440),
            false,
            120,
        ));
        let svc = Monitors::new(mock);
        let snap = svc.snapshot();
        assert_eq!(snap.len(), 2);
        assert_eq!(snap[0].handle, Hmonitor(1));
        assert_eq!(snap[0].work, rect(0, 0, 1920, 1040));
        assert!(snap[0].is_primary);
        assert_eq!(snap[0].dpi_scale, 96);
        assert_eq!(snap[1].handle, Hmonitor(2));
        assert_eq!(snap[1].dpi_scale, 120);
        assert!(!snap[1].is_primary);
    }

    #[test]
    fn snapshot_skips_monitor_whose_get_monitor_info_fails() {
        let mock = MonMock::new();
        let mut bad = mon(1, rect(0, 0, 1920, 1040), rect(0, 0, 1920, 1080), true, 96);
        bad.info_fails = true;
        mock.add(bad);
        mock.add(mon(
            2,
            rect(1920, 0, 3840, 1080),
            rect(1920, 0, 3840, 1080),
            false,
            96,
        ));
        let svc = Monitors::new(mock);
        let snap = svc.snapshot();
        assert_eq!(snap.len(), 1);
        assert_eq!(snap[0].handle, Hmonitor(2));
    }

    // —— Monitors::mouse_monitor ——

    #[test]
    fn mouse_monitor_resolves_under_cursor() {
        let mock = MonMock::new();
        mock.add(mon(
            1,
            rect(0, 0, 1920, 1040),
            rect(0, 0, 1920, 1080),
            true,
            96,
        ));
        mock.add(mon(
            2,
            rect(1920, 0, 3840, 1080),
            rect(1920, 0, 3840, 1080),
            false,
            120,
        ));
        mock.set_cursor(Some((2400, 500)));
        let svc = Monitors::new(mock);
        let m = svc.mouse_monitor().expect("cursor on secondary monitor");
        assert_eq!(m.handle, Hmonitor(2));
        assert_eq!(m.dpi_scale, 120);
    }

    #[test]
    fn mouse_monitor_none_when_cursor_unreadable() {
        let mock = MonMock::new();
        mock.add(mon(1, rect(0, 0, 1920, 1080), rect(0, 0, 1920, 1080), true, 96));
        mock.set_cursor(None);
        let svc = Monitors::new(mock);
        assert!(svc.mouse_monitor().is_none());
    }

    #[test]
    fn mouse_monitor_falls_back_to_96_dpi_on_dpi_failure() {
        let mock = MonMock::new();
        let mut m = mon(1, rect(0, 0, 1920, 1080), rect(0, 0, 1920, 1080), true, 120);
        m.dpi_fails = true;
        mock.add(m);
        mock.set_cursor(Some((100, 100)));
        let svc = Monitors::new(mock);
        let got = svc.mouse_monitor().expect("cursor on monitor");
        assert_eq!(got.dpi_scale, 96);
    }
}