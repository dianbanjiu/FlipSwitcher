//! Window/process icon cache. Step 3.
//!
//! Port of `legacy/Services/IconCacheService.cs`. Four caches:
//! - `exe_icon_cache` (exePath → Image) — exe-wide, safe to share across
//!   windows of the same process.
//! - `window_icon_cache` ("hwnd:<id>" → Image) — per-window, may differ across
//!   windows of the same exe.
//! - `appx_logo_cache` (appDir → Option<logoPath>) — avoid re-parsing
//!   AppxManifest.xml + 11x File.Exists probes.
//! - `process_path_cache` (pid → Option<exePath>) — cached QueryFullProcessImageName.
//!
//! **Core invariant** (test-guarded): an icon obtained via `WM_GETICON` /
//! `GetClassLongPtr` is *per-window* and MUST NOT be written into
//! `exe_icon_cache`. Otherwise explorer.exe running "File Explorer" and
//! "Control Panel" simultaneously would cross-contaminate each other's icons.
//!
//! Use with the [`Win32Api`] trait so tests can drive a mock. Not thread-safe
//! — `IconCache` carries `&mut self` and is borrowed serially behind the
//! enumeration refresh gate.

use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};

use crate::core::app_window::AppWindow;
use crate::core::win32::{Hwnd, IconImage, Win32Api};

/// Child-window classes probed to find the real UWP process behind an
/// `ApplicationFrameWindow`. The first one whose pid differs from the frame's
/// is the UWP process.
const UWP_CHILD_CLASSES: &[&str] = &[
    "Windows.UI.Core.CoreWindow",
    "Windows.UI.Composition.DesktopWindowContentBridge",
];

/// AppxManifest logo suffix probe order — larger sizes preferred, unplated
/// variants preferred.
const APPX_LOGO_SUFFIXES: &[&str] = &[
    ".targetsize-256_altform-unplated",
    ".targetsize-256",
    ".targetsize-64_altform-unplated",
    ".targetsize-64",
    ".targetsize-48_altform-unplated",
    ".targetsize-48",
    ".targetsize-32_altform-unplated",
    ".targetsize-32",
    ".scale-200",
    ".scale-100",
    "",
];

/// Trait seam for filesystem mocks. Production impl touches the FS via `std::fs`.
pub trait IconFs: Send + Sync {
    fn exists(&self, path: &Path) -> bool;
    fn read_to_string(&self, path: &Path) -> Option<String>;
}

/// Real filesystem.
pub struct StdIconFs;
impl IconFs for StdIconFs {
    fn exists(&self, path: &Path) -> bool {
        path.exists()
    }
    fn read_to_string(&self, path: &Path) -> Option<String> {
        std::fs::read_to_string(path).ok()
    }
}

pub struct IconCache<A: Win32Api, Fs: IconFs> {
    api: A,
    fs: Fs,
    exe_icon_cache: HashMap<String, IconImage>,      // exe-wide
    window_icon_cache: HashMap<String, IconImage>,   // "hwnd:<id>" → per-window
    appx_logo_cache: HashMap<String, Option<PathBuf>>, // appDir → resolved logo
    process_path_cache: HashMap<u32, Option<String>>,  // pid → exePath
    /// Test only: counts of writes into the exe-icon cache. Used to assert the
    /// invariant (no per-window keys flow into it).
    #[cfg(test)]
    exe_writes: u32,
}

impl<A: Win32Api, Fs: IconFs> IconCache<A, Fs> {
    pub fn new(api: A, fs: Fs) -> Self {
        Self {
            api,
            fs,
            exe_icon_cache: HashMap::new(),
            window_icon_cache: HashMap::new(),
            appx_logo_cache: HashMap::new(),
            process_path_cache: HashMap::new(),
            #[cfg(test)]
            exe_writes: 0,
        }
    }

    pub fn api(&self) -> &A {
        &self.api
    }

    /// Resolve and cache the executable path for a process id.
    pub fn get_process_path(&mut self, pid: u32) -> Option<String> {
        if let Some(cached) = self.process_path_cache.get(&pid) {
            return cached.clone();
        }
        let p = self.api.process_path_for(pid);
        self.process_path_cache.insert(pid, p.clone());
        p
    }

    /// Shell-extract the icon for an exe path; cache against the path. Safe to
    /// share across windows of the same exe — which is exactly why this is the
    /// *only* call that writes the exe-icon cache.
    pub fn load_shell_icon(&mut self, exe_path: &str) -> Option<IconImage> {
        if let Some(cached) = self.exe_icon_cache.get(exe_path) {
            return Some(cached.clone());
        }
        let icon = self.api.shell_extract_icon(exe_path)?;
        #[cfg(test)]
        {
            self.exe_writes += 1;
        }
        self.exe_icon_cache.insert(exe_path.to_string(), icon.clone());
        Some(icon)
    }

