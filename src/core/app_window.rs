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
    /// This is the bare port of `MatchesFilter` *without* the pinyin leg —
    /// callers that want pinyin use [`matches_filter_pinyin`].
    ///
    /// [`matches_filter_pinyin`]: AppWindow::matches_filter_pinyin
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

    /// Matches the filter against the window's plain substrings **and**, when
    /// `pinyin_on` is set, the pinyin initials / full pinyin of both the title
    /// and the process name. 1:1 with the legacy `AppWindow.MatchesFilter`
    /// pinyin leg (Title+Initials / Title+Full / ProcessName+Initials /
    /// ProcessName+Full). The plain lower-case substring leg runs first so the
    /// pinyin path is only consulted when it would have missed.
    pub fn matches_filter_pinyin(
        &self,
        filter: &str,
        pinyin: &mut crate::core::pinyin::PinyinService,
        pinyin_on: bool,
    ) -> bool {
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
        if pinyin_on && (pinyin.matches_pinyin(&self.title, &needle)
            || pinyin.matches_pinyin(&self.process_name, &needle))
        {
            return true;
        }
        false
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::core::pinyin::PinyinService;
    use crate::core::win32::Hwnd;

    fn win(title: &str, proc: &str) -> AppWindow {
        AppWindow {
            handle: Hwnd(0),
            title: title.to_string(),
            class_name: String::new(),
            process_id: 0,
            process_name: proc.to_string(),
            is_minimized: false,
            is_maximized: false,
            is_topmost: false,
            owner_kept: None,
        }
    }

    #[test]
    fn matches_filter_plain_substring() {
        // Title has no "zzz"; process name has no "zzz" → plain miss.
        let w = win("记事本", "notepad");
        assert!(!w.matches_filter("zzz"));
        // Process name substring hits.
        let w2 = win("记事本", "notepad");
        assert!(w2.matches_filter("note"));
        // Title substring hits (case-insensitive).
        let w3 = win("Hello", "explorer");
        assert!(w3.matches_filter("HEL"));
        // Whitespace filter matches everything.
        let w4 = win("Anything", "any");
        assert!(w4.matches_filter("   "));
    }

    #[test]
    fn matches_filter_pinyin_off_equals_plain() {
        let w = win("记事本", "notepad");
        let mut p = PinyinService::new();
        assert!(!w.matches_filter_pinyin("jsb", &mut p, false));
        assert!(w.matches_filter_pinyin("note", &mut p, false));
    }

    #[test]
    fn matches_filter_pinyin_on_title_initials() {
        let w = win("记事本", "explorer");
        let mut p = PinyinService::new();
        assert!(w.matches_filter_pinyin("jsb", &mut p, true));
    }

    #[test]
    fn matches_filter_pinyin_on_title_full_prefix() {
        let w = win("记事本", "explorer");
        let mut p = PinyinService::new();
        assert!(w.matches_filter_pinyin("jishi", &mut p, true));
    }

    #[test]
    fn matches_filter_pinyin_on_process_name_leg() {
        // Title is plain English (would match plain leg anyway); construct a
        // title that only matches via ProcessName's pinyin.
        let w = win("Untitled", "记事本进程");
        let mut p = PinyinService::new();
        assert!(w.matches_filter_pinyin("jsb", &mut p, true));
    }

    #[test]
    fn matches_filter_pinyin_whitespace_filter_always_true() {
        let w = win("记事本", "notepad");
        let mut p = PinyinService::new();
        assert!(w.matches_filter_pinyin("   ", &mut p, true));
    }

    #[test]
    fn matches_filter_pinyin_case_insensitive_filter() {
        // "Weixin" matches the plain ("ProcessName".lower()) leg before pinyin
        // gets a chance — still true, exercises the no-pinyin-hit path.
        let w = win("Untitled", "Weixin");
        let mut p = PinyinService::new();
        assert!(w.matches_filter_pinyin("WEIXIN", &mut p, true));
    }
}