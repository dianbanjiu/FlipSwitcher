//! Low-level keyboard hook + hotkey state machine (Step 4).
//!
//! 1:1 port of `legacy/Services/HotkeyService.cs`. The hook callback runs on
//! the system keyboard-hook thread for *every keystroke system-wide*, so it
//! stays cheap: read `vkCode` (first 4 bytes), consult a few `AtomicBool`s,
//! and push an event onto a channel. It never touches UI state directly — the
//! UI thread (Step 6+) drains the channel, mirroring `InvokeOnDispatcher`.
//!
//! Two layers:
//! - [`decode_hook_event`] — a pure function over `(keydown, keyup, vk, alt,
//!   shift, state)` that decides which [`HotkeyEvent`] (if any) to dispatch and
//!   whether to swallow the key. Fully unit-tested; no Win32.
//! - [`HotkeyHook`] — the FFI: `WH_KEYBOARD_LL` install/uninstall, a hidden
//!   message-only window that receives `WM_HOTKEY` for the Alt+Space path
//!   (with Ctrl+Space fallback), and the channel plumbing. Wired in Step 6+;
//!   kept `#[allow(dead_code)]` until then.
//!
//! See `docs/rust-rewrite-design.md` §3.5 and §4.1, and
//! `docs/rust-rewrite-design-step4-5.md` §4.

use std::sync::atomic::Ordering;
use std::sync::{Arc, OnceLock, mpsc::Sender};

use crate::core::win32::{
    HotkeyState, VK_LMENU, VK_MENU, VK_RMENU, VK_SHIFT,
};
#[allow(unused_imports)]
use crate::core::win32::Win32Api;

/// Navigation direction for Tab/arrow navigation while Alt is held.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NavDirection {
    Next,
    Previous,
}

/// One decoded hotkey event, 1:1 with the `HotkeyService` C# events.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HotkeyEvent {
    /// Alt+Tab pressed while the switcher is hidden → show it.
    HotkeyPressed,
    /// Alt released while visible (and not ignored) → confirm selection.
    AltReleased,
    /// Tab (visible) or ↑/↓ navigation.
    NavigationRequested(NavDirection),
    /// Alt+W.
    CloseWindowRequested,
    /// Alt+D.
    StopProcessRequested,
    /// Alt+S.
    SearchModeRequested,
    /// Escape (visible, or settings open + Alt held).
    EscapePressed,
    /// Alt+,
    SettingsRequested,
    /// →
    GroupByProcessRequested,
    /// ←
    UngroupFromProcessRequested,
}

/// Virtual key codes the state machine cares about. Mirrors `NativeMethods.VK_*`.
pub mod vk {
    pub const ESCAPE: u32 = 0x1B;
    pub const TAB: u32 = 0x09;
    pub const UP: u32 = 0x26;
    pub const DOWN: u32 = 0x28;
    pub const LEFT: u32 = 0x25;
    pub const RIGHT: u32 = 0x27;
    pub const W: u32 = 0x57;
    pub const D: u32 = 0x44;
    pub const S: u32 = 0x53;
    pub const OEM_COMMA: u32 = 0xBC;
}

