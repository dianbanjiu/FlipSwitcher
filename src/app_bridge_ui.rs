//! Runtime glue between Slint, [`SwitcherState`], and the Win32 core.
//!
//! Owns the Slint component handle + a [`CoreState`] (state machine, Win32
//! host, monitors, hotkey channel) shared between the Slint callbacks and the
//! channel-drain timer. The component handle is **not** `Send` — Slint is
//! single-threaded — so we capture a [`slint::Weak`] handle in the closures and
//! the `Send`-able [`CoreState`] behind an `Arc<Mutex<>>`. Both run on the UI
//! thread; the split is purely to satisfy the `Send` bounds on closures. All
//! wiring is FFI / event-loop; correctness lives in [`crate::app_bridge`] and the
//! core modules. See `docs/rust-rewrite-design-step7.md` §C.

// `CoreState` holds `slint::Weak<UiAppWindow>` + `slint::Timer`, which are
// `!Send + !Sync` (Slint is single-threaded). We wrap it in `Arc<Mutex<>>`
// solely for shared access between Slint closures + the channel timer — all on
// the UI thread, never cross threads. Suppress the `Arc`-non-`Send` lint.
#![allow(clippy::arc_with_non_send_sync)]

use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use slint::{
    ComponentHandle, Image, ModelRc, PhysicalPosition, PhysicalSize, SharedPixelBuffer,
    SharedString, Timer, TimerMode, VecModel, WindowPosition, WindowSize, Rgba8Pixel,
};

use crate::app_bridge::{EmptyState, SwitcherHost, SwitcherState, WindowRow};
use crate::core::activation::{self, ActivationOutcome};
use crate::core::app_window::AppWindow as CoreAppWindow;
use crate::core::enumeration::WindowService;
use crate::core::hotkey::{HotkeyEvent, NavDirection};
use crate::core::monitors::{self, Monitors, PlacementMode};
use crate::core::pinyin::PinyinService;
use crate::core::settings::SettingsService;
use crate::core::win32::{Hwnd, Win32Api, WindowsApi};
use crate::core::window_control::{self, CloseResult};
use crate::ui::{AppWindow as UiAppWindow, EmptyState as UiEmptyState, WindowRowData};

/// Debounce window for the search box (matches `MainViewModel.SearchDebounceMs`).
const SEARCH_DEBOUNCE: Duration = Duration::from_millis(30);

/// The `Send`-able half of the bridge: everything that doesn't depend on Slint.
/// Held behind `Arc<Mutex<>>`, shared by the callbacks + the channel timer.
/// Public only so `build`/`run`'s signatures don't leak private types.
pub struct CoreState {
    pub ui: slint::Weak<UiAppWindow>,
    pub state: SwitcherState,
    pub host: ProductionHost,
    pub monitors: Monitors<WindowsApi>,
    #[allow(dead_code)]
    pub pinyin: PinyinService,
    pub rx: Receiver<HotkeyEvent>,
    /// Search-box debounce timer — restarted on each keystroke, fires once after
    /// `SEARCH_DEBOUNCE` of quiet to run the filter (mirrors `DispatcherTimer`).
    pub debounce: Timer,
    /// Shared hotkey state — `is_visible`/`is_search_mode` are toggled here on
    /// show/hide so the keyboard-hook decode (Tab =唤起 vs 导航) stays correct.
    pub hotkey_state: Arc<crate::core::win32::HotkeyState>,
    /// Snapshot of `settings.hide_on_focus_lost`; re-read on settings change
    /// later. When false, focus loss does not auto-hide (legacy `HideOnFocusLost`).
    pub hide_on_focus_lost: bool,
    /// Icons resolved off-thread, keyed by `WindowRow.id` (the HWND as `isize`).
    /// Populated by the channel-drain timer; consulted by `push_rows` so a row
    /// shows its real icon once the worker has decoded it.
    pub icons: std::collections::HashMap<isize, Image>,
    /// Receives icon results from the background extraction thread.
    pub icon_rx: Receiver<(isize, Option<crate::core::win32::IconImage>)>,
    /// Sender fed into the icon worker; send `()` to ask it to re-enumerate and
    /// push `(row_id, image)` pairs back on [`CoreState::icon_rx`].
    pub icon_trigger: Sender<()>,
}

