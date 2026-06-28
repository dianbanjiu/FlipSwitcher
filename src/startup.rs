//! Secure startup sequence (Step 1, §1.4).
//!
//! 1. Named mutex single instance.
//! 2. Load settings.
//! 3. Admin correction — must match C# ordering to avoid UAC / 降权 deathloops.
//! 4. (Stubs) self-start correction, theme/font/language/tray/updates.
//! 5. Mutex released on exit.
//!
//! The admin-correction branches are pure logic over a [`StartupHost`] trait so
//! they can be unit-tested without spawning processes or acquiring a UAC prompt.

use std::sync::Mutex;

use crate::core::admin;
use crate::core::settings::SettingsService;

/// Outcome of [`startup`]: whether the process should exit after this point.
#[derive(Debug, PartialEq, Eq)]
pub enum StartupOutcome {
    /// No further action needed — proceed into the GUI loop (not wired in Step 1).
    ProceedToGui,
    /// A corrective restart was launched; this process should exit after releasing
    /// the single-instance mutex.
    Restarted { elevated: bool },
    /// Another instance is already running; exit immediately.
    AlreadyRunning,
    /// Non-admin requested via settings while we *are* admin, but the restart
    /// failed (e.g. explorer not available). Keep running as-is.
    UnfixableNonAdmin,
}

/// Abstraction over the process-level bits startup touches. The real
/// implementation talks to Mutex/ShellExecute/the OS; tests plug in a fake
/// that records what would happen.
pub trait StartupHost: Send + Sync {
    /// Try to acquire the named single-instance mutex. `Ok(false)` means another
    /// instance owns it.
    fn acquire_single_instance(&self) -> bool;
    /// Release the mutex to allow a succeeding restart to start a new owner.
    fn release_single_instance(&self);
    fn is_admin(&self) -> bool;
    /// Returns true if the elevation/restart was successfully launched.
    fn restart_as_admin(&self, exe_path: &str) -> bool;
    fn restart_as_normal(&self, exe_path: &str) -> bool;
    fn current_exe_path(&self) -> Option<String>;
}

/// Production host using `admin` + a Windows named Mutex. The mutex handle is
/// held in `OnceLock` so it survives until process exit.
pub struct PlatformStartupHost {
    mutex: Mutex<Option<windows::Win32::Foundation::HANDLE>>,
    exe: Option<String>,
}

unsafe impl Send for PlatformStartupHost {}
unsafe impl Sync for PlatformStartupHost {}

impl PlatformStartupHost {
    pub fn new() -> Self {
        Self {
            mutex: Mutex::new(None),
            exe: admin::current_exe_path(),
        }
    }
}

impl Default for PlatformStartupHost {
    fn default() -> Self {
        Self::new()
    }
}

const MUTEX_NAME: &str = "FlipSwitcher_SingleInstance_Mutex";

impl StartupHost for PlatformStartupHost {
    fn acquire_single_instance(&self) -> bool {
        use windows::core::HSTRING;
        use windows::Win32::Foundation::CloseHandle;
        use windows::Win32::System::Threading::CreateMutexW;
        let mut g = self.mutex.lock().unwrap();
        if g.is_some() {
            return true; // already acquired by us
        }
        unsafe {
            let created = CreateMutexW(None, true, &HSTRING::from(MUTEX_NAME));
            let handle = match created {
                Ok(h) => h,
                Err(_) => return false,
            };
            // ERROR_ALREADY_EXISTS (183) means another owner exists; check last error.
            let already_exists = std::io::Error::last_os_error().raw_os_error() == Some(183);
            if already_exists {
                let _ = CloseHandle(handle);
                return false;
            }
            *g = Some(handle);
            true
        }
    }

    fn release_single_instance(&self) {
        use windows::Win32::Foundation::CloseHandle;
        let mut g = self.mutex.lock().unwrap();
        if let Some(h) = g.take() {
            unsafe {
                let _ = CloseHandle(h);
            }
        }
    }

    fn is_admin(&self) -> bool {
        admin::is_running_as_admin()
    }

    fn restart_as_admin(&self, exe_path: &str) -> bool {
        admin::restart_as_admin(exe_path)
    }

    fn restart_as_normal(&self, exe_path: &str) -> bool {
        admin::restart_as_normal_user(exe_path)
    }

    fn current_exe_path(&self) -> Option<String> {
        self.exe.clone()
    }
}