/// Decode one keystroke into `(event, swallow)`.
///
/// `swallow == true` means the hook returns 1 (eat the key); `false` and
/// `None` both mean "call `CallNextHookEx` and let it pass through". The
/// decision order mirrors `HotkeyService.KeyboardHookCallback` exactly —
/// **order is a contract**, do not reorder:
///
/// 1. `use_alt_tab` off → pass through (the caller also passes through when
///    `nCode < 0`; this guards the in-hook re-check).
/// 2. Escape keydown, when settings-open+Alt or visible → `EscapePressed`.
/// 3. Keyup → `AltReleased` when visible and not ignored (and **not** swallowed
///    — Alt release passes through, matching the C#).
/// 4. Below here is keydown-only; require Alt held.
/// 5. Tab → `HotkeyPressed` (hidden) or `NavigationRequested` (Shift = Prev).
/// 6. ↑/↓ (visible, not search-mode) → navigation.
/// 7. ←/→ (visible) → group/ungroup.
/// 8. W/D/S/OEM_COMMA (visible) → close/stop/search/settings.
pub fn decode_hook_event(
    is_keydown: bool,
    is_keyup: bool,
    vk: u32,
    is_alt: bool,
    is_shift: bool,
    state: &HotkeyState,
) -> Option<(HotkeyEvent, bool)> {
    use vk::*;

    if !state.use_alt_tab.load(Ordering::Relaxed) {
        return None;
    }

    // Neither a keydown nor a keyup (e.g. some other message) → pass through.
    if !is_keydown && !is_keyup {
        return None;
    }

    // Escape — handled on keydown only, ahead of the Alt-release check.
    if is_keydown && vk == ESCAPE {
        let settings_alt = state.is_settings_open.load(Ordering::Relaxed) && is_alt;
        let visible = state.is_visible.load(Ordering::Relaxed);
        let search_mode = state.is_search_mode.load(Ordering::Relaxed);
        if settings_alt || visible {
            // In search mode the LineEdit holds keyboard focus; let the key
            // through (don't swallow) so the .slint search box can also handle
            // Escape via its `key-pressed` → `escape` callback. Otherwise (list
            // mode) swallow it — the list has no key handler of its own. The
            // channel always gets `EscapePressed` so the bridge hides either way.
            return Some((HotkeyEvent::EscapePressed, !search_mode));
        }
        return None;
    }

    // Alt release confirmation. Not swallowed — Alt keyup passes through.
    if is_keyup {
        let visible = state.is_visible.load(Ordering::Relaxed);
        let ignore = state.ignore_alt_release.load(Ordering::Relaxed);
        if visible && !ignore && (vk == VK_MENU || vk == VK_LMENU || vk == VK_RMENU) {
            return Some((HotkeyEvent::AltReleased, false));
        }
        return None;
    }

    // Below: keydown only, and only when Alt is held.
    if !is_alt {
        return None;
    }

    let visible = state.is_visible.load(Ordering::Relaxed);
    let search_mode = state.is_search_mode.load(Ordering::Relaxed);

    if vk == TAB {
        if !visible {
            return Some((HotkeyEvent::HotkeyPressed, true));
        }
        let dir = if is_shift {
            NavDirection::Previous
        } else {
            NavDirection::Next
        };
        return Some((HotkeyEvent::NavigationRequested(dir), true));
    }

    // ↑/↓ only when visible and not in search mode.
    if visible && !search_mode {
        if vk == UP {
            return Some((HotkeyEvent::NavigationRequested(NavDirection::Previous), true));
        }
        if vk == DOWN {
            return Some((HotkeyEvent::NavigationRequested(NavDirection::Next), true));
        }
    }

    // ←/→ — always available while visible (requires Alt in search mode, which
    // we already enforce via the `is_alt` guard above).
    if visible {
        if vk == RIGHT {
            return Some((HotkeyEvent::GroupByProcessRequested, true));
        }
        if vk == LEFT {
            return Some((HotkeyEvent::UngroupFromProcessRequested, true));
        }
        match vk {
            W => return Some((HotkeyEvent::CloseWindowRequested, true)),
            D => return Some((HotkeyEvent::StopProcessRequested, true)),
            S => return Some((HotkeyEvent::SearchModeRequested, true)),
            OEM_COMMA => return Some((HotkeyEvent::SettingsRequested, true)),
            _ => {}
        }
    }

    None
}

// ============================================================================
// FFI layer — wired in Step 6+. Kept dead-code-allowed until then.
// ============================================================================

#[allow(dead_code)]
struct HookCtx {
    state: Arc<HotkeyState>,
    sender: Sender<HotkeyEvent>,
    /// Once the hook is installed, this holds the `HHOOK` so the trampoline can
    /// pass it to `CallNextHookEx`. Stored as a raw `isize` (the `HHOOK` pointer)
    /// because `HHOOK` is not `Send` — but the value is just a pointer token the
    /// OS gave us, and only the hook thread reads it.
    hhk: AtomicPtr,
}

#[allow(dead_code)]
type AtomicPtr = std::sync::atomic::AtomicIsize;

/// Global slot for the hook context. `SetWindowsHookExW`'s callback signature
/// carries no user data, so we look the context up here (single instance, like
/// the C# `HotkeyService`).
#[allow(dead_code)]
static HOOK_CTX: OnceLock<Arc<HookCtx>> = OnceLock::new();

/// Window-class name for the hidden message window that receives `WM_HOTKEY`.
#[allow(dead_code)]
const HOTKEY_WND_CLASS: &str = "FlipSwitcherHotkeySink";

