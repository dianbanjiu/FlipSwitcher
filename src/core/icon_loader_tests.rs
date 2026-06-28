//! Mock-driven tests for the icon loader, focused on the per-window invariant
//! (§3.5 of `docs/rust-rewrite-design-step1-3.md`).

#![allow(clippy::needless_borrow)]

use super::*;
use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::Mutex;

/// Stand-in image: callers can tell apart "icons X and Y".
fn opaque_img(id: u8) -> IconImage {
    IconImage {
        width: 1,
        height: 1,
        pixels: vec![id, 0, 0, 255],
    }
}

struct FakeUniverse {
    wins: HashMap<Hwnd, (u32, String /*class*/, isize /*window hicon*/, Option<IconImage>)>,
    children: HashMap<Hwnd, Vec<(String, Hwnd, u32)>>, // child class probes
    process_paths: HashMap<u32, Option<String>>,
    shell_icons: HashMap<String, Option<IconImage>>,
    extract_icons: HashMap<String, Option<IconImage>>,
    window_icons: HashMap<isize, Option<IconImage>>,
    files: HashMap<PathBuf, Vec<u8>>, // path → file contents
    shell_calls: u32,
    extract_calls: u32,
    process_path_calls: u32,
    window_icon_calls: u32,
}

struct MockApi {
    state: Mutex<FakeUniverse>,
    current_pid: u32,
}

impl MockApi {
    fn new() -> Self {
        Self {
            state: Mutex::new(FakeUniverse {
                wins: HashMap::new(),
                children: HashMap::new(),
                process_paths: HashMap::new(),
                shell_icons: HashMap::new(),
                extract_icons: HashMap::new(),
                window_icons: HashMap::new(),
                files: HashMap::new(),
                shell_calls: 0,
                extract_calls: 0,
                process_path_calls: 0,
                window_icon_calls: 0,
            }),
            current_pid: 9999,
        }
    }
}