    /// Extract the exe-wide icon as a last resort; cache by exe path.
    pub fn load_extracted_icon(&mut self, exe_path: &str) -> Option<IconImage> {
        if let Some(cached) = self.exe_icon_cache.get(exe_path) {
            return Some(cached.clone());
        }
        let icon = self.api.extract_associated_icon(exe_path)?;
        #[cfg(test)]
        {
            self.exe_writes += 1;
        }
        self.exe_icon_cache.insert(exe_path.to_string(), icon.clone());
        Some(icon)
    }

    /// Trim `process_path_cache` to only pids still alive. **Icon caches are
    /// intentionally retained** — same exe, same icon (matches C# TrimProcessCache).
    pub fn trim_process_cache(&mut self, alive: &HashSet<u32>) {
        let stale: Vec<u32> = self
            .process_path_cache
            .keys()
            .filter(|p| !alive.contains(p))
            .copied()
            .collect();
        for p in stale {
            self.process_path_cache.remove(&p);
        }
    }

    /// Load the icon for a window. The cache key for per-window icons is
    /// `"hwnd:<id>"`. Per-window icons never flow into the exe-icon cache.
    pub fn load_window_icon(&mut self, w: &AppWindow) -> Option<IconImage> {
        let key = window_icon_key(w.handle);
        if let Some(cached) = self.window_icon_cache.get(&key) {
            return Some(cached.clone());
        }

        // UWP path: prefer AppxManifest logo.
        if w.class_name == "ApplicationFrameWindow" {
            if let Some(img) = self.load_uwp_icon(w) {
                self.window_icon_cache.insert(key, img.clone());
                return Some(img);
            }
        }

        // Per-window icon via WM_GETICON / GetClassLongPtr (borrowed handle).
        if let Some(h) = self.api.get_window_icon_handle(w.handle) {
            if let Some(img) = self.api.window_icon_to_image(h) {
                // Per-window: cache under the hwnd key. DO NOT touch
                // exe_icon_cache here — that is the invariant.
                self.window_icon_cache.insert(key, img.clone());
                return Some(img);
            }
        }

        // Exe-wide fallback (shared; safe to cache by exe path).
        if let Some(exe_path) = self.get_process_path(w.process_id) {
            if let Some(img) = self.load_shell_icon(&exe_path) {
                self.window_icon_cache.insert(key, img.clone());
                return Some(img);
            }
            if let Some(img) = self.load_extracted_icon(&exe_path) {
                self.window_icon_cache.insert(key, img.clone());
                return Some(img);
            }
        }
        None
    }

    fn load_uwp_icon(&mut self, w: &AppWindow) -> Option<IconImage> {
        // Find the real UWP pid via child-window probe.
        let mut uwp_pid = w.process_id;
        for cls in UWP_CHILD_CLASSES {
            if let Some((_, child_pid)) =
                self.api
                    .find_child_window_class_pid(w.handle, cls, w.process_id)
            {
                uwp_pid = child_pid;
                break;
            }
        }
        let exe_path = self.get_process_path(uwp_pid)?;
        let app_dir = Path::new(&exe_path).parent()?.to_path_buf();
        let app_dir_s = app_dir.to_string_lossy().to_string();

        // exe-icon cache keyed by app dir for UWP (matches C# SetExeIcon(appDir, …)).
        if let Some(cached) = self.exe_icon_cache.get(&app_dir_s) {
            return Some(cached.clone());
        }

        // Resolve manifest logo path (cached per app dir).
        if let Some(logo) = self.resolve_appx_logo_path(&app_dir) {
            if let Some(img) = self.load_icon_from_image_file(&logo) {
                #[cfg(test)]
                {
                    self.exe_writes += 1;
                }
                self.exe_icon_cache.insert(app_dir_s.clone(), img.clone());
                return Some(img);
            }
        }

        // Fallback: shell icon by exe path.
        let img = self.load_shell_icon(&exe_path)?;
        Some(img)
    }

