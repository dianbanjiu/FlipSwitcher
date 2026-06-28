//! Persisted application settings.
//!
//! Faithful port of `legacy/Services/SettingsService.cs` + `AppSettings`.
//! Defaults **must not drift** — the settings file is forward/backward-compatible
//! across versions because partial files fall back to these defaults.
//!
//! Path: `%AppData%/FlipSwitcher/settings.json`. Atomic write: `.tmp` then `rename`.

use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use std::sync::OnceLock;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
pub enum AppTheme {
    #[default]
    #[serde(rename = "Dark")]
    Dark,
    #[serde(rename = "Light")]
    Light,
    #[serde(rename = "Latte")]
    Latte,
    #[serde(rename = "Mocha")]
    Mocha,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
pub enum AppLanguage {
    #[default]
    #[serde(rename = "English")]
    English,
    #[serde(rename = "Chinese")]
    Chinese,
    #[serde(rename = "ChineseTraditional")]
    ChineseTraditional,
}

/// Settings model — every field has a default that matches `AppSettings` in C#.
/// Missing/extra fields in an on-disk file are tolerated: unknown fields are
/// ignored (serde default), and a missing field takes its `default`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AppSettings {
    #[serde(default = "d_true")]
    pub use_alt_tab: bool,
    #[serde(default = "d_false")]
    pub use_alt_space: bool,
    #[serde(default = "d_false")]
    pub start_with_windows: bool,
    #[serde(default = "d_true")]
    pub hide_on_focus_lost: bool,
    #[serde(default)]
    pub theme: AppTheme,
    #[serde(default = "d_false")]
    pub run_as_admin: bool,
    #[serde(default)]
    pub language: AppLanguage,
    #[serde(default = "d_false")]
    pub check_for_updates: bool,
    #[serde(default = "d_empty_string")]
    pub font_family: String,
    #[serde(default = "d_false")]
    pub enable_pinyin_search: bool,
    #[serde(default = "d_false")]
    pub show_monitor_info: bool,
    #[serde(default = "d_false")]
    pub follow_system_theme: bool,
    #[serde(default = "d_false")]
    pub open_search_on_activation: bool,
    #[serde(default = "d_false")]
    pub show_on_mouse_screen: bool,
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            use_alt_tab: true,
            use_alt_space: false,
            start_with_windows: false,
            hide_on_focus_lost: true,
            theme: AppTheme::Dark,
            run_as_admin: false,
            language: AppLanguage::English,
            check_for_updates: false,
            font_family: String::new(),
            enable_pinyin_search: false,
            show_monitor_info: false,
            follow_system_theme: false,
            open_search_on_activation: false,
            show_on_mouse_screen: false,
        }
    }
}

// serde default fns (can't pass `true`/`false` literals directly).
fn d_true() -> bool {
    true
}
fn d_false() -> bool {
    false
}
fn d_empty_string() -> String {
    String::new()
}

/// How settings are loaded from / persisted to disk. The trait is how the rest
/// of the code touches settings, and how tests swap in an in-memory store.
pub trait SettingsStore: Send + Sync {
    fn load(&self) -> AppSettings;
    fn save(&self, settings: &AppSettings) -> io::Result<()>;
}

/// Filesystem-backed store at `%AppData%/FlipSwitcher/settings.json`.
pub struct FileSettingsStore {
    path: PathBuf,
}

impl FileSettingsStore {
    pub fn new(path: PathBuf) -> Self {
        Self { path }
    }

    /// `%AppData%/FlipSwitcher/settings.json` via `SHGetKnownFolderPath` if
    /// available, else `USERPROFILE\AppData\Roaming`.
    pub fn default_path() -> PathBuf {
        if let Some(base) = known_appdata() {
            return base.join("FlipSwitcher").join("settings.json");
        }
        // Fallback: environment variable.
        if let Some(roaming) = std::env::var_os("APPDATA") {
            return PathBuf::from(roaming)
                .join("FlipSwitcher")
                .join("settings.json");
        }
        PathBuf::from("FlipSwitcher").join("settings.json")
    }
}

