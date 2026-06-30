//! Switcher state machine — pure logic layer bridging Slint ↔ Core.
//!
//! 1:1 port of the *non-visual* logic in `legacy/ViewModels/MainViewModel.cs`.
//! Everything here is plain `&mut self` logic over a [`SwitcherHost`] trait; no
//! Win32, no Slint, fully unit-testable. The thin `AppBridge` translation to
//! Slint properties (show/hide, writing `set_windows`, debounce timer) lives in
//! a later sub-step and is integration-smoke-tested, not unit-tested.
//!
//! Key invariants preserved verbatim from the C# ViewModel:
//! - close returning `OnlyDialogDismissed` **keeps** the row (root window still
//!   on screen — the legacy `rootClosed` guard);
//! - Alt+Tab first-open selects index 1 when the list has ≥2 rows (the first
//!   row is the current foreground window);
//! - selection is repaired to a live neighbouring row after close / kill;
//! - grouping remembers the pre-group selection's process and restores it.
//!
//! See `docs/rust-rewrite-design-step7.md` §B.

use crate::core::activation::ActivationOutcome;
use crate::core::pinyin::PinyinService;
use crate::core::window_control::CloseResult;

/// One row of the switcher list handed to Slint. A pure value: no `AppWindow`
/// reference (the enum thread builds these from `AppWindow`s; tests build them
/// directly). `icon_token` is an opaque handle into `IconCache` — `0` means
/// "not yet resolved" so Step 7b/c can show a placeholder.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WindowRow {
    /// Stable identity for "same window across refreshes" — the underlying HWND
    /// as `isize`. Used so the bridge can diff old vs new lists by identity, not
    /// by value equality (titles can change).
    pub id: isize,
    pub title: String,
    pub process_name: String,
    /// 1-based monitor number from `enumeration::monitor_number`; 0 when unknown.
    pub monitor: u32,
    pub process_id: u32,
    /// Opaque icon cache token; `0` = placeholder.
    pub icon_token: u64,
    pub is_elevated: bool,
}

/// Which empty-state caption to show. Mirrors the two legacy strings
/// `NoWindowsFound` ("no matches for search") vs `NoVisibleWindows` ("no
/// switchable windows at all").
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EmptyState {
    /// `windows` is non-empty but `filtered` is empty — search matched nothing.
    NoMatches,
    /// `windows` itself is empty — there's nothing to switch to.
    NoWindowsAtAll,
}

/// Effects the state machine may request of the host. Kept tiny and trait-
/// based so tests inject a `MockHost` recording every call. The production host
/// threads these through `WindowService` / `activation` / `window_control`.
pub trait SwitcherHost {
    /// Enumerate the current window list. Slow (EnumWindows + DWM) so the
    /// bridge runs this off the UI thread; `SwitcherState` itself is agnostic.
    fn enumerate(&mut self) -> Vec<WindowRow>;
    /// Activate the window behind `row.id`. Returns the `ActivationOutcome`
    /// (the bridge reads it to clear `ignore_alt_release` etc.).
    fn activate(&mut self, row: &WindowRow) -> ActivationOutcome;
    /// Close the window — `RootClosed` ( WM_CLOSE hit root) or
    /// `OnlyDialogDismissed` (the close was redirected to an owned dialog and
    /// the root is still open).
    fn close(&mut self, row: &WindowRow) -> CloseResult;
    /// Kill the process tree rooted at `pid`. Returns whether `pid` itself was
    /// terminated (matches the `window_control` contract).
    fn terminate_process_tree(&mut self, pid: u32) -> bool;
}

/// Pure switcher state. Methods take `&mut self` and, where they need side
/// effects, a `&mut impl SwitcherHost`. No interior mutability — calls are
/// serialised by the bridge on the UI thread (the same single-threaded
/// contract the C# ViewModel had on the dispatcher).
pub struct SwitcherState {
    /// Full enumerated set, unfiltered. `_windows` in the C# VM.
    windows: Vec<WindowRow>,
    /// Currently displayed subset. `FilteredWindows` in the C# VM.
    filtered: Vec<WindowRow>,
    /// Index into `filtered`. `None` when `filtered` is empty.
    selected: Option<usize>,
    search_text: String,
    is_grouped_by_process: bool,
    grouped_process_name: Option<String>,
    /// Index (into the *pre-group* filtered list) of the row that owned the
    /// grouping decision. Restored by `ungroup_from_process`.
    last_selected_before_grouping: Option<usize>,
    /// Whether pinyin matching is on. The bridge sets this from settings; the
    /// state needs it only for `flush_filter`.
    pinyin_on: bool,
}