/// Production [`crate::app_bridge::SwitcherHost`]: threads effects through the
/// real Win32 core. `WindowService` holds `&mut` scratch, so it lives behind a
/// `Mutex` (called from the UI thread; enumeration could move to a worker later
/// without changing the trait surface).
pub struct ProductionHost {
    svc: Mutex<WindowService<WindowsApi>>,
    api: WindowsApi,
}

impl ProductionHost {
    fn new() -> Self {
        Self {
            svc: Mutex::new(WindowService::new(WindowsApi)),
            api: WindowsApi,
        }
    }
}

impl crate::app_bridge::SwitcherHost for ProductionHost {
    fn enumerate(&mut self) -> Vec<WindowRow> {
        let mut svc = self.svc.lock().unwrap();
        let apps = svc.get_windows();
        apps.into_iter().map(|w| to_row(&mut svc, w)).collect()
    }
    fn activate(&mut self, row: &WindowRow) -> ActivationOutcome {
        activation::activate(&self.api, &snapshot(row))
    }
    fn close(&mut self, row: &WindowRow) -> CloseResult {
        window_control::close(&self.api, &snapshot(row))
    }
    fn terminate_process_tree(&mut self, pid: u32) -> bool {
        window_control::terminate_process_tree(&self.api, pid)
    }
}

/// Minimal `AppWindow` snapshot for activation / close. The legacy paths only
/// read `handle` + process identity + min/max flags; activation re-checks the
/// live state via `IsIconic`/`IsZoomed` internally, so min/max default false.
fn snapshot(row: &WindowRow) -> CoreAppWindow {
    CoreAppWindow {
        handle: Hwnd(row.id),
        title: row.title.clone(),
        class_name: String::new(),
        process_id: row.process_id,
        process_name: row.process_name.clone(),
        is_minimized: false,
        is_maximized: false,
        is_topmost: false,
        owner_kept: None,
    }
}

/// Map a core window + service-resolved metadata into a Slint row. `&mut`
/// because `is_elevated`/`monitor_number` consult mutable caches.
fn to_row(svc: &mut WindowService<WindowsApi>, w: CoreAppWindow) -> WindowRow {
    let id = w.handle.raw();
    let monitor = svc.monitor_number(w.handle);
    let is_elevated = svc.is_elevated(w.handle);
    WindowRow {
        id,
        title: if w.title.trim().is_empty() {
            w.process_name.clone()
        } else {
            w.title.clone()
        },
        process_name: w.process_name.clone(),
        monitor,
        process_id: w.process_id,
        icon_token: 0,
        is_elevated,
    }
}

