//! Centralised Win32 binding layer.
//!
//! Single place that touches `windows-rs`. Other modules (enumeration, activation,
//! icon_loader, …) only depend on the [`Win32Api`] trait here + a couple of handle
//! newtypes, which keeps the rest of `core` mockable and unit-testable.
//!
//! See `docs/rust-rewrite-design-step1-3.md` §1.1 + public conventions:
//! - every "returns 0/NULL = failure" call is wrapped so callers see domain values;
//! - handles are newtyped; `Owned*` own their resource and `Drop` it;
//! - enumeration stores `HWND` as `isize` so the raw pointer never outlives
//!   the `unsafe` call that produced it.

use std::ffi::OsString;
use std::io;
use std::mem::MaybeUninit;
use std::os::windows::prelude::OsStringExt;
use std::sync::Mutex;

use thiserror::Error;
use windows::core::PCWSTR;
use windows::Win32::Foundation::{HANDLE, HWND, LPARAM, RECT, WPARAM};
use windows::Win32::Graphics::Dwm::DWMWA_CLOAKED;
use windows::Win32::Graphics::Gdi::{
    EnumDisplayMonitors, GetMonitorInfoW, MonitorFromWindow, HMONITOR, MONITORINFO,
    MONITOR_DEFAULTTONEAREST, MONITOR_DEFAULTTONULL, MONITOR_DEFAULTTOPRIMARY,
};
use windows::Win32::Security::{
    GetTokenInformation, TokenElevation, TOKEN_ELEVATION, TOKEN_QUERY,
};
use windows::Win32::System::Threading::{
    GetCurrentProcessId, OpenProcess, OpenProcessToken, QueryFullProcessImageNameW,
    PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows::Win32::UI::WindowsAndMessaging::*;

// ============================================================================
// Errors & newtypes
// ============================================================================

#[derive(Debug, Error)]
pub enum Win32Error {
    #[error("Win32 call failed: {0}")]
    Api(#[from] io::Error),
    #[error("null handle")]
    NullHandle,
    #[error("{0}")]
    Other(String),
}

/// A read-only `HWND` obtained during enumeration. Does not own the window.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct Hwnd(pub isize);

impl Hwnd {
    #[inline]
    pub fn raw(self) -> isize {
        self.0
    }
}

/// A process handle opened with `PROCESS_QUERY_LIMITED_INFORMATION`. Owned —
/// closed on drop.
pub struct OwnedProcessHandle(pub HANDLE);

impl OwnedProcessHandle {
    #[inline]
    pub fn raw(&self) -> HANDLE {
        self.0
    }
}

impl Drop for OwnedProcessHandle {
    fn drop(&mut self) {
        unsafe {
            let _ = windows::Win32::Foundation::CloseHandle(self.0);
        }
    }
}

/// An `HICON` we are responsible for destroying. Icons returned by
/// `WM_GETICON` are *borrowed* and must not be destroyed — those stay as raw
/// `isize` and are never wrapped in this type.
pub struct OwnedIcon(pub isize);

impl Drop for OwnedIcon {
    fn drop(&mut self) {
        unsafe {
            let _ = DestroyIcon(HICON(self.0 as *mut _));
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Gwlp {
    Style,
    ExStyle,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Gw {
    Owner,
    EnabledPopup,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MonitorFlag {
    DefaultToNull,
    DefaultToPrimary,
    DefaultToNearest,
}

/// `HMONITOR`, stored as `isize` for cross-thread / mock use.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct Hmonitor(pub isize);

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Rect {
    pub left: i32,
    pub top: i32,
    pub right: i32,
    pub bottom: i32,
}

impl Rect {
    #[inline]
    pub fn width(self) -> i32 {
        self.right - self.left
    }
    #[inline]
    pub fn height(self) -> i32 {
        self.bottom - self.top
    }
    #[inline]
    pub fn intersects(self, other: Rect) -> bool {
        self.left < other.right
            && self.right > other.left
            && self.top < other.bottom
            && self.bottom > other.top
    }
}

impl From<RECT> for Rect {
    fn from(r: RECT) -> Self {
        Self {
            left: r.left,
            top: r.top,
            right: r.right,
            bottom: r.bottom,
        }
    }
}

// ============================================================================
// Activation / window-control constants (Step 4–5)
// ============================================================================
// Sourced from `legacy/Core/NativeMethods.cs`. Kept here as the single binding
// point so `activation`/`window_control`/`hotkey` reference values, not magic
// numbers.

/// `SW_SHOWMINIMIZED` — `WINDOWPLACEMENT.showCmd == 2` means minimized.
pub const SW_SHOWMINIMIZED: i32 = 2;
/// `SW_SHOWMAXIMIZED` — `WINDOWPLACEMENT.showCmd == 3` means maximized; also the
/// `nCmdShow` value passed to `ShowWindow` to (re)maximize.
pub const SW_SHOWMAXIMIZED: i32 = 3;
/// `SW_SHOWNORMAL` — the "normal" `showCmd` fallback when placement read fails.
pub const SW_SHOWNORMAL: i32 = 1;
/// `SW_RESTORE` — `ShowWindow` cmd to restore a minimized window.
pub const SW_RESTORE: i32 = 9;

/// `AllowSetForegroundWindow(ASFW_ANY)` — let any process set foreground.
pub const ASFW_ANY: i32 = -1;
/// `LockSetForegroundWindow(LSFW_UNLOCK)` — unlock foreground timeout lock.
pub const LSFW_UNLOCK: u32 = 2;

/// `SetWindowPos` `hWndInsertAfter = HWND_TOPMOST`.
pub const HWND_TOPMOST: isize = -1;
/// `SetWindowPos` `hWndInsertAfter = HWND_NOTOPMOST`.
pub const HWND_NOTOPMOST: isize = -2;
/// `SWP_NOMOVE` — ignore x/y.
pub const SWP_NOMOVE: u32 = 0x0002;
/// `SWP_NOSIZE` — ignore cx/cy.
pub const SWP_NOSIZE: u32 = 0x0001;
/// `SWP_SHOWWINDOW` — show the window.
pub const SWP_SHOWWINDOW: u32 = 0x0040;

/// `VK_MENU` (Alt).
pub const VK_MENU: u32 = 0x12;
/// `VK_LMENU` (left Alt).
pub const VK_LMENU: u32 = 0xA4;
/// `VK_RMENU` (right Alt).
pub const VK_RMENU: u32 = 0xA5;
/// `VK_SHIFT`.
pub const VK_SHIFT: u32 = 0x10;
/// `VK_ALT` alias used by `keybd_event` in the legacy code (same as `VK_MENU`).
pub const VK_ALT: u8 = 0x12;
/// `KEYEVENTF_EXTENDEDKEY`.
pub const KEYEVENTF_EXTENDEDKEY: u32 = 0x0001;
/// `KEYEVENTF_KEYUP`.
pub const KEYEVENTF_KEYUP: u32 = 0x0002;

/// `WM_KEYDOWN`.
pub const WM_KEYDOWN: u32 = 0x0100;
/// `WM_KEYUP`.
pub const WM_KEYUP: u32 = 0x0101;
/// `WM_SYSKEYDOWN`.
pub const WM_SYSKEYDOWN: u32 = 0x0104;
/// `WM_SYSKEYUP`.
pub const WM_SYSKEYUP: u32 = 0x0105;
/// `WH_KEYBOARD_LL` — low-level keyboard hook id.
pub const WH_KEYBOARD_LL: i32 = 13;
/// `WM_HOTKEY`.
pub const WM_HOTKEY: u32 = 0x0312;
/// `MOD_ALT`.
pub const MOD_ALT: u32 = 0x0001;
/// `MOD_CONTROL`.
pub const MOD_CONTROL: u32 = 0x0002;
/// `MOD_NOREPEAT`.
pub const MOD_NOREPEAT: u32 = 0x4000;
/// `VK_SPACE`.
pub const VK_SPACE: u32 = 0x20;

// ============================================================================
// HotkeyState — cross-thread flags shared between the UI thread (writer) and
// the keyboard-hook thread (reader). Mirrors `HotkeyService`'s private bools.
// ============================================================================

use std::sync::atomic::{AtomicBool, Ordering};

/// Shared hotkey state. The UI thread writes; the hook thread reads. Lock-free.
///
/// `set_visible(false)` also clears `is_search_mode`, matching the C#
/// `HotkeyService.SetVisible`.
#[derive(Default)]
pub struct HotkeyState {
    pub use_alt_tab: AtomicBool,
    pub is_visible: AtomicBool,
    pub is_search_mode: AtomicBool,
    pub is_settings_open: AtomicBool,
    pub ignore_alt_release: AtomicBool,
}

impl HotkeyState {
    pub fn new() -> Self {
        Self::default()
    }
    pub fn set_use_alt_tab(&self, v: bool) {
        self.use_alt_tab.store(v, Ordering::Relaxed);
    }
    pub fn set_visible(&self, v: bool) {
        self.is_visible.store(v, Ordering::Relaxed);
        if !v {
            // Mirrors `HotkeyService.SetVisible`: leaving visibility clears
            // search mode so arrow keys don't stay "search-routed" after hide.
            self.is_search_mode.store(false, Ordering::Relaxed);
        }
    }
    pub fn set_search_mode(&self, v: bool) {
        self.is_search_mode.store(v, Ordering::Relaxed);
    }
    pub fn set_settings_open(&self, v: bool) {
        self.is_settings_open.store(v, Ordering::Relaxed);
    }
    pub fn set_ignore_alt_release(&self, v: bool) {
        self.ignore_alt_release.store(v, Ordering::Relaxed);
    }
}

// ============================================================================
// Win32Api trait
// ============================================================================

pub trait Win32Api: Send + Sync {
    // —— window basics ——
    fn is_window_visible(&self, hwnd: Hwnd) -> bool;
    fn is_iconic(&self, hwnd: Hwnd) -> bool;
    fn is_zoomed(&self, hwnd: Hwnd) -> bool;
    fn get_window_text(&self, hwnd: Hwnd) -> String;
    fn get_window_text_length(&self, hwnd: Hwnd) -> i32;
    fn get_class_name(&self, hwnd: Hwnd) -> String;
    fn get_window_long_ptr(&self, hwnd: Hwnd, idx: Gwlp) -> isize;
    fn get_window_rect(&self, hwnd: Hwnd) -> Option<Rect>;
    fn get_window(&self, hwnd: Hwnd, cmd: Gw) -> Option<Hwnd>;
    fn get_window_thread_process_id(&self, hwnd: Hwnd) -> (u32, u32);
    fn get_shell_window(&self) -> Option<Hwnd>;
    /// Enumerate windows top-to-bottom. `cb` returns `false` to stop. Panics in
    /// `cb` are caught so they never cross the FFI boundary; instead they
    /// surface here as [`Win32Error::Other`].
    fn enum_windows(&self, cb: &mut dyn FnMut(Hwnd) -> bool) -> Result<(), Win32Error>;

    // —— DWM ——
    fn is_cloaked(&self, hwnd: Hwnd) -> bool;

    // —— layered ——
    /// `(color_key, alpha, flags)` from `GetLayeredWindowAttributes`; `None` on failure.
    fn get_layered_window_attributes(&self, hwnd: Hwnd) -> Option<(u32, u8, u32)>;

    // —— placement ——
    fn get_window_placement_show_cmd(&self, hwnd: Hwnd) -> Option<i32>;
    fn get_window_placement_flags(&self, hwnd: Hwnd) -> Option<i32>;

    // —— monitors ——
    fn enum_display_monitors(&self) -> Vec<(Hmonitor, Rect)>;
    fn monitor_from_window(&self, hwnd: Hwnd, flag: MonitorFlag) -> Option<Hmonitor>;

    // —— process info ——
    fn query_full_process_image_name(&self, pid: u32) -> Option<String>;
    fn open_process_query_limited(&self, pid: u32) -> Option<OwnedProcessHandle>;
    fn process_elevation(&self, handle: &OwnedProcessHandle) -> bool;
    fn current_process_id(&self) -> u32;

    // —— post / per-window icon ——
    /// Borrowed per-window icon handle from `WM_GETICON` / `GetClassLongPtr`,
    /// with `ICON_BIG → ICON_SMALL → GCLP_HICON → GCLP_HICONSM`. Caller must
    /// not destroy it. `None` when nothing surfaced.
    fn get_window_icon_handle(&self, hwnd: Hwnd) -> Option<isize>;
    /// `PostMessage(WM_CLOSE)` to the window (used by window_control).
    fn post_close(&self, hwnd: Hwnd);

    // —— activation (Step 5) ——
    /// Current foreground window (`GetForegroundWindow`).
    fn get_foreground_window(&self) -> Option<Hwnd>;
    /// Calling thread id (`GetCurrentThreadId`).
    fn get_current_thread_id(&self) -> u32;
    /// `AttachThreadInput`. `true` on success.
    fn attach_thread_input(&self, id_attach: u32, id_attach_to: u32, attach: bool) -> bool;
    /// `AllowSetForegroundWindow(ASFW_ANY = -1)`.
    fn allow_set_foreground_window_any(&self) -> bool;
    /// `LockSetForegroundWindow(LSFW_UNLOCK = 2)`.
    fn lock_set_foreground_window_unlock(&self) -> bool;
    /// `ShowWindow(hwnd, cmd)`. `cmd` is one of the `SW_*` constants below.
    fn show_window(&self, hwnd: Hwnd, cmd: i32) -> bool;
    /// `BringWindowToTop`.
    fn bring_window_to_top(&self, hwnd: Hwnd) -> bool;
    /// `SetForegroundWindow`.
    fn set_foreground_window(&self, hwnd: Hwnd) -> bool;
    /// `SwitchToThisWindow(hwnd, fAltTab)`.
    fn switch_to_this_window(&self, hwnd: Hwnd, alt_tab: bool);
    /// `SetWindowPos(hwnd, hwnd_insert_after, x, y, cx, cy, flags)`.
    /// `hwnd_insert_after` is the raw `isize` of `HWND_TOPMOST`/`HWND_NOTOPMOST`.
    #[allow(clippy::too_many_arguments)]
    fn set_window_pos(
        &self,
        hwnd: Hwnd,
        hwnd_insert_after: isize,
        x: i32,
        y: i32,
        cx: i32,
        cy: i32,
        flags: u32,
    ) -> bool;
    /// `keybd_event(vk, 0, flags, 0)` — synthetic keystroke (used to fake an Alt
    /// tap so `SetForegroundWindow` is permitted).
    fn keybd_event(&self, vk: u8, flags: u32);
    /// `GetLastActivePopup(hwnd)` — the most recently active popup owned by
    /// `hwnd`, or `hwnd` itself when there is none.
    fn get_last_active_popup(&self, hwnd: Hwnd) -> Option<Hwnd>;

    // —— process termination (Step 5 window_control) ——
    /// Open the process with `PROCESS_TERMINATE`.
    fn open_process_terminate(&self, pid: u32) -> Option<OwnedProcessHandle>;
    /// `TerminateProcess(handle, exit_code)`.
    fn terminate_process(&self, handle: &OwnedProcessHandle, exit_code: u32) -> bool;
    /// All pids in the process tree rooted at `root_pid`, **children before
    /// root** (post-order). Uses `CreateToolhelp32Snapshot` + `Process32First/Next`.
    fn enumerate_process_tree(&self, root_pid: u32) -> Vec<u32>;

    // —— async key state (Step 4 hotkey) ——
    /// `GetAsyncKeyState(vk) & 0x8000 != 0` — is `vk` currently held.
    fn is_key_down_async(&self, vk: u32) -> bool;

    // —— UWP child probe ——
    /// `FindWindowEx(parent, None, class, None)`; returns the child HWND + its
    /// pid when the child exists and the pid differs from `exclude_pid`.
    fn find_child_window_class_pid(
        &self,
        parent: Hwnd,
        class: &str,
        exclude_pid: u32,
    ) -> Option<(Hwnd, u32)>;

    // —— icon extraction (Step 3) ——
    /// Resolve the executable path for a process id (cached externally).
    /// Same as [`query_full_process_image_name`]; exposed here for symmetry
    /// with `IconCache::get_process_path`.
    fn process_path_for(&self, pid: u32) -> Option<String> {
        self.query_full_process_image_name(pid)
    }

    /// Shell-extract the icon for an executable on disk. Returns an owned
    /// `IconImage`. The underlying HICON is destroyed here (exe-wide: safe
    /// across windows of the same exe).
    fn shell_extract_icon(&self, exe_path: &str) -> Option<IconImage>;

    /// Last-resort extraction from the process module. Also exe-wide: safe to
    /// cache.
    fn extract_associated_icon(&self, exe_path: &str) -> Option<IconImage>;

    /// Convert a *borrowed* per-window HICON (from `WM_GETICON` /
    /// `GetClassLongPtr`) into an `IconImage`. The input HICON is **not**
    /// destroyed (`WM_GETICON` returns borrowed handles).
    fn window_icon_to_image(&self, hicon: isize) -> Option<IconImage>;
}

/// Decoded icon image, opaque to callers — in production it's RGBA8 pixels
/// decoded off the HICON; in tests it's a stand-in integer keyed by the mock.
/// The point of the type is that **Equality is identity** — two windows of the
/// same exe may surface different `IconImage` values, which is what the
/// per-window invariant guards.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct IconImage {
    pub width: u32,
    pub height: u32,
    /// BGRA-premultiplied (or RGBA8 — step 3 just needs a buffer). Bytes are
    /// deliberately opaque so tests can compare with `==`. Production code
    /// fills this from `GetDIBits`; mock code fills an opaque seal.
    pub pixels: Vec<u8>,
}

impl IconImage {
    pub fn new(width: u32, height: u32, pixels: Vec<u8>) -> Self {
        Self {
            width,
            height,
            pixels,
        }
    }
}

// ============================================================================
// Concrete implementation
// ============================================================================

#[derive(Default, Clone, Copy)]
pub struct WindowsApi;

#[allow(dead_code)]
fn last_io_err() -> io::Error {
    io::Error::last_os_error()
}

fn raw_hwnd(hwnd: Hwnd) -> HWND {
    HWND(hwnd.0 as *mut _)
}

fn into_hwnd(h: HWND) -> Option<Hwnd> {
    (h.0 as isize != 0).then_some(Hwnd(h.0 as isize))
}

impl Win32Api for WindowsApi {
    fn is_window_visible(&self, hwnd: Hwnd) -> bool {
        unsafe { IsWindowVisible(raw_hwnd(hwnd)).as_bool() }
    }
    fn is_iconic(&self, hwnd: Hwnd) -> bool {
        unsafe { IsIconic(raw_hwnd(hwnd)).as_bool() }
    }
    fn is_zoomed(&self, hwnd: Hwnd) -> bool {
        unsafe { IsZoomed(raw_hwnd(hwnd)).as_bool() }
    }
    fn get_window_text(&self, hwnd: Hwnd) -> String {
        unsafe {
            let h = raw_hwnd(hwnd);
            let len = GetWindowTextLengthW(h);
            if len <= 0 {
                return String::new();
            }
            let cap = (len as usize) + 1;
            let mut buf = vec![0u16; cap];
            let got = GetWindowTextW(h, &mut buf);
            let take = got.max(0) as usize;
            // truncate if the null is included, then build an OsString
            let end = buf[..take].iter().position(|&c| c == 0).unwrap_or(take);
            OsString::from_wide(&buf[..end]).to_string_lossy().into_owned()
        }
    }
    fn get_window_text_length(&self, hwnd: Hwnd) -> i32 {
        unsafe { GetWindowTextLengthW(raw_hwnd(hwnd)) }
    }
    fn get_class_name(&self, hwnd: Hwnd) -> String {
        unsafe {
            let mut buf = vec![0u16; 256];
            let got = GetClassNameW(raw_hwnd(hwnd), &mut buf);
            let take = got.max(0) as usize;
            let end = buf[..take].iter().position(|&c| c == 0).unwrap_or(take);
            OsString::from_wide(&buf[..end]).to_string_lossy().into_owned()
        }
    }
    fn get_window_long_ptr(&self, hwnd: Hwnd, idx: Gwlp) -> isize {
        let n = match idx {
            Gwlp::Style => GWL_STYLE,
            Gwlp::ExStyle => GWL_EXSTYLE,
        };
        unsafe { GetWindowLongPtrW(raw_hwnd(hwnd), n) }
    }
    fn get_window_rect(&self, hwnd: Hwnd) -> Option<Rect> {
        unsafe {
            let mut r = MaybeUninit::<RECT>::uninit();
            GetWindowRect(raw_hwnd(hwnd), r.as_mut_ptr()).ok()?;
            Some(Rect::from(r.assume_init()))
        }
    }
    fn get_window(&self, hwnd: Hwnd, cmd: Gw) -> Option<Hwnd> {
        let u = match cmd {
            Gw::Owner => GW_OWNER,
            Gw::EnabledPopup => GW_ENABLEDPOPUP,
        };
        unsafe {
            let r = GetWindow(raw_hwnd(hwnd), u).ok()?;
            into_hwnd(r)
        }
    }
    fn get_window_thread_process_id(&self, hwnd: Hwnd) -> (u32, u32) {
        unsafe {
            let mut pid: u32 = 0;
            let tid = GetWindowThreadProcessId(raw_hwnd(hwnd), Some(&mut pid));
            (tid, pid)
        }
    }
    fn get_shell_window(&self) -> Option<Hwnd> {
        unsafe { into_hwnd(GetShellWindow()) }
    }
    fn enum_windows(&self, cb: &mut dyn FnMut(Hwnd) -> bool) -> Result<(), Win32Error> {
        // Box the trait-object reference and pass the box's thin pointer through
        // LPARAM. Panics in the callback are caught and recorded in a single-
        // cell buffer (enum calls are serialised by the `is_refreshing` gate).
        let boxed: Box<&mut dyn FnMut(Hwnd) -> bool> = Box::new(cb);
        let state = Box::into_raw(boxed) as *mut ();
        // Reset the panic slot before the call.
        drop(PANIC_MSG.lock().unwrap().take());

        unsafe extern "system" fn raw(hwnd: HWND, lparam: LPARAM) -> windows::core::BOOL {
            let state = lparam.0 as *mut &mut dyn FnMut(Hwnd) -> bool;
            if state.is_null() {
                return windows::core::BOOL(1);
            }
            let cb = &mut *state;
            let res = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                if hwnd.0 as isize == 0 {
                    true
                } else {
                    cb(Hwnd(hwnd.0 as isize))
                }
            }));
            match res {
                Ok(true) => windows::core::BOOL(1),
                Ok(false) => windows::core::BOOL(0),
                Err(e) => {
                    let m = e
                        .downcast_ref::<&'static str>()
                        .map(|s| s.to_string())
                        .or_else(|| e.downcast_ref::<String>().cloned())
                        .unwrap_or_else(|| "panic in enum_windows callback".to_string());
                    *PANIC_MSG.lock().unwrap() = Some(m);
                    windows::core::BOOL(0)
                }
            }
        }

        let res = unsafe { EnumWindows(Some(raw), LPARAM(state as isize)) };
        // Reclaim the box so it doesn't leak.
        unsafe {
            drop(Box::from_raw(state as *mut &mut dyn FnMut(Hwnd) -> bool));
        }
        // EnumWindows returns Err only when the callback returned FALSE —
        // which is also our early-stop signal. Treat that as a success and let
        // the panic slot decide whether to surface an error.
        let _ = res;
        // Inspect the panic slot.
        let p = PANIC_MSG.lock().unwrap().take();
        match p {
            Some(m) => Err(Win32Error::Other(m)),
            None => Ok(()),
        }
    }

    fn is_cloaked(&self, hwnd: Hwnd) -> bool {
        unsafe {
            let mut val: i32 = 0;
            let r = windows::Win32::Graphics::Dwm::DwmGetWindowAttribute(
                raw_hwnd(hwnd),
                DWMWA_CLOAKED,
                &mut val as *mut i32 as *mut _,
                std::mem::size_of::<i32>() as u32,
            );
            r.is_ok() && val != 0
        }
    }

    fn get_layered_window_attributes(&self, hwnd: Hwnd) -> Option<(u32, u8, u32)> {
        unsafe {
            use windows::Win32::Foundation::COLORREF;
            use windows::Win32::UI::WindowsAndMessaging::LAYERED_WINDOW_ATTRIBUTES_FLAGS;
            let mut key = COLORREF(0);
            let mut alpha: u8 = 0;
            let mut flags = LAYERED_WINDOW_ATTRIBUTES_FLAGS(0);
            let ok = GetLayeredWindowAttributes(
                raw_hwnd(hwnd),
                Some(&mut key),
                Some(&mut alpha),
                Some(&mut flags),
            )
            .is_ok();
            ok.then_some((key.0, alpha, flags.0))
        }
    }

    fn get_window_placement_show_cmd(&self, hwnd: Hwnd) -> Option<i32> {
        unsafe {
            let mut p = WINDOWPLACEMENT {
                length: std::mem::size_of::<WINDOWPLACEMENT>() as u32,
                ..Default::default()
            };
            GetWindowPlacement(raw_hwnd(hwnd), &mut p).ok()?;
            Some(p.showCmd as i32)
        }
    }
    fn get_window_placement_flags(&self, hwnd: Hwnd) -> Option<i32> {
        unsafe {
            let mut p = WINDOWPLACEMENT {
                length: std::mem::size_of::<WINDOWPLACEMENT>() as u32,
                ..Default::default()
            };
            GetWindowPlacement(raw_hwnd(hwnd), &mut p).ok()?;
            Some(p.flags.0 as i32)
        }
    }

    fn enum_display_monitors(&self) -> Vec<(Hmonitor, Rect)> {
        unsafe extern "system" fn raw(
            hmon: HMONITOR,
            _hdc: windows::Win32::Graphics::Gdi::HDC,
            rect: *mut RECT,
            data: LPARAM,
        ) -> windows::core::BOOL {
            let out = data.0 as *mut Vec<(Hmonitor, Rect)>;
            if out.is_null() {
                return windows::core::BOOL(1);
            }
            let raw_rect = if rect.is_null() {
                Rect {
                    left: 0,
                    top: 0,
                    right: 0,
                    bottom: 0,
                }
            } else {
                Rect::from(*rect)
            };
            let mut mi = MONITORINFO {
                cbSize: std::mem::size_of::<MONITORINFO>() as u32,
                ..Default::default()
            };
            let r = if GetMonitorInfoW(hmon, &mut mi).as_bool() {
                Rect::from(mi.rcMonitor)
            } else {
                raw_rect
            };
            (*out).push((Hmonitor(hmon.0 as isize), r));
            windows::core::BOOL(1)
        }
        let mut out: Vec<(Hmonitor, Rect)> = Vec::new();
        unsafe {
            // BOOL-returning variant — ignore the result, we always enumerate.
            let _ = EnumDisplayMonitors(None, None, Some(raw), LPARAM(&mut out as *mut _ as isize));
        }
        out
    }

    fn monitor_from_window(&self, hwnd: Hwnd, flag: MonitorFlag) -> Option<Hmonitor> {
        let f = match flag {
            MonitorFlag::DefaultToNull => MONITOR_DEFAULTTONULL,
            MonitorFlag::DefaultToPrimary => MONITOR_DEFAULTTOPRIMARY,
            MonitorFlag::DefaultToNearest => MONITOR_DEFAULTTONEAREST,
        };
        unsafe {
            let h = MonitorFromWindow(raw_hwnd(hwnd), f);
            (h.0 as isize != 0).then_some(Hmonitor(h.0 as isize))
        }
    }

    fn query_full_process_image_name(&self, pid: u32) -> Option<String> {
        let handle = self.open_process_query_limited(pid)?;
        unsafe {
            let mut buf = vec![0u16; 260];
            let mut size = buf.len() as u32;
            QueryFullProcessImageNameW(handle.raw(), windows::Win32::System::Threading::PROCESS_NAME_FORMAT(0), windows::core::PWSTR(buf.as_mut_ptr()), &mut size).ok()?;
            let n = size as usize;
            Some(OsString::from_wide(&buf[..n]).to_string_lossy().into_owned())
        }
    }

    fn open_process_query_limited(&self, pid: u32) -> Option<OwnedProcessHandle> {
        unsafe {
            let h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
            Some(OwnedProcessHandle(h))
        }
    }

    fn process_elevation(&self, handle: &OwnedProcessHandle) -> bool {
        unsafe {
            let mut token = HANDLE::default();
            if OpenProcessToken(handle.raw(), TOKEN_QUERY, &mut token).is_err() {
                return false;
            }
            struct Tok(HANDLE);
            impl Drop for Tok {
                fn drop(&mut self) {
                    unsafe {
                        let _ = windows::Win32::Foundation::CloseHandle(self.0);
                    }
                }
            }
            let _g = Tok(token);
            let mut elev = TOKEN_ELEVATION::default();
            let mut ret: u32 = 0;
            let ok = GetTokenInformation(
                token,
                TokenElevation,
                Some(&mut elev as *mut _ as *mut _),
                std::mem::size_of::<TOKEN_ELEVATION>() as u32,
                &mut ret,
            )
            .is_ok();
            ok && elev.TokenIsElevated != 0
        }
    }

    fn current_process_id(&self) -> u32 {
        unsafe { GetCurrentProcessId() }
    }

    fn get_window_icon_handle(&self, hwnd: Hwnd) -> Option<isize> {
        unsafe {
            let h = raw_hwnd(hwnd);
            let try_msg = |icon_kind: usize| -> Option<isize> {
                let mut res: usize = 0;
                let _ = SendMessageTimeoutW(
                    h,
                    WM_GETICON,
                    WPARAM(icon_kind),
                    LPARAM(0),
                    SMTO_ABORTIFHUNG,
                    50,
                    Some(&mut res),
                );
                (res as isize != 0).then_some(res as isize)
            };
            try_msg(ICON_BIG as usize)
                .or_else(|| try_msg(ICON_SMALL as usize))
                .or_else(|| {
                    let v = GetClassLongPtrW(h, GCLP_HICON) as isize;
                    (v != 0).then_some(v)
                })
                .or_else(|| {
                    let v = GetClassLongPtrW(h, GCLP_HICONSM) as isize;
                    (v != 0).then_some(v)
                })
        }
    }

    fn post_close(&self, hwnd: Hwnd) {
        unsafe {
            let _ = PostMessageW(Some(raw_hwnd(hwnd)), WM_CLOSE, WPARAM(0), LPARAM(0));
        }
    }

    fn get_foreground_window(&self) -> Option<Hwnd> {
        unsafe { into_hwnd(GetForegroundWindow()) }
    }
    fn get_current_thread_id(&self) -> u32 {
        unsafe { windows::Win32::System::Threading::GetCurrentThreadId() }
    }
    fn attach_thread_input(&self, id_attach: u32, id_attach_to: u32, attach: bool) -> bool {
        unsafe {
            windows::Win32::System::Threading::AttachThreadInput(id_attach, id_attach_to, attach)
                .as_bool()
        }
    }
    fn allow_set_foreground_window_any(&self) -> bool {
        // `ASFW_ANY = -1` is passed as `u32::MAX` (the API takes a process id, and
        // `ASFW_ANY` is documented as `(DWORD)-1`).
        unsafe {
            windows::Win32::UI::WindowsAndMessaging::AllowSetForegroundWindow(ASFW_ANY as u32)
                .is_ok()
        }
    }
    fn lock_set_foreground_window_unlock(&self) -> bool {
        unsafe {
            windows::Win32::UI::WindowsAndMessaging::LockSetForegroundWindow(
                windows::Win32::UI::WindowsAndMessaging::FOREGROUND_WINDOW_LOCK_CODE(LSFW_UNLOCK),
            )
            .is_ok()
        }
    }
    fn show_window(&self, hwnd: Hwnd, cmd: i32) -> bool {
        unsafe {
            windows::Win32::UI::WindowsAndMessaging::ShowWindow(
                raw_hwnd(hwnd),
                windows::Win32::UI::WindowsAndMessaging::SHOW_WINDOW_CMD(cmd),
            )
            .as_bool()
        }
    }
    fn bring_window_to_top(&self, hwnd: Hwnd) -> bool {
        unsafe {
            windows::Win32::UI::WindowsAndMessaging::BringWindowToTop(raw_hwnd(hwnd)).is_ok()
        }
    }
    fn set_foreground_window(&self, hwnd: Hwnd) -> bool {
        unsafe { SetForegroundWindow(raw_hwnd(hwnd)).as_bool() }
    }
    fn switch_to_this_window(&self, hwnd: Hwnd, alt_tab: bool) {
        unsafe {
            windows::Win32::UI::WindowsAndMessaging::SwitchToThisWindow(raw_hwnd(hwnd), alt_tab);
        }
    }
    // 1:1 with `SetWindowPos`; the arg count is the Win32 signature's.
    #[allow(clippy::too_many_arguments)]
    fn set_window_pos(
        &self,
        hwnd: Hwnd,
        hwnd_insert_after: isize,
        x: i32,
        y: i32,
        cx: i32,
        cy: i32,
        flags: u32,
    ) -> bool {
        unsafe {
            let after = HWND(hwnd_insert_after as *mut _);
            windows::Win32::UI::WindowsAndMessaging::SetWindowPos(
                raw_hwnd(hwnd),
                Some(after),
                x,
                y,
                cx,
                cy,
                windows::Win32::UI::WindowsAndMessaging::SET_WINDOW_POS_FLAGS(flags),
            )
            .is_ok()
        }
    }
    fn keybd_event(&self, vk: u8, flags: u32) {
        unsafe {
            windows::Win32::UI::Input::KeyboardAndMouse::keybd_event(
                vk,
                0,
                windows::Win32::UI::Input::KeyboardAndMouse::KEYBD_EVENT_FLAGS(flags),
                0,
            );
        }
    }
    fn get_last_active_popup(&self, hwnd: Hwnd) -> Option<Hwnd> {
        unsafe {
            let r = windows::Win32::UI::WindowsAndMessaging::GetLastActivePopup(raw_hwnd(hwnd));
            into_hwnd(r)
        }
    }

    fn open_process_terminate(&self, pid: u32) -> Option<OwnedProcessHandle> {
        use windows::Win32::System::Threading::{OpenProcess, PROCESS_TERMINATE};
        unsafe {
            let h = OpenProcess(PROCESS_TERMINATE, false, pid).ok()?;
            Some(OwnedProcessHandle(h))
        }
    }
    fn terminate_process(&self, handle: &OwnedProcessHandle, exit_code: u32) -> bool {
        unsafe {
            windows::Win32::System::Threading::TerminateProcess(handle.raw(), exit_code).is_ok()
        }
    }
    fn enumerate_process_tree(&self, root_pid: u32) -> Vec<u32> {
        // Equivalent of .NET's `Process.Kill(entireProcessTree: true)`: snapshot
        // every process, build parent→children adjacency, then post-order DFS
        // from `root_pid` so children are killed before their parent.
        use std::collections::{HashMap, HashSet};
        use windows::Win32::System::Diagnostics::ToolHelp::{
            CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W,
            TH32CS_SNAPPROCESS,
        };

        let mut parent_to_children: HashMap<u32, Vec<u32>> = HashMap::new();
        unsafe {
            let snap = match CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) {
                Ok(s) => s,
                Err(_) => return Vec::new(),
            };
            let mut entry = PROCESSENTRY32W {
                dwSize: std::mem::size_of::<PROCESSENTRY32W>() as u32,
                ..Default::default()
            };
            if Process32FirstW(snap, &mut entry).is_err() {
                let _ = windows::Win32::Foundation::CloseHandle(snap);
                return Vec::new();
            }
            loop {
                let pid = entry.th32ProcessID;
                let parent = entry.th32ParentProcessID;
                parent_to_children.entry(parent).or_default().push(pid);
                if Process32NextW(snap, &mut entry).is_err() {
                    break;
                }
            }
            let _ = windows::Win32::Foundation::CloseHandle(snap);
        }

        // Post-order: children first, then self. Guard against cycles / repeated
        // parents with a `seen` set.
        fn walk(
            pid: u32,
            adj: &HashMap<u32, Vec<u32>>,
            seen: &mut HashSet<u32>,
            out: &mut Vec<u32>,
        ) {
            if !seen.insert(pid) {
                return;
            }
            if let Some(children) = adj.get(&pid) {
                for &c in children {
                    walk(c, adj, seen, out);
                }
            }
            out.push(pid);
        }
        let mut out = Vec::new();
        let mut seen = HashSet::new();
        walk(root_pid, &parent_to_children, &mut seen, &mut out);
        out
    }

    fn is_key_down_async(&self, vk: u32) -> bool {
        unsafe {
            (windows::Win32::UI::Input::KeyboardAndMouse::GetAsyncKeyState(vk as i32) as u16
                & 0x8000) != 0
        }
    }

    fn find_child_window_class_pid(
        &self,
        parent: Hwnd,
        class: &str,
        exclude_pid: u32,
    ) -> Option<(Hwnd, u32)> {
        let mut wide: Vec<u16> = class.encode_utf16().collect();
        wide.push(0);
        unsafe {
            let child = FindWindowExW(
                Some(raw_hwnd(parent)),
                None,
                PCWSTR(wide.as_ptr()),
                PCWSTR::null(),
            )
            .ok()?;
            let child = into_hwnd(child)?;
            let (_, pid) = self.get_window_thread_process_id(child);
            (pid != 0 && pid != exclude_pid).then_some((child, pid))
        }
    }

    fn shell_extract_icon(&self, exe_path: &str) -> Option<IconImage> {
        use windows::Win32::UI::Shell::{
            ExtractIconW, SHGetFileInfoW, SHFILEINFOW, SHGFI_ICON, SHGFI_LARGEICON,
        };
        let wide: Vec<u16> = exe_path.encode_utf16().chain(std::iter::once(0)).collect();
        unsafe {
            let mut shinfo = SHFILEINFOW::default();
            let r = SHGetFileInfoW(
                PCWSTR(wide.as_ptr()),
                windows::Win32::Storage::FileSystem::FILE_FLAGS_AND_ATTRIBUTES(0),
                Some(&mut shinfo),
                std::mem::size_of::<SHFILEINFOW>() as u32,
                SHGFI_ICON | SHGFI_LARGEICON,
            );
            if r == 0 || shinfo.hIcon.0.is_null() {
                // Fallback: ExtractIconW.
                let h = ExtractIconW(None, PCWSTR(wide.as_ptr()), 0);
                if h.0.is_null() {
                    return None;
                }
                let img = hicon_to_image(h);
                let _ = DestroyIcon(h);
                return img;
            }
            let h = HICON(shinfo.hIcon.0);
            let img = hicon_to_image(h);
            let _ = DestroyIcon(h);
            img
        }
    }

    fn extract_associated_icon(&self, exe_path: &str) -> Option<IconImage> {
        use windows::Win32::UI::Shell::ExtractIconW;
        let wide: Vec<u16> = exe_path.encode_utf16().chain(std::iter::once(0)).collect();
        unsafe {
            let h = ExtractIconW(None, PCWSTR(wide.as_ptr()), 0);
            if h.0.is_null() {
                return None;
            }
            let img = hicon_to_image(h);
            let _ = DestroyIcon(h);
            img
        }
    }

    fn window_icon_to_image(&self, hicon: isize) -> Option<IconImage> {
        if hicon == 0 {
            return None;
        }
        unsafe { hicon_to_image(HICON(hicon as *mut _)) }
        // **Not** destroying the HICON — WM_GETICON returns borrowed handles.
    }
}

/// Decode an HICON into an [`IconImage`] (RGBA8). Mirrors the C#
/// `IconHandleToImageSource` pipeline (`GetIconInfo` → `GetDIBits` against a
/// 32-bpp DIB section) but produces raw bytes instead of a WPF `ImageSource`.
/// `Step 3+` will hand these bytes to Slint as an `SharedPixelBuffer<Rgba8>`.
unsafe fn hicon_to_image(hicon: HICON) -> Option<IconImage> {
    use windows::Win32::Graphics::Gdi::{
        CreateCompatibleDC, DeleteDC, DeleteObject, GetDIBits, GetObjectW, BITMAP, BITMAPINFO,
        BITMAPINFOHEADER, DIB_RGB_COLORS, HGDIOBJ,
    };
    use windows::Win32::UI::WindowsAndMessaging::{GetIconInfo, ICONINFO};

    let mut ii = ICONINFO::default();
    if GetIconInfo(hicon, &mut ii).is_err() {
        return None;
    }
    // We only need the color bitmap for the icon's pixels.
    let color = ii.hbmColor;
    if color.0.is_null() {
        // Mask-only (1-bpp) icons are uncommon; skip rather than synthesise.
        let _ = DeleteObject(HGDIOBJ(ii.hbmMask.0));
        return None;
    }

    let mut hdr = BITMAPINFOHEADER {
        biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
        biWidth: 0,
        biHeight: 0,
        biPlanes: 1,
        biBitCount: 32,
        biCompression: 0, // BI_RGB
        biSizeImage: 0,
        biXPelsPerMeter: 0,
        biYPelsPerMeter: 0,
        biClrUsed: 0,
        biClrImportant: 0,
    };

    // Read the bitmap header to learn dimensions.
    let mut bm = BITMAP::default();
    if GetObjectW(
        HGDIOBJ(color.0),
        std::mem::size_of::<BITMAP>() as i32,
        Some(&mut bm as *mut _ as *mut _),
    ) == 0
    {
        let _ = DeleteObject(HGDIOBJ(ii.hbmMask.0));
        let _ = DeleteObject(HGDIOBJ(color.0));
        return None;
    }
    let width = bm.bmWidth as u32;
    let height = bm.bmHeight as u32;

    hdr.biWidth = width as i32;
    hdr.biHeight = -(height as i32); // top-down DIB

    let mut info = BITMAPINFO {
        bmiHeader: hdr,
        bmiColors: [Default::default(); 1],
    };

    // Create a compatible DC; we don't need `GetDC` on a window — a memory DC
    // borrowed from the screen DC is enough for `GetDIBits` on a DIB section.
    let hdc = CreateCompatibleDC(None);
    if hdc.is_invalid() {
        let _ = DeleteObject(HGDIOBJ(ii.hbmMask.0));
        let _ = DeleteObject(HGDIOBJ(color.0));
        return None;
    }
    let mut pixels = vec![0u8; (width * height * 4) as usize];
    let got = GetDIBits(
        hdc,
        color,
        0,
        height,
        Some(pixels.as_mut_ptr() as *mut _),
        &mut info,
        DIB_RGB_COLORS,
    );
    let _ = DeleteDC(hdc);
    let _ = DeleteObject(HGDIOBJ(ii.hbmMask.0));
    let _ = DeleteObject(HGDIOBJ(color.0));

    if got == 0 {
        return None;
    }

    // Convert BGRA → RGBA8 (Slint later wants Rgba8; we keep RGBA-premul here).
    for chunk in pixels.chunks_exact_mut(4) {
        chunk.swap(0, 2);
    }
    Some(IconImage { width, height, pixels })
}

// Panic-slot for the EnumWindows trampoline. Single serialised caller.
static PANIC_MSG: Mutex<Option<String>> = Mutex::new(None);

// `WPARAM`/`LPARAM` are re-exported but `windows::core::BOOL` is the FFI bool;
// keep the imports honest in case the lint runs.
#[allow(dead_code)]
fn _link_anchors() {
    let _ = std::mem::size_of::<LPARAM>();
    let _ = std::mem::size_of::<WPARAM>();
}