impl Default for SwitcherState {
    fn default() -> Self {
        Self::new()
    }
}

impl SwitcherState {
    pub fn new() -> Self {
        Self {
            windows: Vec::new(),
            filtered: Vec::new(),
            selected: None,
            search_text: String::new(),
            is_grouped_by_process: false,
            grouped_process_name: None,
            last_selected_before_grouping: None,
            pinyin_on: false,
        }
    }

    pub fn set_pinyin_on(&mut self, on: bool) {
        self.pinyin_on = on;
    }

    /// Current filter rows — handed to Slint as the list model.
    pub fn filtered(&self) -> &[WindowRow] {
        &self.filtered
    }

    /// Index of the selected row within `filtered()`, or `None`.
    pub fn selected_index(&self) -> Option<usize> {
        self.selected
    }

    pub fn search_text(&self) -> &str {
        &self.search_text
    }

    pub fn is_grouped(&self) -> bool {
        self.is_grouped_by_process
    }

    /// Empty-state classification for the UI caption. `None` means "list has
    /// rows, show the list, not the empty panel".
    pub fn empty_state(&self) -> Option<EmptyState> {
        if !self.filtered.is_empty() {
            return None;
        }
        if self.windows.is_empty() {
            Some(EmptyState::NoWindowsAtAll)
        } else {
            Some(EmptyState::NoMatches)
        }
    }

    /// Replace the full window set from an enumeration, then re-apply the
    /// current view (grouping-aware). `select_second` is the Alt+Tab first-open
    /// hint: select index 1 if the resulting list has ≥2 rows.
    ///
    /// Mirrors `RefreshWindows` → `ApplyRefreshedWindows`.
    pub fn refresh<H: SwitcherHost>(&mut self, host: &mut H, select_second: bool) {
        let rows = host.enumerate();
        self.apply_rows(rows, select_second);
    }

    /// Apply a freshly enumerated window set (the bridge calls this after the
    /// background enumeration future resolves, so the hot path doesn't block).
    pub fn apply_rows(&mut self, rows: Vec<WindowRow>, select_second: bool) {
        self.windows = rows;
        if self.is_grouped_by_process {
            if let Some(proc) = self.grouped_process_name.clone() {
                let grouped: Vec<WindowRow> = self
                    .windows
                    .iter()
                    .filter(|w| w.process_name == proc)
                    .cloned()
                    .collect();
                if grouped.is_empty() {
                    // The grouped process disappeared entirely — exit grouping
                    // and fall through to the full filter, mirroring the C#
                    // `ExitGroupingMode(); FilterWindows(...)` branch.
                    self.exit_grouping();
                    self.rebuild_filtered(select_second);
                } else {
                    self.filtered = grouped;
                    self.repair_selection();
                    if self.selected.is_none() {
                        self.selected = Some(0);
                    }
                }
                return;
            }
            self.exit_grouping();
        }
        self.rebuild_filtered(select_second);
    }

    /// Clear the search box. The actual re-filter happens on the next
    /// `apply_rows` / `flush_filter`, matching `ClearSearch`'s "don't filter
    /// here — RefreshWindows will" comment.
    pub fn clear_search(&mut self) {
        self.search_text.clear();
    }

    /// Set the search text; mark that a debounced filter is owed. The bridge
    /// schedules the 30ms timer; `flush_filter` does the real work.
    pub fn set_search_text(&mut self, text: &str) {
        self.search_text = text.to_string();
    }

    /// Run the filter now. `select_second` is honoured only on the very first
    /// open (the bridge passes `alt_tab_mode`); a later keystroke-driven
    /// `flush_filter` keeps the current selection-ish position — replicating
    /// `FilterWindows(selectSecond)` being called with `false` from the timer.
    pub fn flush_filter(&mut self, select_second: bool) {
        self.rebuild_filtered(select_second);
    }