/// Build the backend + UI + hotkey hook, wire callbacks, prime the list. Returns
/// the Arc to the shared core state; call [`run`] next.
pub fn build() -> Result<(UiAppWindow, Arc<Mutex<CoreState>>), String> {
    // Backend + per-window attributes hook: borderless, topmost, starts hidden,
    // transparent (so the rounded-corner fill shows).
    slint::BackendSelector::new()
        .backend_name("winit".into())
        .with_winit_window_attributes_hook(|attrs| {
            use slint::winit_030::winit::window::WindowLevel;
            attrs
                .with_decorations(false)
                .with_resizable(false)
                .with_transparent(true)
                .with_visible(false)
                .with_window_level(WindowLevel::AlwaysOnTop)
        })
        .select()
        .map_err(|e| format!("backend select: {e}"))?;

    let ui = UiAppWindow::new().map_err(|e| format!("UI new: {e}"))?;

    let settings = SettingsService::global().settings().clone();
    let hotkey_state = Arc::new(crate::core::win32::HotkeyState::new());
    hotkey_state.set_use_alt_tab(settings.use_alt_tab);

    let (tx, rx): (Sender<HotkeyEvent>, Receiver<HotkeyEvent>) = mpsc::channel();
    let hook = crate::core::hotkey::HotkeyHook::install(
        hotkey_state.clone(),
        tx,
        settings.use_alt_space,
    )
    .map_err(|e| format!("hotkey install: {e}"))?;
    ui.set_hotkey_label(SharedString::from(hook.current_hotkey_label().to_string()));
    ui.set_show_monitor_info(settings.show_monitor_info);

    let mut state = SwitcherState::new();
    state.set_pinyin_on(settings.enable_pinyin_search);

    let mut host = ProductionHost::new();
    // Prime the list so the first open isn't blank (瞬显先用上一帧占位).
    let rows = host.enumerate();
    state.apply_rows(rows, false);

    // Icon pipeline: a long-lived worker owns its own `WindowService` +
    // `IconCache`, decodes HICONs off the UI thread, and ships `(row_id, image)`
    // pairs back. UI tells it to run when the switcher opens.
    let (icon_tx, icon_rx) = mpsc::channel::<(isize, Option<crate::core::win32::IconImage>)>();
    let (icon_trigger_tx, icon_trigger_rx) = mpsc::channel::<()>();
    std::thread::Builder::new()
        .name("flipswitcher-icons".into())
        .spawn(move || icon_worker(icon_trigger_rx, icon_tx))
        .map_err(|e| format!("icon worker spawn: {e}"))?;

    let core = CoreState {
        ui: ui.as_weak(),
        state,
        host,
        monitors: Monitors::new(WindowsApi),
        pinyin: PinyinService::new(),
        rx,
        debounce: Timer::default(),
        hotkey_state,
        hide_on_focus_lost: settings.hide_on_focus_lost,
        icons: std::collections::HashMap::new(),
        icon_rx,
        icon_trigger: icon_trigger_tx,
    };
    sync_ui(&ui, &core);

    let core = Arc::new(Mutex::new(core));

    wire_callbacks(&ui, core.clone());
    register_focus_hook(&ui, core.clone());

    // The hotkey hook uninstalls in its own Drop; for a single-instance daemon
    // that never exits cleanly we leak it (matching the C# hook-for-process-life).
    std::mem::forget(hook);

    Ok((ui, core))
}

/// Drive the bridge: poll the hotkey channel on a timer, then enter the Slint
/// event loop (daemon mode — stays alive with the window hidden). The caller
/// must keep `ui` alive for the duration of the loop (it owns the strong
/// component handle that the `Weak` inside `CoreState` resolves to).
pub fn run(ui: UiAppWindow, core: Arc<Mutex<CoreState>>) -> Result<(), String> {
    let core_for_timer = core.clone();
    let timer = Timer::default();
    timer.start(TimerMode::Repeated, Duration::from_millis(16), move || {
        let mut b = core_for_timer.lock().unwrap();
        while let Ok(ev) = b.rx.try_recv() {
            handle_hotkey(&mut b, ev);
        }
        // Drain icon results from the worker; batch into the map and re-sync once
        // (a flood of arrivals collapses to a single `sync_ui`).
        let mut got_icons = false;
        while let Ok((row_id, img)) = b.icon_rx.try_recv() {
            if let Some(slint_img) = img.as_ref().and_then(icon_to_slint) {
                b.icons.insert(row_id, slint_img);
                got_icons = true;
            } else if img.is_none() {
                // Marker for "no icon" — don't keep retrying; leave the placeholder.
                b.icons.remove(&row_id);
            }
        }
        if let Some(ui) = b.ui.upgrade() {
            sync_ui(&ui, &b);
            let _ = got_icons; // sync_ui always rebuilds, so no extra action.
        }
    });
    std::mem::forget(timer);

    place(&ui, &core.lock().unwrap());
    slint::run_event_loop_until_quit().map_err(|e| format!("event loop: {e}"))
}

/// Show the switcher: reveal + position, and flip `hotkey_state.is_visible`
/// so the keyboard-hook decode (`Tab` = 唤起 vs 导航) stays correct. Mirrors
/// `HotkeyService.SetVisible(true)`.
fn show_switcher(ui: &UiAppWindow, b: &mut CoreState) {
    place(ui, b);
    let _ = ui.show();
    b.hotkey_state.set_visible(true);
    // Kick the icon worker: re-enumerate + decode icons off the UI thread.
    let _ = b.icon_trigger.send(());
}

/// Hide the switcher and clear the hotkey visibility + search-mode flags so
/// subsequent keystrokes route through the hidden-window decode path. Mirrors
/// `HotkeyService.SetVisible(false)`.
fn hide_switcher(ui: &UiAppWindow, b: &mut CoreState) {
    let _ = ui.hide();
    b.hotkey_state.set_visible(false);
}