/// Run the secure startup flow against `host`, the current settings snapshot
/// (`settings`), and a `persist` callback that startup calls with the corrected
/// settings whenever it downgrades `run_as_admin` (e.g. after a cancelled UAC
/// prompt). The persistor is the only side effect startup needs; everything
/// else lives behind `host`. This keeps the function pure enough to unit-test
/// without binding to the process-global `SettingsService`.
pub fn startup(
    host: &dyn StartupHost,
    settings: &crate::core::settings::AppSettings,
    mut persist: impl FnMut(&crate::core::settings::AppSettings),
) -> StartupOutcome {
    if !host.acquire_single_instance() {
        return StartupOutcome::AlreadyRunning;
    }

    let is_admin = host.is_admin();

    if settings.run_as_admin && !is_admin {
        if let Some(exe) = host.current_exe_path() {
            if host.restart_as_admin(&exe) {
                host.release_single_instance();
                return StartupOutcome::Restarted { elevated: true };
            }
        }
        // UAC cancelled or launch failed — record reality and continue as user.
        let mut patched = settings.clone();
        patched.run_as_admin = false;
        persist(&patched);
    } else if !settings.run_as_admin && is_admin {
        if let Some(exe) = host.current_exe_path() {
            if host.restart_as_normal(&exe) {
                host.release_single_instance();
                return StartupOutcome::Restarted { elevated: false };
            }
        }
        return StartupOutcome::UnfixableNonAdmin;
    }

    // (Step 4+) self-start correction, theme/font/language/tray/updates land here.
    StartupOutcome::ProceedToGui
}

/// Entry for `main()`: init settings, run startup, and exit on the
/// correction / already-running branches. Step 1 has no GUI yet, so
/// `ProceedToGui` is reached in tests only.
pub fn run() {
    SettingsService::init_default();
    let host = PlatformStartupHost::new();
    let svc = SettingsService::global();
    let outcome = startup(
        &host,
        &svc.settings(),
        |patched| svc.update_and_save(|cur| *cur = patched.clone()),
    );
    match outcome {
        StartupOutcome::ProceedToGui => {
            host.release_single_instance();
        }
        StartupOutcome::Restarted { .. } | StartupOutcome::AlreadyRunning => {}
        StartupOutcome::UnfixableNonAdmin => {
            host.release_single_instance();
        }
    }
}

// Silence unused import warnings in the no-GUI step.
#[allow(unused_imports)]
use crate::core::settings::FileSettingsStore as _;
#[cfg(test)]
mod tests {
    use super::*;
    use crate::core::settings::AppSettings;
    use std::sync::Mutex;

    /// Records every action the host took so the test can assert the branch.
    #[derive(Default)]
    struct FakeHost {
        acquire: bool,
        released: Mutex<bool>,
        is_admin: bool,
        admin_restarts: Mutex<u32>,
        normal_restarts: Mutex<u32>,
        exe: Option<String>,
    }

    impl StartupHost for FakeHost {
        fn acquire_single_instance(&self) -> bool {
            self.acquire
        }
        fn release_single_instance(&self) {
            *self.released.lock().unwrap() = true;
        }
        fn is_admin(&self) -> bool {
            self.is_admin
        }
        fn restart_as_admin(&self, _exe: &str) -> bool {
            *self.admin_restarts.lock().unwrap() += 1;
            true
        }
        fn restart_as_normal(&self, _exe: &str) -> bool {
            *self.normal_restarts.lock().unwrap() += 1;
            true
        }
        fn current_exe_path(&self) -> Option<String> {
            self.exe.clone()
        }
    }

    fn settings(admin: bool) -> AppSettings {
        let mut s = AppSettings::default();
        s.run_as_admin = admin;
        s
    }

    #[test]
    fn already_running_when_mutex_busy() {
        let host = FakeHost {
            acquire: false,
            ..Default::default()
        };
        let outcome = startup(&host, &settings(false), |_| {});
        assert_eq!(outcome, StartupOutcome::AlreadyRunning);
    }

    #[test]
    fn proceeds_when_admin_matches_setting() {
        // run_as_admin=false, is_admin=false → no correction, proceed.
        let host = FakeHost {
            acquire: true,
            is_admin: false,
            exe: Some("x".into()),
            ..Default::default()
        };
        let outcome = startup(&host, &settings(false), |_| {});
        assert_eq!(outcome, StartupOutcome::ProceedToGui);
        assert!(!*host.released.lock().unwrap());
    }