    /// Resolve the best AppxManifest logo path for a UWP app directory, cached.
    fn resolve_appx_logo_path(&mut self, app_dir: &Path) -> Option<PathBuf> {
        let app_dir_s = app_dir.to_string_lossy().to_string();
        if let Some(c) = self.appx_logo_cache.get(&app_dir_s) {
            return c.clone();
        }
        let manifest = app_dir.join("AppxManifest.xml");
        if !self.fs.exists(&manifest) {
            self.appx_logo_cache.insert(app_dir_s.clone(), None);
            return None;
        }
        let xml = match self.fs.read_to_string(&manifest) {
            Some(s) => s,
            None => {
                self.appx_logo_cache.insert(app_dir_s.clone(), None);
                return None;
            }
        };

        let base_rel = pick_visual_elements_logo(&xml);
        let resolved = base_rel.and_then(|rel| {
            let base = app_dir.join(&rel);
            let dir = base.parent()?;
            let stem = base.file_stem()?.to_string_lossy().to_string();
            let ext = base.extension().map(|e| e.to_string_lossy().to_string());

            for sfx in APPX_LOGO_SUFFIXES {
                let mut cand = stem.clone();
                cand.push_str(sfx);
                if let Some(ext) = &ext {
                    cand.push('.');
                    cand.push_str(ext);
                }
                let candidate = dir.join(&cand);
                if self.fs.exists(&candidate) {
                    return Some(candidate);
                }
            }
            Some(base) // fall back to the base logo
        });
        self.appx_logo_cache.insert(app_dir_s.clone(), resolved.clone());
        resolved
    }

    /// Load an arbitrary image file as an `IconImage`. Currently only exercised
    /// by UWP logo resolution; the real implementation decodes via Slint's image
    /// loader later — for Step 3 we treat a PNG that exists as a placeholder
    /// `IconImage` (one opaque black pixel) so the cache machinery is testable.
    fn load_icon_from_image_file(&self, path: &Path) -> Option<IconImage> {
        if !self.fs.exists(path) {
            return None;
        }
        Some(IconImage {
            width: 1,
            height: 1,
            pixels: vec![0, 0, 0, 255],
        })
    }

    /// Test seam: how many writes hit `exe_icon_cache`.
    #[cfg(test)]
    pub fn exe_writes(&self) -> u32 {
        self.exe_writes
    }

    /// Test seam: number of cached per-window entries.
    #[cfg(test)]
    pub fn window_cache_len(&self) -> usize {
        self.window_icon_cache.len()
    }

    /// Test seam: number of cached exe entries.
    #[cfg(test)]
    pub fn exe_cache_len(&self) -> usize {
        self.exe_icon_cache.len()
    }

    /// Test seam: lookup a per-window icon we cached.
    #[cfg(test)]
    pub fn window_icon(&self, hwnd: Hwnd) -> Option<&IconImage> {
        self.window_icon_cache.get(&window_icon_key(hwnd))
    }

    /// Test seam: lookup an exe-icon entry.
    #[cfg(test)]
    pub fn exe_icon(&self, key: &str) -> Option<&IconImage> {
        self.exe_icon_cache.get(key)
    }

    /// Test seam: did process-path get queried for `pid`?
    #[cfg(test)]
    pub fn process_path_cached(&self, pid: u32) -> bool {
        self.process_path_cache.contains_key(&pid)
    }
}

fn window_icon_key(hwnd: Hwnd) -> String {
    format!("hwnd:{}", hwnd.raw())
}

/// Cheap XML scan for the first non-empty VisualElements logo attribute.
/// Mirrors the C# `XDocument` walk over `<VisualElements Square44x44Logo=…>`.
/// Namespaced forms (`<uap:VisualElements>`) are tolerated by searching for the
/// `VisualElements` token, not a literal open-tag.
fn pick_visual_elements_logo(xml: &str) -> Option<String> {
    // Find `<…VisualElements …>` opening tag (namespace-prefixed tolerated).
    let ve_start = xml.find("VisualElements")?;
    // Walk back to the `<` that begins this tag.
    let lt = xml[..ve_start].rfind('<')?;
    let tag_open = xml[lt..].find('>')?;
    let ve_attrs = &xml[lt..lt + tag_open];
    for name in [
        "Square44x44Logo",
        "Square150x150Logo",
        "Square71x71Logo",
    ] {
        if let Some(v) = pick_attr(ve_attrs, name) {
            if !v.is_empty() {
                return Some(v);
            }
        }
    }
    // Fall back to <…Logo> inside <Properties>…</Properties>.
    let props_start = xml.find("<Properties")?;
    let props_end_rel = xml[props_start..].find("</Properties>")?;
    let block = &xml[props_start..props_start + props_end_rel];
    block
        .find("<Logo")
        .and_then(|s| {
            let rest = &block[s..];
            let tag_end = rest.find('>')?;
            let attrs = &rest[..tag_end];
            pick_attr(attrs, "Logo")
        })
        .filter(|s| !s.is_empty())
}

/// Extract `"Name"` attribute value from a tag-attribute slice.
fn pick_attr(attrs: &str, name: &str) -> Option<String> {
    let needle = format!("{}=\"", name);
    let s = attrs.find(&needle)? + needle.len();
    let rest = &attrs[s..];
    let end = rest.find('"')?;
    Some(rest[..end].to_string())
}
#[cfg(test)]
#[path = "icon_loader_tests.rs"]
mod tests;