/// Dispatch one hotkey event → state transition. `ui` is reached via the weak
/// handle stored on the [`CoreState`]; this never crosses threads.
fn handle_hotkey(b: &mut CoreState, ev: HotkeyEvent) {
    let ui = match b.ui.upgrade() {
        Some(u) => u,
        None => return,
    };
    match ev {
        HotkeyEvent::HotkeyPressed => {
            b.state.reset_grouping();
            b.state.clear_search();
            refresh_list(b, true);
            show_switcher(&ui, b);
        }
        HotkeyEvent::AltReleased => {
            let activated = {
                let CoreState { state, host, .. } = &mut *b;
                state.activate_selected(host).is_some()
            };
            if activated {
                hide_switcher(&ui, b);
            }
        }
        HotkeyEvent::NavigationRequested(dir) => {
            b.state.move_selection(matches!(dir, NavDirection::Previous));
        }
        HotkeyEvent::CloseWindowRequested => {
            let CoreState { state, host, .. } = &mut *b;
            state.close_selected(host);
        }
        HotkeyEvent::StopProcessRequested => {
            let CoreState { state, host, .. } = &mut *b;
            state.stop_selected(host);
        }
        HotkeyEvent::EscapePressed => {
            hide_switcher(&ui, b);
        }
        HotkeyEvent::GroupByProcessRequested => {
            b.state.group_by_process();
        }
        HotkeyEvent::UngroupFromProcessRequested => {
            b.state.ungroup_from_process();
        }
        HotkeyEvent::SearchModeRequested => {
            // Focusing the search box needs Win32 foreground (§5-A); the
            // dedicated path lands with icon/theme wiring. No-op for now.
        }
        HotkeyEvent::SettingsRequested => {
            // Settings window is a separate Slint component (Step 8).
        }
    }
}

fn refresh_list(b: &mut CoreState, select_second: bool) {
    let rows = b.host.enumerate();
    b.state.apply_rows(rows, select_second);
}

/// Wire the Slint `on_*` callbacks. Each captures a `Weak<UiAppWindow>` (cheaply
/// cloneable, `Send`) and an `Arc<Mutex<CoreState>>`. After mutating the state
/// we re-sync the UI visible through the upgraded handle.
fn wire_callbacks(ui: &UiAppWindow, core: Arc<Mutex<CoreState>>) {
    // Click a row → select + activate (matches legacy click-activates).
    ui.on_activated({
        let core = core.clone();
        move |row_index| {
            let mut b = core.lock().unwrap();
            b.state.set_selected(row_index);
            let activated = {
                let CoreState { state, host, .. } = &mut *b;
                state.activate_selected(host).is_some()
            };
            if activated {
                if let Some(ui) = b.ui.upgrade() {
                    hide_switcher(&ui, &mut b);
                }
            }
            if let Some(ui) = b.ui.upgrade() {
                sync_ui(&ui, &b);
            }
        }
    });

    // Search text — debounce 30ms then run the filter.
    ui.on_search_changed({
        let core = core.clone();
        move |text| {
            let text = text.to_string();
            let mut b = core.lock().unwrap();
            b.state.set_search_text(&text);
            let core_for_debounce = core.clone();
            b.debounce
                .start(TimerMode::SingleShot, SEARCH_DEBOUNCE, move || {
                    let mut b = core_for_debounce.lock().unwrap();
                    b.state.flush_filter(false);
                    if let Some(ui) = b.ui.upgrade() {
                        sync_ui(&ui, &b);
                    }
                });
        }
    });

    // Symmetric wiring for the footer/keyboard requests.
    ui.on_move_selection({
        let core = core.clone();
        move |prev| {
            let mut b = core.lock().unwrap();
            b.state.move_selection(prev);
            if let Some(ui) = b.ui.upgrade() {
                sync_ui(&ui, &b);
            }
        }
    });
    ui.on_close_selected({
        let core = core.clone();
        move || {
            let mut b = core.lock().unwrap();
            {
                let CoreState { state, host, .. } = &mut *b;
                state.close_selected(host);
            }
            if let Some(ui) = b.ui.upgrade() {
                sync_ui(&ui, &b);
            }
        }
    });
    ui.on_stop_selected({
        let core = core.clone();
        move || {
            let mut b = core.lock().unwrap();
            {
                let CoreState { state, host, .. } = &mut *b;
                state.stop_selected(host);
            }
            if let Some(ui) = b.ui.upgrade() {
                sync_ui(&ui, &b);
            }
        }
    });
    ui.on_group_requested({
        let core = core.clone();
        move || {
            let mut b = core.lock().unwrap();
            b.state.group_by_process();
            if let Some(ui) = b.ui.upgrade() {
                sync_ui(&ui, &b);
            }
        }
    });
    ui.on_ungroup_requested({
        let core = core.clone();
        move || {
            let mut b = core.lock().unwrap();
            b.state.ungroup_from_process();
            if let Some(ui) = b.ui.upgrade() {
                sync_ui(&ui, &b);
            }
        }
    });
    ui.on_escape({
        let core = core.clone();
        move || {
            let mut b = core.lock().unwrap();
            if let Some(ui) = b.ui.upgrade() {
                hide_switcher(&ui, &mut b);
            }
            if let Some(ui) = b.ui.upgrade() {
                sync_ui(&ui, &b);
            }
        }
    });
    ui.on_settings_requested(|| {
        // Settings window (Step 8); no-op here.
    });
}