/// Hotkey id for Alt+Space / Ctrl+Space. Matches the C# `HOTKEY_ID_ALT_SPACE`.
#[allow(dead_code)]
const HOTKEY_ID_ALT_SPACE: i32 = 9000;

/// Private window message posted to the pump thread to ask it to exit.
#[allow(dead_code)]
const WM_QUIT_THREAD: u32 = 0x8000 + 1;

#[allow(dead_code)]
pub struct HotkeyHook {
    ctx: Arc<HookCtx>,
    /// `JoinHandle` for the message-pump thread that owns the hidden window.
    pump: Option<std::thread::JoinHandle<()>>,
    /// The thread id of the pump thread — used to post the quit message and to
    /// register/unregister the `RegisterHotKey` target (the hidden window,
    /// which lives on that thread).
    pump_tid: u32,
    /// What we actually registered, for `current_hotkey_label`.
    label: &'static str,
}

#[allow(dead_code)]
impl HotkeyHook {
    /// Install the `WH_KEYBOARD_LL` hook and, when `use_alt_space` is set,
    /// register the Alt+Space global hotkey (falling back to Ctrl+Space).
    /// The returned hook owns the hook handle and uninstalls on drop.
    pub fn install(
        state: Arc<HotkeyState>,
        sender: Sender<HotkeyEvent>,
        use_alt_space: bool,
    ) -> Result<Self, String> {
        // Spin up the pump thread first — it creates the hidden window and
        // registers the hotkey on its own thread (RegisterHotKey delivers
        // WM_HOTKEY to the thread that registered it).
        let ctx = Arc::new(HookCtx {
            state: state.clone(),
            sender,
            hhk: AtomicPtr::new(0),
        });
        // Install the global ctx before starting the pump / installing the hook
        // so the trampoline can always find it.
        let _ = HOOK_CTX.set(ctx.clone());

        let (tx_ready, rx_ready) = std::sync::mpsc::channel::<(u32, &'static str)>();
        let ctx_for_pump = ctx.clone();
        let pump = std::thread::Builder::new()
            .name("flipswitcher-hotkey-pump".into())
            .spawn(move || pump_thread(ctx_for_pump, use_alt_space, tx_ready))
            .map_err(|e| e.to_string())?;

        let (pump_tid, label) = rx_ready.recv().map_err(|_| "pump thread failed to start")?;

        // Install the low-level keyboard hook from this (UI) thread —
        // WH_KEYBOARD_LL callbacks arrive on the system hook thread regardless.
        let hhk = unsafe {
            windows::Win32::UI::WindowsAndMessaging::SetWindowsHookExW(
                windows::Win32::UI::WindowsAndMessaging::WINDOWS_HOOK_ID(
                    crate::core::win32::WH_KEYBOARD_LL,
                ),
                Some(low_level_proc),
                current_module_handle(),
                0,
            )
        };
        match hhk {
            Ok(h) => ctx.hhk.store(h.0 as isize, Ordering::SeqCst),
            Err(e) => return Err(format!("SetWindowsHookExW failed: {e}")),
        }
        Ok(Self {
            ctx,
            pump: Some(pump),
            pump_tid,
            label,
        })
    }

    pub fn current_hotkey_label(&self) -> &'static str {
        self.label
    }

    pub fn uninstall(&mut self) {
        let h = self.ctx.hhk.swap(0, Ordering::SeqCst);
        if h != 0 {
            unsafe {
                let _ = windows::Win32::UI::WindowsAndMessaging::UnhookWindowsHookEx(
                    windows::Win32::UI::WindowsAndMessaging::HHOOK(h as *mut _),
                );
            }
        }
        // Tell the pump thread to exit, then join.
        unsafe {
            let _ = windows::Win32::UI::WindowsAndMessaging::PostThreadMessageW(
                self.pump_tid,
                WM_QUIT_THREAD,
                windows::Win32::Foundation::WPARAM(0),
                windows::Win32::Foundation::LPARAM(0),
            );
        }
        if let Some(p) = self.pump.take() {
            let _ = p.join();
        }
    }
}

#[allow(dead_code)]
impl Drop for HotkeyHook {
    fn drop(&mut self) {
        self.uninstall();
    }
}