    /// Rebuild `filtered` from `windows` given `search_text`, then fix the
    /// selection. When `select_second` is true and the result has ≥2 rows, the
    /// selection lands on index 1 (Alt+Tab opens on the second window).
    fn rebuild_filtered(&mut self, select_second: bool) {
        let keep = if self.search_text.trim().is_empty() {
            self.windows.clone()
        } else {
            let pinyin = &mut self.pinyin_service();
            let needle = self.search_text.to_lowercase();
            self.windows
                .iter()
                .filter(|w| row_matches(w, &needle, pinyin, self.pinyin_on))
                .cloned()
                .collect()
        };
        self.filtered = keep;
        self.repair_selection();
        if self.search_text.trim().is_empty() && select_second && self.filtered.len() > 1 {
            // Alt+Tab first-open: the current foreground window is on row 0,
            // so the user's intent is the *next* one — jump to index 1.
            self.selected = Some(1);
        }
    }

    /// Borrow a transient `PinyinService` for filtering. The cache lives across
    /// `SwitcherState` instantiations only if the *caller* reuses the service —
    /// here we create one per filter pass; the bridge owns the long-lived one
    /// (Step 7c) and will pass it in instead. For Step 7a the granularity matches
    /// the C# behaviour closely enough that cache-hit invariants still hold
    /// within a single filter pass.
    fn pinyin_service(&self) -> PinyinService {
        PinyinService::new()
    }

    /// Move selection, wrapping around. `prev = true` → toward head, else tail.
    /// No-op when `filtered` is empty. Mirrors `MoveSelectionUp/Down`.
    pub fn move_selection(&mut self, prev: bool) {
        if self.filtered.is_empty() {
            self.selected = None;
            return;
        }
        let cur = self.selected.unwrap_or(0);
        let n = self.filtered.len();
        let next = if prev {
            if cur == 0 { n - 1 } else { cur - 1 }
        } else {
            if cur + 1 >= n { 0 } else { cur + 1 }
        };
        self.selected = Some(next);
    }

    /// Group `filtered` down to the selected row's process (right-arrow). The
    /// bridge records the prior selection index so `ungroup` can restore it.
    /// Mirrors `GroupByProcess`.
    pub fn group_by_process(&mut self) {
        if self.is_grouped_by_process {
            return;
        }
        let idx = match self.selected {
            Some(i) => i,
            None => return,
        };
        let proc = match self.filtered.get(idx) {
            Some(w) => w.process_name.clone(),
            None => return,
        };
        self.last_selected_before_grouping = Some(idx);
        self.grouped_process_name = Some(proc.clone());
        self.is_grouped_by_process = true;
        self.filtered.retain(|w| w.process_name == proc);
        self.selected = if self.filtered.is_empty() { None } else { Some(0) };
    }

    /// Leave grouping, rebuild the full filtered list, and try to land the
    /// selection back on (a row of) the process that owned the group. Mirrors
    /// `UngroupFromProcess`.
    pub fn ungroup_from_process(&mut self) {
        if !self.is_grouped_by_process {
            return;
        }
        let proc = self.grouped_process_name.take();
        self.is_grouped_by_process = false;
        self.last_selected_before_grouping = None;
        // Rebuild the full filtered view (preserving search text, if any) but
        // without the select_second first-open hint.
        self.rebuild_filtered(false);
        if let Some(p) = proc {
            if !self.filtered.is_empty() {
                let target = self
                    .filtered
                    .iter()
                    .position(|w| w.process_name == p)
                    .unwrap_or(0);
                self.selected = Some(target);
            }
        }
        if self.filtered.is_empty() {
            self.selected = None;
        }
    }

    /// Drop grouping state without touching the list. Called on every open
    /// (legacy `ResetGrouping` on activation / show).
    pub fn reset_grouping(&mut self) {
        if self.is_grouped_by_process {
            self.exit_grouping();
        }
    }

    fn exit_grouping(&mut self) {
        self.is_grouped_by_process = false;
        self.grouped_process_name = None;
        self.last_selected_before_grouping = None;
    }