/// Register the focus-lost handler on the winit window event stream. Mirrors
/// `MainWindow.Window_Deactivated` + the §3.6 rules:
///
/// - Alt still held (Alt+Tab hold mode) → don't hide; the user is still cycling.
/// - Otherwise, when `hide_on_focus_lost` is on → hide.
///
/// Returns `EventResult::Propagate` so Slint still processes the focus change.
fn register_focus_hook(ui: &UiAppWindow, core: Arc<Mutex<CoreState>>) {
    use slint::winit_030::winit::event::WindowEvent;
    use slint::winit_030::{EventResult, WinitWindowAccessor};
    ui.window().on_winit_window_event(move |_slint_win, event| {
        if let WindowEvent::Focused(false) = event {
            let mut b = core.lock().unwrap();
            // Alt+Tab hold: keep the switcher open while Alt is still down.
            let api = WindowsApi;
            let alt_held = api.is_key_down_async(crate::core::win32::VK_MENU)
                || api.is_key_down_async(crate::core::win32::VK_LMENU)
                || api.is_key_down_async(crate::core::win32::VK_RMENU);
            if !alt_held && b.hide_on_focus_lost {
                if let Some(ui) = b.ui.upgrade() {
                    hide_switcher(&ui, &mut b);
                }
            }
        }
        EventResult::Propagate
    });
}

/// Convert [`WindowRow`]s to Slint's generated model and push to the UI. Icons
/// are looked up in `icons` by `WindowRow.id`; rows without a resolved icon use
/// the placeholder. The model is rebuilt wholesale each sync — Slint's
/// `VecModel` doesn't expose in-place row updates, and icon arrival during a
/// stable window list is rare enough that rebuilding ~40 rows is cheap.
fn push_rows(ui: &UiAppWindow, rows: &[WindowRow], icons: &std::collections::HashMap<isize, Image>) {
    let model: Vec<WindowRowData> = rows
        .iter()
        .map(|r| WindowRowData {
            title: SharedString::from(r.title.as_str()),
            process_name: SharedString::from(r.process_name.as_str()),
            monitor: r.monitor as i32,
            is_elevated: r.is_elevated,
            is_minimized: false,
            icon: icons.get(&r.id).cloned().unwrap_or_else(placeholder_image),
        })
        .collect();
    ui.set_windows(ModelRc::new(VecModel::from(model)));
}

/// 1×1 transparent placeholder until IconCache → Image lands (later step).
fn placeholder_image() -> Image {
    Image::from_rgba8(SharedPixelBuffer::<Rgba8Pixel>::new(1, 1))
}