/// The low-level keyboard hook trampoline. Looks up the global context,
/// decodes the keystroke, and either swallows (returns 1) or passes through.
/// Panics are caught so they never cross the FFI boundary.
#[allow(dead_code)]
unsafe extern "system" fn low_level_proc(
    n_code: i32,
    w_param: windows::Win32::Foundation::WPARAM,
    l_param: windows::Win32::Foundation::LPARAM,
) -> windows::Win32::Foundation::LRESULT {
    use std::panic::{catch_unwind, AssertUnwindSafe};

    if n_code < 0 {
        return call_next(0, n_code, w_param, l_param);
    }
    let Some(ctx) = HOOK_CTX.get() else {
        return call_next(0, n_code, w_param, l_param);
    };
    if !ctx.state.use_alt_tab.load(Ordering::Relaxed) {
        return call_next(ctx.hhk.load(Ordering::Relaxed), n_code, w_param, l_param);
    }

    let res = catch_unwind(AssertUnwindSafe(|| {
        let msg = w_param.0 as u32;
        let is_keydown = msg == crate::core::win32::WM_KEYDOWN
            || msg == crate::core::win32::WM_SYSKEYDOWN;
        let is_keyup = msg == crate::core::win32::WM_KEYUP
            || msg == crate::core::win32::WM_SYSKEYUP;
        if !is_keydown && !is_keyup {
            return None;
        }
        // KBDLLHOOKSTRUCT.vkCode is the first 4 bytes.
        let kb = l_param.0 as *const windows::Win32::UI::WindowsAndMessaging::KBDLLHOOKSTRUCT;
        if kb.is_null() {
            return None;
        }
        let vk = unsafe { (*kb).vkCode };
        let api = crate::core::win32::WindowsApi;
        let is_alt = api.is_key_down_async(VK_MENU)
            || api.is_key_down_async(crate::core::win32::VK_LMENU)
            || api.is_key_down_async(crate::core::win32::VK_RMENU);
        let is_shift = api.is_key_down_async(VK_SHIFT);
        decode_hook_event(is_keydown, is_keyup, vk, is_alt, is_shift, &ctx.state)
    }));

    match res {
        Ok(Some((ev, swallow))) => {
            let _ = ctx.sender.send(ev);
            if swallow {
                windows::Win32::Foundation::LRESULT(1)
            } else {
                call_next(ctx.hhk.load(Ordering::Relaxed), n_code, w_param, l_param)
            }
        }
        _ => call_next(ctx.hhk.load(Ordering::Relaxed), n_code, w_param, l_param),
    }
}

#[allow(dead_code)]
unsafe fn call_next(
    hhk: isize,
    n_code: i32,
    w_param: windows::Win32::Foundation::WPARAM,
    l_param: windows::Win32::Foundation::LPARAM,
) -> windows::Win32::Foundation::LRESULT {
    let h = if hhk == 0 {
        None
    } else {
        Some(windows::Win32::UI::WindowsAndMessaging::HHOOK(hhk as *mut _))
    };
    windows::Win32::UI::WindowsAndMessaging::CallNextHookEx(h, n_code, w_param, l_param)
}