fn known_appdata() -> Option<PathBuf> {
    use std::os::windows::ffi::OsStringExt;
    use windows::Win32::System::Com::CoTaskMemFree;
    use windows::Win32::UI::Shell::{SHGetKnownFolderPath, KF_FLAG_DEFAULT, FOLDERID_RoamingAppData};

    unsafe {
        let pstr = SHGetKnownFolderPath(&FOLDERID_RoamingAppData, KF_FLAG_DEFAULT, None).ok()?;
        let wide: Vec<u16> = {
            let mut n = 0usize;
            while *pstr.0.add(n) != 0 {
                n += 1;
            }
            std::slice::from_raw_parts(pstr.0, n).to_vec()
        };
        // PWSTR is a raw pointer wrapper; SHGetKnownFolderPath allocates with the
        // COM allocator, so we must free it ourselves.
        CoTaskMemFree(Some(pstr.0 as *const _));
        Some(PathBuf::from(std::ffi::OsString::from_wide(&wide)))
    }
}

impl SettingsStore for FileSettingsStore {
    fn load(&self) -> AppSettings {
        match fs::read(&self.path) {
            Ok(bytes) => serde_json::from_slice::<AppSettings>(&bytes).unwrap_or_default(),
            Err(_) => AppSettings::default(),
        }
    }

    fn save(&self, settings: &AppSettings) -> io::Result<()> {
        let parent = self.path.parent().filter(|p| !p.as_os_str().is_empty());
        if let Some(dir) = parent {
            if !dir.exists() {
                fs::create_dir_all(dir)?;
            }
        }
        let json = serde_json::to_string_pretty(settings)
            .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))?;

        // Atomic write: write `.tmp`, then rename over the target (POSIX atomic;
        // on Windows, MoveFileEx replaces too).
        let tmp = tmp_path(&self.path);
        fs::write(&tmp, json.as_bytes())?;
        atomic_rename(&tmp, &self.path)?;
        Ok(())
    }
}

fn tmp_path(p: &Path) -> PathBuf {
    let mut s = p.as_os_str().to_owned();
    s.push(".tmp");
    PathBuf::from(s)
}

#[cfg(unix)]
fn atomic_rename(from: &Path, to: &Path) -> io::Result<()> {
    fs::rename(from, to)
}

#[cfg(windows)]
fn atomic_rename(from: &Path, to: &Path) -> io::Result<()> {
    // std::fs::rename on Windows is implemented with MoveFileExW(REPLACE_EXISTING),
    // which is atomic enough for our purposes (single sink file).
    fs::rename(from, to)
}

/// On-save subscription. The callback is `&self` — invoked live at fire time,
/// never collected into a snapshot (which `Box<dyn Fn>` would not allow).
type SubCb = Box<dyn Fn() + Send + Sync>;

/// In-memory store for tests.
pub struct MemorySettingsStore {
    inner: std::sync::Mutex<Option<AppSettings>>,
}

impl MemorySettingsStore {
    pub fn new(initial: AppSettings) -> Self {
        Self {
            inner: std::sync::Mutex::new(Some(initial)),
        }
    }
    pub fn empty() -> Self {
        Self {
            inner: std::sync::Mutex::new(None),
        }
    }
}

impl SettingsStore for MemorySettingsStore {
    fn load(&self) -> AppSettings {
        self.inner
            .lock()
            .unwrap()
            .clone()
            .unwrap_or_default()
    }
    fn save(&self, settings: &AppSettings) -> io::Result<()> {
        *self.inner.lock().unwrap() = Some(settings.clone());
        Ok(())
    }
}

// ============================================================================
// SettingsService singleton (mirrors `SettingsService.Instance`)
// ============================================================================

/// Process-wide settings service. Holds the current settings, notifies
/// subscribers on save, and writes through to the backing store.
pub struct SettingsService {
    current: std::sync::RwLock<AppSettings>,
    store: Box<dyn SettingsStore>,
    subs: std::sync::Mutex<Vec<Option<SubCb>>>,
}