impl Win32Api for MockApi {
    fn is_window_visible(&self, _: Hwnd) -> bool {
        true
    }
    fn is_iconic(&self, _: Hwnd) -> bool {
        false
    }
    fn is_zoomed(&self, _: Hwnd) -> bool {
        false
    }
    fn get_window_text(&self, _: Hwnd) -> String {
        String::new()
    }
    fn get_window_text_length(&self, _: Hwnd) -> i32 {
        0
    }
    fn get_class_name(&self, hwnd: Hwnd) -> String {
        self.state
            .lock()
            .unwrap()
            .wins
            .get(&hwnd)
            .map(|(_, cls, _, _)| cls.clone())
            .unwrap_or_default()
    }
    fn get_window_long_ptr(&self, _: Hwnd, _: crate::core::win32::Gwlp) -> isize {
        0
    }
    fn get_window_rect(&self, _: Hwnd) -> Option<crate::core::win32::Rect> {
        None
    }
    fn get_window(&self, _: Hwnd, _: crate::core::win32::Gw) -> Option<Hwnd> {
        None
    }
    fn get_window_thread_process_id(&self, hwnd: Hwnd) -> (u32, u32) {
        (
            1,
            self.state.lock().unwrap().wins.get(&hwnd).map(|(p, _, _, _)| *p).unwrap_or(0),
        )
    }
    fn get_shell_window(&self) -> Option<Hwnd> {
        None
    }
    fn enum_windows(
        &self,
        _cb: &mut dyn FnMut(Hwnd) -> bool,
    ) -> Result<(), crate::core::win32::Win32Error> {
        Ok(())
    }
    fn is_cloaked(&self, _: Hwnd) -> bool {
        false
    }
    fn get_layered_window_attributes(&self, _: Hwnd) -> Option<(u32, u8, u32)> {
        None
    }
    fn get_window_placement_show_cmd(&self, _: Hwnd) -> Option<i32> {
        None
    }
    fn get_window_placement_flags(&self, _: Hwnd) -> Option<i32> {
        None
    }
    fn enum_display_monitors(&self) -> Vec<(crate::core::win32::Hmonitor, crate::core::win32::Rect)> {
        Vec::new()
    }
    fn monitor_from_window(
        &self,
        _: Hwnd,
        _: crate::core::win32::MonitorFlag,
    ) -> Option<crate::core::win32::Hmonitor> {
        None
    }
    fn query_full_process_image_name(&self, pid: u32) -> Option<String> {
        let mut g = self.state.lock().unwrap();
        g.process_path_calls += 1;
        g.process_paths.get(&pid).cloned().unwrap_or(None)
    }
    fn open_process_query_limited(&self, _: u32) -> Option<crate::core::win32::OwnedProcessHandle> {
        None
    }
    fn process_elevation(&self, _: &crate::core::win32::OwnedProcessHandle) -> bool {
        false
    }
    fn current_process_id(&self) -> u32 {
        self.current_pid
    }
    fn get_window_icon_handle(&self, hwnd: Hwnd) -> Option<isize> {
        let mut g = self.state.lock().unwrap();
        g.window_icon_calls += 1;
        g.wins
            .get(&hwnd)
            .map(|(_, _, h, _)| Some(*h))
            .unwrap_or(None)
    }
    fn post_close(&self, _: Hwnd) {}
    fn find_child_window_class_pid(
        &self,
        parent: Hwnd,
        class: &str,
        exclude_pid: u32,
    ) -> Option<(Hwnd, u32)> {
        let g = self.state.lock().unwrap();
        g.children
            .get(&parent)
            .and_then(|v| {
                v.iter()
                    .find(|(c, _, _)| c == class)
                    .filter(|(_, _, pid)| *pid != exclude_pid)
                    .map(|(_, h, p)| (*h, *p))
            })
    }
    fn shell_extract_icon(&self, exe_path: &str) -> Option<IconImage> {
        let mut g = self.state.lock().unwrap();
        g.shell_calls += 1;
        g.shell_icons.get(exe_path).cloned().unwrap_or(None)
    }
    fn extract_associated_icon(&self, exe_path: &str) -> Option<IconImage> {
        let mut g = self.state.lock().unwrap();
        g.extract_calls += 1;
        g.extract_icons.get(exe_path).cloned().unwrap_or(None)
    }
    fn window_icon_to_image(&self, hicon: isize) -> Option<IconImage> {
        let g = self.state.lock().unwrap();
        g.window_icons.get(&hicon).cloned().unwrap_or(None)
    }
}

struct FsProbe {
    files: Mutex<HashMap<PathBuf, Vec<u8>>>,
}

impl IconFs for FsProbe {
    fn exists(&self, path: &std::path::Path) -> bool {
        self.files.lock().unwrap().contains_key(path)
    }
    fn read_to_string(&self, path: &std::path::Path) -> Option<String> {
        self.files
            .lock()
            .unwrap()
            .get(path)
            .and_then(|b| std::str::from_utf8(b).ok().map(|s| s.to_string()))
    }
}

fn app_window(hwnd: Hwnd, pid: u32, class: &str) -> AppWindow {
    AppWindow {
        handle: hwnd,
        title: "T".into(),
        class_name: class.into(),
        process_id: pid,
        process_name: "p".into(),
        is_minimized: false,
        is_maximized: false,
        is_topmost: false,
        owner_kept: None,
    }
}

#[test]
fn per_window_icon_does_not_pollute_exe_cache() {
    // Two windows shared pid=42 (explorer.exe-like), different per-window icons.
    let mock = MockApi::new();
    {
        let mut g = mock.state.lock().unwrap();
        g.wins.insert(Hwnd(1), (42, "Explorer".into(), 0xAB01, Some(opaque_img(1))));
        g.wins.insert(Hwnd(2), (42, "Explorer".into(), 0xAB02, Some(opaque_img(2))));
        g.window_icons.insert(0xAB01, Some(opaque_img(1)));
        g.window_icons.insert(0xAB02, Some(opaque_img(2)));
        // No shell icon, no extract — so all paths lead into per-hwnd cache.
    }
    let fs = FsProbe { files: Mutex::new(HashMap::new()) };
    let mut cache = IconCache::new(mock, fs);
    let w1 = app_window(Hwnd(1), 42, "Explorer");
    let w2 = app_window(Hwnd(2), 42, "Explorer");
    let i1 = cache.load_window_icon(&w1);
    let i2 = cache.load_window_icon(&w2);
    assert_eq!(i1, Some(opaque_img(1)));
    assert_eq!(i2, Some(opaque_img(2)));
    // Both keyed per-hwnd.
    assert_eq!(cache.window_icon(Hwnd(1)), Some(&opaque_img(1)));
    assert_eq!(cache.window_icon(Hwnd(2)), Some(&opaque_img(2)));
    // **Invariant**: exe cache is empty.
    assert_eq!(cache.exe_cache_len(), 0);
    assert_eq!(cache.exe_writes(), 0);
}