/// Message-pump thread: creates the hidden window, registers the Alt+Space
/// (or Ctrl+Space fallback) hotkey, pumps messages until asked to quit.
#[allow(dead_code)]
fn pump_thread(
    ctx: Arc<HookCtx>,
    use_alt_space: bool,
    ready: Sender<(u32, &'static str)>,
) {
    use windows::Win32::UI::WindowsAndMessaging::{
        CreateWindowExW, DispatchMessageW, GetMessageW, RegisterClassExW, TranslateMessage,
        WINDOW_EX_STYLE, WINDOW_STYLE, WNDCLASSEXW, MSG,
    };
    use windows::core::w;

    let tid = unsafe { windows::Win32::System::Threading::GetCurrentThreadId() };

    // Register a window class whose WndProc routes WM_HOTKEY to the channel.
    let class_name = w!("FlipSwitcherHotkeySink");
    let wc = WNDCLASSEXW {
        cbSize: std::mem::size_of::<WNDCLASSEXW>() as u32,
        lpfnWndProc: Some(wnd_proc),
        lpszClassName: class_name,
        hInstance: current_module_handle().unwrap_or_default(),
        ..Default::default()
    };
    let atom = unsafe { RegisterClassExW(&wc) };
    let hwnd = if atom != 0 {
        unsafe {
            CreateWindowExW(
                WINDOW_EX_STYLE(0),
                class_name,
                w!(""),
                WINDOW_STYLE(0),
                0,
                0,
                0,
                0,
                None,
                None,
                current_module_handle(),
                None,
            )
            .ok()
        }
    } else {
        None
    };

    // Register Alt+Space, fall back to Ctrl+Space.
    let mut label: &'static str = "";
    if use_alt_space && hwnd.is_some() {
        let ok = unsafe {
            windows::Win32::UI::Input::KeyboardAndMouse::RegisterHotKey(
                hwnd,
                HOTKEY_ID_ALT_SPACE,
                windows::Win32::UI::Input::KeyboardAndMouse::HOT_KEY_MODIFIERS(
                    crate::core::win32::MOD_ALT | crate::core::win32::MOD_NOREPEAT,
                ),
                crate::core::win32::VK_SPACE,
            )
            .is_ok()
        };
        if ok {
            label = "Alt + Space";
        }
    }
    if label.is_empty() && hwnd.is_some() {
        let ok = unsafe {
            windows::Win32::UI::Input::KeyboardAndMouse::RegisterHotKey(
                hwnd,
                HOTKEY_ID_ALT_SPACE,
                windows::Win32::UI::Input::KeyboardAndMouse::HOT_KEY_MODIFIERS(
                    crate::core::win32::MOD_CONTROL | crate::core::win32::MOD_NOREPEAT,
                ),
                crate::core::win32::VK_SPACE,
            )
            .is_ok()
        };
        if ok {
            label = "Ctrl + Space";
        }
    }

    // Stash the hwnd in the ctx (reused slot) so the WndProc can find the
    // sender. We reuse the global HOOK_CTX's sender via the Arc.
    let _ = ready.send((tid, label));

    // Pump.
    let mut msg = MSG::default();
    loop {
        let r = unsafe {
            GetMessageW(
                &mut msg as *mut _,
                None,
                0,
                0,
            )
        };
        if !r.as_bool() {
            break;
        }
        if msg.message == WM_QUIT_THREAD {
            break;
        }
        unsafe {
            let _ = TranslateMessage(&msg as *const _);
            DispatchMessageW(&msg as *const _);
        }
    }

    // Cleanup: unregister the hotkey + destroy the window + unregister class.
    if let Some(h) = hwnd {
        unsafe {
            let _ = windows::Win32::UI::Input::KeyboardAndMouse::UnregisterHotKey(Some(h), HOTKEY_ID_ALT_SPACE);
            let _ = windows::Win32::UI::WindowsAndMessaging::DestroyWindow(h);
        }
    }
    if atom != 0 {
        unsafe {
            let _ = windows::Win32::UI::WindowsAndMessaging::UnregisterClassW(
                class_name,
                current_module_handle(),
            );
        }
    }
    drop(ctx);
}

/// WndProc for the hidden window: turn `WM_HOTKEY` into a `HotkeyPressed` event.
#[allow(dead_code)]
unsafe extern "system" fn wnd_proc(
    hwnd: windows::Win32::Foundation::HWND,
    msg: u32,
    w_param: windows::Win32::Foundation::WPARAM,
    l_param: windows::Win32::Foundation::LPARAM,
) -> windows::Win32::Foundation::LRESULT {
    if msg == crate::core::win32::WM_HOTKEY && w_param.0 as i32 == HOTKEY_ID_ALT_SPACE {
        if let Some(ctx) = HOOK_CTX.get() {
            let _ = ctx.sender.send(HotkeyEvent::HotkeyPressed);
        }
        return windows::Win32::Foundation::LRESULT(0);
    }
    windows::Win32::UI::WindowsAndMessaging::DefWindowProcW(hwnd, msg, w_param, l_param)
}

/// `GetModuleHandleW(NULL)`-equivalent: the handle of this exe, needed by
/// `SetWindowsHookExW` / `RegisterClassExW`. Returns `HINSTANCE` (same opaque
/// pointer type as `HMODULE`; we convert the newtype).
#[allow(dead_code)]
fn current_module_handle() -> Option<windows::Win32::Foundation::HINSTANCE> {
    unsafe {
        windows::Win32::System::LibraryLoader::GetModuleHandleW(None)
            .ok()
            .map(|h| windows::Win32::Foundation::HINSTANCE(h.0))
    }
}

#[cfg(test)]
#[path = "hotkey_tests.rs"]
mod hotkey_tests;