/// Convert a core [`IconImage`] to a Slint [`Image`]. The `IconImage` pixels
/// are BGRA→RGBA byte-swapped (see `win32.rs::hicon_to_image`); they're plain
/// RGBA8, so `Image::from_rgba8` matches. Used by the async pipeline once a
/// worker has decoded an HICON off the UI thread.
fn icon_to_slint(img: &crate::core::win32::IconImage) -> Option<Image> {
    if img.width == 0 || img.height == 0 || img.pixels.is_empty() {
        return None;
    }
    let expected = (img.width as usize) * (img.height as usize) * 4;
    if img.pixels.len() != expected {
        return None;
    }
    let mut buf = SharedPixelBuffer::<Rgba8Pixel>::new(img.width, img.height);
    buf.make_mut_bytes().copy_from_slice(&img.pixels);
    Some(Image::from_rgba8(buf))
}

/// Place the window centred on the primary work area (default path; the
/// mouse-screen path wires with `settings.show_on_mouse_screen` later).
/// `compute_placement` returns physical pixels, so we pass `Physical` variants.
fn place(ui: &UiAppWindow, core: &CoreState) {
    let snap = core.monitors.snapshot();
    let placement = if let Some(p) = snap.iter().find(|m| m.is_primary).or_else(|| snap.first()) {
        monitors::compute_placement(PlacementMode::PrimaryCenter {
            primary_work: &p.work,
            primary_dpi: p.dpi_scale,
        })
    } else {
        return; // No monitors: leave the default 640×520 at (0,0).
    };
    ui.window().set_position(WindowPosition::Physical(PhysicalPosition::new(
        placement.x,
        placement.y,
    )));
    ui.window().set_size(WindowSize::Physical(PhysicalSize::new(
        placement.width as u32,
        placement.height as u32,
    )));
}

/// Background icon-extraction worker. Owns its own `WindowService` +
/// `IconCache` so all Win32/DWM icon work happens off the UI thread. Blocks on
/// `trigger` (sent when the switcher opens), then enumerates windows and ships
/// `(row_id, Option<IconImage>)` pairs back. `None` marks "no icon" so the UI
/// stops waiting. Exits when the trigger channel disconnects (process exit).
/// See `docs/rust-rewrite-design.md` §3.4 — the per-hwnd vs per-exe cache split
/// lives inside `IconCache`; we just consume `load_window_icon`.
fn icon_worker(
    trigger: Receiver<()>,
    out: Sender<(isize, Option<crate::core::win32::IconImage>)>,
) {
    use crate::core::icon_loader::{IconCache, StdIconFs};
    let mut svc = WindowService::new(WindowsApi);
    let mut cache = IconCache::new(WindowsApi, StdIconFs);

    fn run_round(
        svc: &mut WindowService<WindowsApi>,
        cache: &mut IconCache<WindowsApi, StdIconFs>,
        out: &Sender<(isize, Option<crate::core::win32::IconImage>)>,
    ) -> bool {
        let apps = svc.get_windows();
        for app in &apps {
            let id = app.handle.raw();
            let img = cache.load_window_icon(app);
            if out.send((id, img)).is_err() {
                return false; // UI gone
            }
        }
        true
    }

    // Run once at startup so the primed list has icons before the first open.
    if !run_round(&mut svc, &mut cache, &out) {
        return;
    }
    // Then wait for open-triggered rounds, coalescing bursts into one round.
    while trigger.recv().is_ok() {
        while trigger.try_recv().is_ok() {}
        if !run_round(&mut svc, &mut cache, &out) {
            return;
        }
    }
}

fn update_empty_state(ui: &UiAppWindow, state: &SwitcherState) {
    let es = match state.empty_state() {
        None => UiEmptyState::r#None,
        Some(EmptyState::NoMatches) => UiEmptyState::NoMatches,
        Some(EmptyState::NoWindowsAtAll) => UiEmptyState::NoWindowsAtAll,
    };
    ui.set_empty_state(es);
    ui.set_is_search_active(!state.search_text().trim().is_empty());
}

/// Push the state's filtered rows (with resolved icons) + selection/empty state
/// to the UI. Called after every state mutation that the user-visible changes.
fn sync_ui(ui: &UiAppWindow, core: &CoreState) {
    push_rows(ui, core.state.filtered(), &core.icons);
    ui.set_selected_index(core.state.selected_index().map(|i| i as i32).unwrap_or(0));
    ui.set_window_count(core.state.filtered().len() as i32);
    update_empty_state(ui, &core.state);
}