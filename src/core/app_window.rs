//! Data model for one enumerated window.
//!
//! Pure value type — no interior mutability, no Win32 calls. The "on-demand"
//! pieces from the C# version (Icon, Elevated, MonitorNumber) live in the
//! [`crate::enumeration`] caches keyed by `Hwnd`, because `AppWindow` crosses
//! thread boundaries (enum thread → UI thread) and Rust forbids interior
//! mutation across `Send` copies. See `docs/rust-rewrite-design-step1-3.md` §2.1.

use crate::core::win32::Hwnd;

#[derive(Debug, Clone)]
pub struct AppWindow {
    pub handle: Hwnd,
    pub title: String,
    pub class_name: String,
    pub process_id: u32,
    pub process_name: String,
    pub is_minimized: bool,
    pub is_maximized: bool,
    /// Whether the window carries `WS_EX_TOPMOST`. Topmost windows are dropped
    /// to the tail of the switcher list — see [`crate::enumeration`].
    pub is_topmost: bool,
    /// Visible ancestor owner recorded on the Delphi-VCL compatibility path.
    /// `None` when the window passed cheap filters without needing that path
    /// (no owner, or an owner but is APPWINDOW).
    pub owner_kept: Option<Hwnd>,
}

impl AppWindow {
    /// `Title` if it is non-empty (after trimming), else `ProcessName`. Mirrors
    /// the C# `FormattedTitle` fallback that lets title-less dialog-frame
    /// windows survive by showing the owning process name.
    pub fn formatted_title(&self) -> &str {
        if self.title.trim().is_empty() {
            &self.process_name
        } else {
            &self.title
        }
    }

    /// Substring match used by the search box. Empty / whitespace filter
    /// matches everything. Case-insensitive substring of title or process name.
    /// Pinyin matching is plumbed through separately by `enumeration` /
    /// `app_bridge` (Step 7+) — this method is the bare port of `MatchesFilter`
    /// minus the pinyin leg, which needs the resolved pinyin tables.
    pub fn matches_filter(&self, filter: &str) -> bool {
        if filter.trim().is_empty() {
            return true;
        }
        let needle = filter.to_lowercase();
        if self.title.to_lowercase().contains(&needle) {
            return true;
        }
        if self.process_name.to_lowercase().contains(&needle) {
            return true;
        }
        false
    }
}