    /// Close the selected window. When `close` reports `RootClosed`, drop the
    /// row from both the full set and the filtered view, then repair the
    /// selection to a neighbour. `OnlyDialogDismissed` leaves everything intact
    /// (the root window is still on screen). Mirrors `CloseSelectedWindow`.
    pub fn close_selected<H: SwitcherHost>(&mut self, host: &mut H) {
        let row = match self.selected.and_then(|i| self.filtered.get(i)) {
            Some(r) => r.clone(),
            None => return,
        };
        let cur_idx = self.selected.unwrap();
        let result = host.close(&row);
        if result == CloseResult::OnlyDialogDismissed {
            // Root still open — keep the list as-is (legacy `rootClosed` guard).
            return;
        }
        let id = row.id;
        self.windows.retain(|w| w.id != id);
        self.filtered.retain(|w| w.id != id);
        self.select_after_removal(cur_idx);
    }

    /// Kill the selected window's process tree, then drop every row sharing
    /// that pid from both lists. Mirrors `StopSelectedProcess`.
    pub fn stop_selected<H: SwitcherHost>(&mut self, host: &mut H) {
        let (pid, cur_idx) = match self.selected.and_then(|i| self.filtered.get(i)) {
            Some(r) => (r.process_id, self.selected.unwrap()),
            None => return,
        };
        host.terminate_process_tree(pid);
        self.windows.retain(|w| w.process_id != pid);
        self.filtered.retain(|w| w.process_id != pid);
        self.select_after_removal(cur_idx);
    }

    /// Activate the selected window; returns the outcome so the bridge can
    /// clear `ignore_alt_release` and drop back to hidden. No selection change.
    pub fn activate_selected<H: SwitcherHost>(&mut self, host: &mut H) -> Option<ActivationOutcome> {
        let row = match self.selected.and_then(|i| self.filtered.get(i)) {
            Some(r) => r.clone(),
            None => return None,
        };
        Some(host.activate(&row))
    }

    /// After a removal at `cur_idx`, pick a live neighbour: clamp to the new
    /// tail; if the view emptied but grouping is active, exit grouping and
    /// re-filter; otherwise clear selection. Mirrors `SelectWindowAfterRemoval`.
    fn select_after_removal(&mut self, cur_idx: usize) {
        if !self.filtered.is_empty() {
            let new_idx = cur_idx.min(self.filtered.len() - 1);
            self.selected = Some(new_idx);
        } else if self.is_grouped_by_process {
            self.exit_grouping();
            self.rebuild_filtered(false);
            self.selected = if self.filtered.is_empty() { None } else { Some(0) };
        } else {
            self.selected = None;
        }
    }

    /// Bring `self.selected` back into range after the filtered list shrinks.
    /// When the list is non-empty but the selection is unset (e.g. a fresh
    /// refresh with no `select_second`), default to the first row — matching
    /// legacy `FilterWindows` landing on `FilteredWindows[0]`.
    fn repair_selection(&mut self) {
        let n = self.filtered.len();
        self.selected = match (self.selected, n) {
            (_, 0) => None,
            (Some(i), _) if i < n => Some(i),
            (Some(_), _) => Some(n - 1),
            (None, _) => Some(0),
        };
    }
}