static SETTINGS_SERVICE: OnceLock<SettingsService> = OnceLock::new();

/// A handle that drops the subscription when dropped.
pub struct Subscription {
    id: usize,
}

impl Drop for Subscription {
    fn drop(&mut self) {
        if let Some(svc) = SETTINGS_SERVICE.get() {
            svc.remove_sub(self.id);
        }
    }
}

impl SettingsService {
    /// Initialise the global service with the given store. Idempotent — first
    /// call wins. Subsequent calls are ignored (returns the existing instance).
    pub fn init(store: Box<dyn SettingsStore>) -> &'static SettingsService {
        SETTINGS_SERVICE.get_or_init(|| {
            let current = store.load();
            SettingsService {
                current: std::sync::RwLock::new(current),
                store,
                subs: std::sync::Mutex::new(Vec::new()),
            }
        })
    }

    /// Default-initialise using `FileSettingsStore` at the platform default path.
    pub fn init_default() -> &'static SettingsService {
        Self::init(Box::new(FileSettingsStore::new(FileSettingsStore::default_path())))
    }

    pub fn global() -> &'static SettingsService {
        SETTINGS_SERVICE.get().expect("SettingsService not initialised")
    }

    pub fn settings(&self) -> AppSettings {
        self.current.read().unwrap().clone()
    }

    /// Save the *current* settings to the store and fire subscribers.
    pub fn save_current(&self) {
        let s = self.settings();
        let _ = self.store.save(&s);
        self.fire();
    }

    /// Overwrite the in-memory settings (without persisting), then persist and fire.
    pub fn update_and_save(&self, f: impl FnOnce(&mut AppSettings)) {
        {
            let mut w = self.current.write().unwrap();
            f(&mut w);
            let snapshot = w.clone();
            drop(w);
            let _ = self.store.save(&snapshot);
        }
        self.fire();
    }

    pub fn subscribe(&self, cb: impl Fn() + Send + Sync + 'static) -> Subscription {
        let mut g = self.subs.lock().unwrap();
        let id = g.len();
        g.push(Some(Box::new(cb)));
        Subscription { id }
    }

    fn fire(&self) {
        // Enumerate live subscriptions; invoke each inside the lock. Subscriber
        // callbacks must not re-enter `save_current`/`subscribe` (the lock is
        // not reentrant) — they are theme/font style switches in the real app
        // and observe settings after this returns.
        let g = self.subs.lock().unwrap();
        for slot in g.iter() {
            if let Some(cb) = slot.as_ref() {
                cb();
            }
        }
    }

    fn remove_sub(&self, id: usize) {
        let mut g = self.subs.lock().unwrap();
        if id < g.len() {
            g[id] = None;
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex;

    fn fresh_store(initial: AppSettings) -> MemorySettingsStore {
        MemorySettingsStore::new(initial)
    }

    #[test]
    fn defaults_match_csharp() {
        let s = AppSettings::default();
        assert!(s.use_alt_tab);
        assert!(!s.use_alt_space);
        assert!(!s.start_with_windows);
        assert!(s.hide_on_focus_lost);
        assert_eq!(s.theme, AppTheme::Dark);
        assert!(!s.run_as_admin);
        assert_eq!(s.language, AppLanguage::English);
        assert!(!s.check_for_updates);
        assert_eq!(s.font_family, "");
        assert!(!s.enable_pinyin_search);
        assert!(!s.show_monitor_info);
        assert!(!s.follow_system_theme);
        assert!(!s.open_search_on_activation);
        assert!(!s.show_on_mouse_screen);
    }

    #[test]
    fn roundtrip_preserves_all_fields() {
        let mut s = AppSettings::default();
        s.use_alt_tab = false;
        s.use_alt_space = true;
        s.start_with_windows = true;
        s.hide_on_focus_lost = false;
        s.theme = AppTheme::Latte;
        s.run_as_admin = true;
        s.language = AppLanguage::ChineseTraditional;
        s.check_for_updates = true;
        s.font_family = "Segoe UI".to_string();
        s.enable_pinyin_search = true;
        s.show_monitor_info = true;
        s.follow_system_theme = true;
        s.open_search_on_activation = true;
        s.show_on_mouse_screen = true;

        let json = serde_json::to_string(&s).unwrap();
        let back: AppSettings = serde_json::from_str(&json).unwrap();
        assert_eq!(s, back);
    }

    #[test]
    fn partial_json_falls_back_to_defaults() {
        // Themes-roundtrip only the use_alt_tab field; everything else defaults.
        let json = r#"{"use_alt_tab": false}"#;
        let s: AppSettings = serde_json::from_str(json).unwrap();
        assert!(!s.use_alt_tab);
        // The rest fall back to AppSettings::default values.
        assert!(s.hide_on_focus_lost);
        assert_eq!(s.theme, AppTheme::Dark);
        assert_eq!(s.font_family, "");
    }

    #[test]
    fn unknown_fields_ignored() {
        let json = r#"{"use_alt_tab": true, "future_field": 42}"#;
        let s: AppSettings = serde_json::from_str(json).unwrap();
        assert!(s.use_alt_tab);
    }

    #[test]
    fn memory_store_save_then_load() {
        let store = fresh_store(AppSettings::default());
        let mut s = store.load();
        s.run_as_admin = true;
        let _ = store.save(&s);
        assert!(store.load().run_as_admin);
    }

    #[test]
    fn atomic_write_via_tmp_then_replace() {
        // Round-trip a settings file to a temp path and assert the contents
        // are observable only *after* save returns (the .tmp disappear too).
        let dir = std::env::temp_dir();
        let path = dir.join(format!(
            "flipswitcher-settings-test-{}.json",
            std::process::id()
        ));
        // guard against leftover
        let _ = std::fs::remove_file(&path);
        let _ = std::fs::remove_file(format!("{}.tmp", path.to_string_lossy()));

        let store = FileSettingsStore::new(path.clone());
        let mut s = AppSettings::default();
        s.font_family = "TestFont".into();
        let _ = store.save(&s);

        // Final file exists with the serialised form; .tmp is gone.
        assert!(path.exists(), "final settings file should exist");
        let tmp = tmp_path(&path);
        assert!(!tmp.exists(), "temp file should be renamed away");

        let bytes = std::fs::read(&path).unwrap();
        let parsed: AppSettings = serde_json::from_slice(&bytes).unwrap();
        assert_eq!(parsed.font_family, "TestFont");

        let _ = std::fs::remove_file(&path);
    }

    /// Subscribe to SettingsService changes and assert the callback fires on save.
    /// Note: the SettingsService is a process-global `OnceLock`; tests that touch
    /// it race with each other, so we guard the whole group with a single mutex.
    static SVC_GUARD: Mutex<()> = Mutex::new(());

    #[test]
    fn subscribe_fires_on_save_current() {
        let _g = SVC_GUARD.lock().unwrap();
        // Initialise the global with an in-memory store. After first init the
        // service global wins; subsequent tests that re-init get the same
        // instance — acceptable since they only assert round-trip observability.
        let store = fresh_store(AppSettings::default());
        let svc = SettingsService::init(Box::new(store));
        let fired = std::sync::Arc::new(std::sync::atomic::AtomicUsize::new(0));
        {
            let fired2 = fired.clone();
            let _sub = svc.subscribe(move || {
                fired2.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
            });
            svc.save_current();
            assert!(fired.load(std::sync::atomic::Ordering::SeqCst) >= 1);
        }
    }

    #[test]
    fn file_store_load_missing_file_is_defaults() {
        let store = FileSettingsStore::new(std::env::temp_dir().join(
            "flipswitcher-no-such-file-12345.json",
        ));
        let s = store.load();
        assert_eq!(s, AppSettings::default());
    }
}