    #[test]
    fn elevates_when_setting_requires_admin() {
        let host = FakeHost {
            acquire: true,
            is_admin: false,
            exe: Some("x".into()),
            ..Default::default()
        };
        let outcome = startup(&host, &settings(true), |_| {});
        assert_eq!(outcome, StartupOutcome::Restarted { elevated: true });
        assert_eq!(*host.admin_restarts.lock().unwrap(), 1);
        // Mutex released because we handed ownership to the new elevated process.
        assert!(*host.released.lock().unwrap());
    }

    #[test]
    fn downgrade_setting_when_uac_cancelled() {
        // run_as_admin=true but is_admin=false AND restart_as_admin returns false
        // (e.g. UAC cancelled) — we record reality and continue.
        let host = FakeHost {
            acquire: true,
            is_admin: false,
            exe: Some("x".into()),
            ..Default::default()
        };
        // Override restart_as_admin to return failure.
        struct CancellingHost(FakeHost);
        impl StartupHost for CancellingHost {
            fn acquire_single_instance(&self) -> bool {
                self.0.acquire_single_instance()
            }
            fn release_single_instance(&self) {
                self.0.release_single_instance()
            }
            fn is_admin(&self) -> bool {
                self.0.is_admin()
            }
            fn restart_as_admin(&self, _exe: &str) -> bool {
                false
            }
            fn restart_as_normal(&self, _exe: &str) -> bool {
                self.0.restart_as_normal(_exe)
            }
            fn current_exe_path(&self) -> Option<String> {
                self.0.current_exe_path()
            }
        }
        let h = CancellingHost(host);
        let mut persisted: Option<AppSettings> = None;
        let outcome = startup(&h, &settings(true), |p| persisted = Some(p.clone()));
        assert_eq!(outcome, StartupOutcome::ProceedToGui);
        let p = persisted.expect("upgraded setting should have been persisted");
        assert!(!p.run_as_admin, "UAC-cancelled path must downgrade run_as_admin");
    }

    #[test]
    fn downgrades_when_setting_says_normal_but_is_admin() {
        let host = FakeHost {
            acquire: true,
            is_admin: true,
            exe: Some("x".into()),
            ..Default::default()
        };
        let outcome = startup(&host, &settings(false), |_| {});
        assert_eq!(outcome, StartupOutcome::Restarted { elevated: false });
        assert_eq!(*host.normal_restarts.lock().unwrap(), 1);
        assert!(*host.released.lock().unwrap());
    }

    #[test]
    fn no_death_loop_when_exe_unknown() {
        // current_exe_path returns None → can't restart; must NOT loop.
        let host = FakeHost {
            acquire: true,
            is_admin: false,
            exe: None, // no path → can't elevate
            ..Default::default()
        };
        let outcome = startup(&host, &settings(true), |_| {});
        // With no exe, the admin branch can neither restart nor fall back to
        // ProceedToGui without persisting — but `settings.run_as_admin` should
        // still be downgraded to reality as in the UAC-cancelled path.
        assert_eq!(outcome, StartupOutcome::ProceedToGui);
        // We don't assert persisted here: the downgrading side-effect happens
        // either via the persistor or a subsequent save; the key invariant is
        // *no restart was attempted and the function returned*.
    }

    #[test]
    fn unfixable_when_normal_requested_but_restart_fails() {
        // is_admin=true, setting=normal but restart_as_normal returns false.
        let host = FakeHost {
            acquire: true,
            is_admin: true,
            exe: Some("x".into()),
            ..Default::default()
        };
        struct FailNormal(FakeHost);
        impl StartupHost for FailNormal {
            fn acquire_single_instance(&self) -> bool {
                self.0.acquire_single_instance()
            }
            fn release_single_instance(&self) {
                self.0.release_single_instance()
            }
            fn is_admin(&self) -> bool {
                self.0.is_admin()
            }
            fn restart_as_admin(&self, e: &str) -> bool {
                self.0.restart_as_admin(e)
            }
            fn restart_as_normal(&self, _e: &str) -> bool {
                false
            }
            fn current_exe_path(&self) -> Option<String> {
                self.0.current_exe_path()
            }
        }
        let h = FailNormal(host);
        let outcome = startup(&h, &settings(false), |_| {});
        assert_eq!(outcome, StartupOutcome::UnfixableNonAdmin);
    }
}