/// Plain substring match — title or process name, lowercase — plus the pinyin
/// leg when `pinyin_on`. This is `AppWindow.MatchesFilter` re-applied at the
/// row level so `SwitcherState` never depends on `AppWindow` (keeping it pure
/// and testable without the enumeration's HWND plumbing).
fn row_matches(row: &WindowRow, needle: &str, pinyin: &mut PinyinService, pinyin_on: bool) -> bool {
    if row.title.to_lowercase().contains(needle) || row.process_name.to_lowercase().contains(needle) {
        return true;
    }
    if pinyin_on
        && (pinyin.matches_pinyin(&row.title, needle)
            || pinyin.matches_pinyin(&row.process_name, needle))
    {
        return true;
    }
    false
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::RefCell;
    use std::rc::Rc;

    fn row(id: isize, title: &str, proc: &str) -> WindowRow {
        WindowRow {
            id,
            title: title.into(),
            process_name: proc.into(),
            monitor: 1,
            process_id: id as u32,
            icon_token: 0,
            is_elevated: false,
        }
    }

    /// A host that strips `host_id`s so the row set is fixed for the duration
    /// of the test and records every effect called on it.
    struct MockHost {
        rows: Vec<WindowRow>,
        log: Rc<RefCell<Vec<String>>>,
        /// What `close` should report for the next call (default `RootClosed`).
        next_close: Rc<RefCell<CloseResult>>,
    }

    impl MockHost {
        fn new(rows: Vec<WindowRow>) -> (Self, Rc<RefCell<Vec<String>>>) {
            let log = Rc::new(RefCell::new(Vec::new()));
            let host = MockHost {
                rows,
                log: log.clone(),
                next_close: Rc::new(RefCell::new(CloseResult::RootClosed)),
            };
            (host, log)
        }
        fn set_close(&self, r: CloseResult) {
            *self.next_close.borrow_mut() = r;
        }
    }

    impl SwitcherHost for MockHost {
        fn enumerate(&mut self) -> Vec<WindowRow> {
            self.log.borrow_mut().push("enumerate".into());
            self.rows.clone()
        }
        fn activate(&mut self, row: &WindowRow) -> ActivationOutcome {
            self.log.borrow_mut().push(format!("activate:{}", row.id));
            ActivationOutcome { target: crate::core::win32::Hwnd(row.id) }
        }
        fn close(&mut self, row: &WindowRow) -> CloseResult {
            let r = *self.next_close.borrow();
            self.log.borrow_mut().push(format!("close:{}:{:?}", row.id, r));
            r
        }
        fn terminate_process_tree(&mut self, pid: u32) -> bool {
            self.log.borrow_mut().push(format!("kill:{}", pid));
            true
        }
    }

    // ---- flush_filter ----

    #[test]
    fn flush_filter_empty_search_keeps_full_set() {
        let rows = vec![row(1, "A", "a"), row(2, "B", "b"), row(3, "C", "c")];
        let (mut h, _log) = MockHost::new(rows);
        let mut s = SwitcherState::new();
        s.refresh(&mut h, false);
        assert_eq!(s.filtered().len(), 3);
        assert_eq!(s.selected_index(), Some(0));
    }

    #[test]
    fn flush_filter_substring_matches() {
        // "alpha"/"beta"/"gamma": filter "t" matches only "beta" (contains 't').
        let rows = vec![row(1, "alpha", "a"), row(2, "beta", "b"), row(3, "gamma", "g")];
        let (mut h, _log) = MockHost::new(rows);
        let mut s = SwitcherState::new();
        s.refresh(&mut h, false);
        s.set_search_text("t");
        s.flush_filter(false);
        assert_eq!(s.filtered().len(), 1);
        assert_eq!(s.filtered()[0].id, 2);
        assert_eq!(s.selected_index(), Some(0));
    }

    #[test]
    fn flush_filter_select_second_on_alt_tab() {
        let rows = vec![row(1, "A", "a"), row(2, "B", "b")];
        let (mut h, _log) = MockHost::new(rows);
        let mut s = SwitcherState::new();
        s.refresh(&mut h, true); // select_second
        assert_eq!(s.selected_index(), Some(1));
    }

    #[test]
    fn flush_filter_select_second_short_list_stays_zero() {
        let rows = vec![row(1, "A", "a")];
        let (mut h, _log) = MockHost::new(rows);
        let mut s = SwitcherState::new();
        s.refresh(&mut h, true);
        assert_eq!(s.selected_index(), Some(0));
    }

    #[test]
    fn flush_filter_no_matches_clears_selected() {
        let rows = vec![row(1, "A", "a"), row(2, "B", "b")];
        let (mut h, _log) = MockHost::new(rows);
        let mut s = SwitcherState::new();
        s.refresh(&mut h, false);
        s.set_search_text("zzz");
        s.flush_filter(false);
        assert!(s.filtered().is_empty());
        assert_eq!(s.selected_index(), None);
        assert_eq!(s.empty_state(), Some(EmptyState::NoMatches));
    }

    #[test]
    fn empty_state_no_windows_at_all() {
        let (mut h, _log) = MockHost::new(vec![]);
        let mut s = SwitcherState::new();
        s.refresh(&mut h, false);
        assert_eq!(s.empty_state(), Some(EmptyState::NoWindowsAtAll));
    }

    // ---- move_selection ----

    #[test]
    fn move_selection_wraps_forward() {
        let rows = vec![row(1, "A", "a"), row(2, "B", "b"), row(3, "C", "c")];
        let (mut h, _log) = MockHost::new(rows);
        let mut s = SwitcherState::new();
        s.refresh(&mut h, false); // selected = 0
        s.move_selection(false); // 1
        s.move_selection(false); // 2
        s.move_selection(false); // wrap → 0
        assert_eq!(s.selected_index(), Some(0));
    }

    #[test]
    fn move_selection_wraps_backward() {
        let rows = vec![row(1, "A", "a"), row(2, "B", "b"), row(3, "C", "c")];
        let (mut h, _log) = MockHost::new(rows);
        let mut s = SwitcherState::new();
        s.refresh(&mut h, false); // 0
        s.move_selection(true); // wrap → 2 (last)
        assert_eq!(s.selected_index(), Some(2));
    }

    #[test]
    fn move_selection_empty_is_noop() {
        let (mut h, _log) = MockHost::new(vec![]);
        let mut s = SwitcherState::new();
        s.refresh(&mut h, false);
        s.move_selection(false);
        assert_eq!(s.selected_index(), None);
    }

    // ---- grouping ----

    #[test]
    fn group_by_process_keeps_only_matching_process() {
        let rows = vec![
            row(1, "Explorer1", "explorer"),
            row(2, "Notepad1", "notepad"),
            row(3, "Explorer2", "explorer"),
        ];
        let (mut h, _log) = MockHost::new(rows);
        let mut s = SwitcherState::new();
        s.refresh(&mut h, false); // selected = 0 → explorer
        s.group_by_process();
        assert!(s.is_grouped());
        assert_eq!(s.filtered().len(), 2);
        assert!(s.filtered().iter().all(|w| w.process_name == "explorer"));
        assert_eq!(s.selected_index(), Some(0));
    }

    #[test]
    fn ungroup_restores_to_grouped_process_row() {
        let rows = vec![
            row(1, "Explorer1", "explorer"),
            row(2, "Notepad1", "notepad"),
            row(3, "Explorer2", "explorer"),
        ];
        let (mut h, _log) = MockHost::new(rows);
        let mut s = SwitcherState::new();
        s.refresh(&mut h, false);
        s.group_by_process();
        assert!(s.is_grouped());
        s.ungroup_from_process();
        assert!(!s.is_grouped());
        assert_eq!(s.filtered().len(), 3);
        // Selection lands on the first row of the previously-grouped process.
        let sel = s.selected_index().unwrap();
        assert_eq!(s.filtered()[sel].process_name, "explorer");
    }

    #[test]
    fn ungroup_falls_back_to_zero_when_process_absent() {
        let rows = vec![
            row(1, "Explorer1", "explorer"),
            row(2, "Notepad1", "notepad"),
        ];
        let (mut h, _log) = MockHost::new(rows.clone());
        let mut s = SwitcherState::new();
        s.refresh(&mut h, false); // selected 0 = explorer
        s.group_by_process();
        // Simulate the explorer windows vanishing from the host.
        h.rows.retain(|w| w.process_name != "explorer");
        s.refresh(&mut h, false); // grouping auto-exits (empty group)
        assert!(!s.is_grouped());
        assert_eq!(s.filtered().len(), 1);
        assert_eq!(s.selected_index(), Some(0));
    }

    // ---- close_selected ----

    #[test]
    fn close_root_closed_drops_row_and_repairs_selection() {
        let rows = vec![row(1, "A", "a"), row(2, "B", "b"), row(3, "C", "c")];
        let (mut h, log) = MockHost::new(rows);
        let mut s = SwitcherState::new();
        s.refresh(&mut h, false); // selected 0
        s.close_selected(&mut h);
        assert_eq!(log.borrow().as_slice(), &["enumerate".to_string(), "close:1:RootClosed".to_string()]);
        assert_eq!(s.filtered().len(), 2);
        // Removed index 0 → selection moves to new index 0 (the old 1).
        assert_eq!(s.selected_index(), Some(0));
        assert_eq!(s.filtered()[0].id, 2);
    }

    #[test]
    fn close_only_dialog_dismissed_keeps_row() {
        let rows = vec![row(1, "A", "a"), row(2, "B", "b"), row(3, "C", "c")];
        let (mut h, _log) = MockHost::new(rows);
        let mut s = SwitcherState::new();
        s.refresh(&mut h, false);
        h.set_close(CloseResult::OnlyDialogDismissed);
        let before_len = s.filtered().len();
        s.close_selected(&mut h);
        // Row NOT removed.
        assert_eq!(s.filtered().len(), before_len);
        // Selection unchanged.
        assert_eq!(s.selected_index(), Some(0));
    }

    // ---- stop_selected ----

    #[test]
    fn stop_selected_removes_all_rows_of_pid() {
        let rows = vec![
            WindowRow { id: 1, title: "A".into(), process_name: "explorer".into(), monitor: 1, process_id: 100, icon_token: 0, is_elevated: false },
            WindowRow { id: 2, title: "B".into(), process_name: "notepad".into(), monitor: 1, process_id: 200, icon_token: 0, is_elevated: false },
            WindowRow { id: 3, title: "C".into(), process_name: "explorer".into(), monitor: 1, process_id: 100, icon_token: 0, is_elevated: false },
        ];
        let (mut h, log) = MockHost::new(rows);
        let mut s = SwitcherState::new();
        s.refresh(&mut h, false); // selected 0 (id 1, pid 100)
        s.stop_selected(&mut h);
        assert_eq!(log.borrow().last().unwrap(), "kill:100");
        assert_eq!(s.filtered().len(), 1);
        assert_eq!(s.filtered()[0].process_id, 200);
    }

    // ---- refresh under grouping ----

    #[test]
    fn refresh_in_group_mode_keeps_only_grouped_process() {
        let rows = vec![
            row(1, "Explorer1", "explorer"),
            row(2, "Notepad1", "notepad"),
            row(3, "Explorer2", "explorer"),
        ];
        let (mut h, _log) = MockHost::new(rows);
        let mut s = SwitcherState::new();
        s.refresh(&mut h, false);
        s.group_by_process(); // grouped to explorer, filtered = [id1, id3]
        // New enumeration: explorer windows changed titles.
        h.rows = vec![
            row(1, "Explorer1-renamed", "explorer"),
            row(3, "Explorer2-renamed", "explorer"),
            row(4, "Notepad2", "notepad"),
        ];
        s.refresh(&mut h, false);
        assert!(s.is_grouped());
        assert_eq!(s.filtered().len(), 2);
        assert!(s.filtered().iter().all(|w| w.process_name == "explorer"));
    }

    // ---- activate_selected ----

    #[test]
    fn activate_selected_invokes_host_and_returns_outcome() {
        let rows = vec![row(1, "A", "a"), row(2, "B", "b")];
        let (mut h, log) = MockHost::new(rows);
        let mut s = SwitcherState::new();
        s.refresh(&mut h, false);
        s.move_selection(false); // → index 1 (id 2)
        let out = s.activate_selected(&mut h).expect("non-empty list");
        assert_eq!(out.target.raw(), 2);
        assert_eq!(log.borrow().last().unwrap(), "activate:2");
        // No row removed, no selection change.
        assert_eq!(s.filtered().len(), 2);
        assert_eq!(s.selected_index(), Some(1));
    }

    // ---- pinyin leg ----

    #[test]
    fn flush_filter_pinyin_on_matches_initials() {
        let rows = vec![WindowRow {
            id: 1, title: "记事本".into(), process_name: "explorer".into(),
            monitor: 1, process_id: 1, icon_token: 0, is_elevated: false,
        }];
        let (mut h, _log) = MockHost::new(rows);
        let mut s = SwitcherState::new();
        s.set_pinyin_on(true);
        s.refresh(&mut h, false);
        s.set_search_text("jsb");
        s.flush_filter(false);
        assert_eq!(s.filtered().len(), 1);
    }

    #[test]
    fn flush_filter_pinyin_off_misses_initials() {
        let rows = vec![WindowRow {
            id: 1, title: "记事本".into(), process_name: "explorer".into(),
            monitor: 1, process_id: 1, icon_token: 0, is_elevated: false,
        }];
        let (mut h, _log) = MockHost::new(rows);
        let mut s = SwitcherState::new();
        s.set_pinyin_on(false);
        s.refresh(&mut h, false);
        s.set_search_text("jsb");
        s.flush_filter(false);
        assert!(s.filtered().is_empty());
    }
}