#[test]
fn exe_cache_hit_calls_shell_once_for_three_windows() {
    let mock = MockApi::new();
    let exe = "C:\\windows\\explorer.exe";
    {
        let mut g = mock.state.lock().unwrap();
        for id in 1..=3u64 {
            g.wins.insert(Hwnd(id as isize), (42, "Explorer".into(), 0, None));
        }
        // Per-window handle returns None (icon call stub returns None) → fall through.
        // We plug the fall-through by NOT setting window_icons; shell path returns the same image.
        for id in 1..=3u64 {
            g.window_icons.insert(0, None); // hicon 0 returns nothing
        }
        g.process_paths.insert(42, Some(exe.to_string()));
        g.shell_icons.insert(exe.to_string(), Some(opaque_img(9)));
    }
    let fs = FsProbe { files: Mutex::new(HashMap::new()) };
    let mut cache = IconCache::new(mock, fs);

    for id in 1..=3u64 {
        let w = app_window(Hwnd(id as isize), 42, "Explorer");
        // window_icon_calls: get_window_icon_handle returns 0 (hicon=0 stored),
        // window_icon_to_image(0) returns None → shell fallback path.
        // Make hicon None by overriding state.wins to Some(non-zero) and image None: same effect.
        // To force the shell fallback we set hicon = 0 and image_entry absent:
        // mock returns hicon = 0 from get_window_icon_handle (table-driven).
        let _ = cache.load_window_icon(&w);
    }
    // Only one shell extraction call regardless of window count.
    assert!(cache.exe_writes() == 1);
}

#[test]
fn process_path_cached_after_first_lookup() {
    let mock = MockApi::new();
    {
        let mut g = mock.state.lock().unwrap();
        g.process_paths.insert(42, Some("C:\\p\\app.exe".to_string()));
    }
    let fs = FsProbe { files: Mutex::new(HashMap::new()) };
    let mut cache = IconCache::new(mock, fs);
    let p1 = cache.get_process_path(42);
    let p2 = cache.get_process_path(42);
    assert_eq!(p1, Some("C:\\p\\app.exe".to_string()));
    assert_eq!(p2, p1, "subsequent lookup uses cache");
    assert!(cache.process_path_cached(42));
}

#[test]
fn trim_process_cache_drops_stale_pids_keeps_icon_caches() {
    let mock = MockApi::new();
    let exe = "C:\\windows\\app.exe";
    {
        let mut g = mock.state.lock().unwrap();
        g.process_paths.insert(1, Some(exe.to_string()));
        g.process_paths.insert(2, Some(exe.to_string()));
        g.shell_icons.insert(exe.to_string(), Some(opaque_img(5)));
        g.wins.insert(Hwnd(1), (1, "App".into(), 0, None));
        g.wins.insert(Hwnd(2), (2, "App".into(), 0, None));
    }
    let fs = FsProbe { files: Mutex::new(HashMap::new()) };
    let mut cache = IconCache::new(mock, fs);
    // Populate the process-path cache for both pids.
    let _ = cache.get_process_path(1);
    let _ = cache.get_process_path(2);
    let _ = cache.load_shell_icon(exe);
    assert_eq!(cache.exe_cache_len(), 1);
    let alive: HashSet<u32> = [1].iter().copied().collect();
    cache.trim_process_cache(&alive);
    assert!(cache.process_path_cached(1));
    assert!(!cache.process_path_cached(2));
    // Icon caches retained:
    assert_eq!(cache.exe_cache_len(), 1);
}

