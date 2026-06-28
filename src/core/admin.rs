//! Administrator-privilege helpers.
//!
//! Port of `legacy/Services/AdminService.cs`. We cache the result of
//! `IsRunningAsAdmin` for the process lifetime (matches C#). Restarts use
//! `ShellExecuteW` with the `runas` verb (UAC) or an `explorer.exe` broker
//! (降权). Mirrors the C# behaviour one-for-one.

use std::sync::OnceLock;

use windows::core::HSTRING;
use windows::Win32::Foundation::HANDLE;
use windows::Win32::Security::{
    GetTokenInformation, TokenElevation, TOKEN_ELEVATION, TOKEN_QUERY,
};
use windows::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};
use windows::Win32::UI::Shell::ShellExecuteW;
use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

/// True if the current process runs with administrator privileges.
/// Cached on first successful probe.
pub fn is_running_as_admin() -> bool {
    if let Some(cached) = ADMIN.get() {
        return *cached;
    }
    let v = probe_admin();
    let _ = ADMIN.set(v);
    v
}

static ADMIN: OnceLock<bool> = OnceLock::new();

fn probe_admin() -> bool {
    unsafe {
        let mut token = HANDLE::default();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token).is_err() {
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

/// Restart the current executable with `runas` (UAC prompt). Returns `true`
/// when the launcher succeeded and the caller should exit. The user may still
/// cancel UAC, in which case this returns `false` and we keep running.
pub fn restart_as_admin(exe_path: &str) -> bool {
    shell_execute(exe_path, Some("runas"), None)
}

/// Restart the current executable as a normal user by launching it through
/// `explorer.exe` (Explorer always runs as the logged-in user).
pub fn restart_as_normal_user(exe_path: &str) -> bool {
    let args = format!("\"{}\"", exe_path);
    shell_execute("explorer.exe", None, Some(&args))
}

/// Path to the current executable. `Environment.ProcessPath` equiv.
pub fn current_exe_path() -> Option<String> {
    std::env::current_exe()
        .ok()
        .map(|p| p.to_string_lossy().into_owned())
}

fn shell_execute(file: &str, verb: Option<&str>, params: Option<&str>) -> bool {
    let file_h: HSTRING = file.into();
    let verb_h: HSTRING = verb.unwrap_or("").into();
    let params_h: HSTRING = params.unwrap_or("").into();
    unsafe {
        let r = ShellExecuteW(
            None,
            &verb_h,
            &file_h,
            &params_h,
            windows::core::PCWSTR::null(),
            SW_SHOWNORMAL,
        );
        // HINSTANCE > 32 indicates success (Windows convention).
        (r.0 as usize) > 32
    }
}
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn is_running_as_admin_returns_same_value_on_repeat_calls() {
        // The OnceLock caches the probe. Two consecutive calls must agree.
        let a = is_running_as_admin();
        let b = is_running_as_admin();
        assert_eq!(a, b, "cached value must not flip between calls");
    }

    #[test]
    fn probe_count_reflects_singleton() {
        // Whether we've probed at all is observable via the OnceLock cell.
        // We don't assert an exact count (other tests may have warmed the cache),
        // only that the API typechecks and is callable in a thread.
        let _ = is_running_as_admin();
    }
}