#[test]
fn uwp_manifest_path_chosen_over_shell() {
    let app_dir = std::path::Path::new("C:\\uwp\\app");
    let manifest = app_dir.join("AppxManifest.xml");
    let mut files = HashMap::new();
    files.insert(
        manifest.clone(),
        b"<Package><Applications><Application><VisualElements Square44x44Logo=\"Assets\\Logo.png\" Square150x150Logo=\"Assets\\LogoBig.png\"/></Application></Applications></Package>".to_vec(),
    );
    files.insert(
        app_dir.join("Assets/Logo.targetsize-256_altform-unplated.png"),
        b"PNG".to_vec(),
    );

    let exe = app_dir.join("app.exe").to_string_lossy().to_string();
    let mock = MockApi::new();
    {
        let mut g = mock.state.lock().unwrap();
        g.process_paths.insert(7, Some(exe.clone()));
        g.wins.insert(Hwnd(1), (7, "ApplicationFrameWindow".into(), 0, None));
        // UWP child probe: returns pid 7 as the real pid? The probe requires
        // child pid to differ from frame's. Use a separate pid 70.
        g.children.insert(
            Hwnd(1),
            vec![("Windows.UI.Core.CoreWindow".into(), Hwnd(2), 70)],
        );
        g.process_paths.insert(70, Some(exe.clone()));
        g.shell_icons.insert(exe.clone(), Some(opaque_img(99)));
    }
    // Share the file map with the FsProbe.
    let fs = FsProbe { files: Mutex::new(files) };
    let mut cache = IconCache::new(mock, fs);

    let w = app_window(Hwnd(1), 7, "ApplicationFrameWindow");
    let img = cache.load_window_icon(&w);
    // We went through manifest path → landed in exe cache keyed by app_dir, and
    // window_icon_cache keyed by hwnd.
    assert!(img.is_some());
    assert_eq!(cache.exe_writes(), 1, "manifest logo should populate exe cache once");
    assert!(cache.exe_icon(&app_dir.to_string_lossy()).is_some());
    assert!(cache.window_icon(Hwnd(1)).is_some());
}

#[test]
fn uwp_manifest_suffix_prefers_targetsize_unplated() {
    let app_dir = std::path::Path::new("C:\\uwp2");
    let manifest = app_dir.join("AppxManifest.xml");
    let mut files = HashMap::new();
    files.insert(
        manifest.clone(),
        b"<Package><VisualElements Square44x44Logo=\"Assets\\logo.png\"/></Package>".to_vec(),
    );
    // Only the targetsize-48 unplated variant exists.
    files.insert(
        app_dir.join("Assets/logo.targetsize-48_altform-unplated.png"),
        b"PNG".to_vec(),
    );
    let exe = app_dir.join("app.exe").to_string_lossy().to_string();
    let mock = MockApi::new();
    {
        let mut g = mock.state.lock().unwrap();
        g.process_paths.insert(70, Some(exe.clone()));
        g.wins.insert(Hwnd(1), (7, "ApplicationFrameWindow".into(), 0, None));
        g.children.insert(
            Hwnd(1),
            vec![("Windows.UI.Core.CoreWindow".into(), Hwnd(2), 70)],
        );
        g.process_paths.insert(7, Some(exe.clone()));
    }
    let fs = FsProbe { files: Mutex::new(files) };
    let mut cache = IconCache::new(mock, fs);
    let w = app_window(Hwnd(1), 7, "ApplicationFrameWindow");
    let img = cache.load_window_icon(&w);
    assert!(img.is_some());
}

#[test]
fn manifest_xml_visual_elements_picks_first_logo_attr() {
    let xml = r#"<Package><Applications><Application><uap:VisualElements Square44x44Logo="Assets\S44.png" Square150x150Logo="Assets\S150.png" Square71x71Logo="Assets\S71.png"/></Application></Applications></Package>"#;
    assert_eq!(pick_visual_elements_logo(xml), Some("Assets\\S44.png".to_string()));
}

#[test]
fn manifest_xml_attrs_attrs() {
    let s = r#" DisplayName="App" Square150x150Logo="Assets\S150.png" Description="x""#;
    assert_eq!(pick_attr(s, "Square150x150Logo"), Some("Assets\\S150.png".into()));
}