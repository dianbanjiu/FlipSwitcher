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
use crate::core::win32::{Hwnd, WindowsApi};
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
        hotkey_state,
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

    let core = CoreState {
        ui: ui.as_weak(),
        state,
        host,
        monitors: Monitors::new(WindowsApi),
        pinyin: PinyinService::new(),
        rx,
        debounce: Timer::default(),
    };
    sync_ui(&ui, &core.state);

    let core = Arc::new(Mutex::new(core));

    wire_callbacks(&ui, core.clone());

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
        if let Some(ui) = b.ui.upgrade() {
            sync_ui(&ui, &b.state);
        }
    });
    std::mem::forget(timer);

    place(&ui, &core.lock().unwrap());
    slint::run_event_loop_until_quit().map_err(|e| format!("event loop: {e}"))
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
            let _ = ui.show();
            place(&ui, b);
        }
        HotkeyEvent::AltReleased => {
            let activated = {
                let CoreState { state, host, .. } = &mut *b;
                state.activate_selected(host).is_some()
            };
            if activated {
                let _ = ui.hide();
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
            let _ = ui.hide();
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
                    let _ = ui.hide();
                }
            }
            if let Some(ui) = b.ui.upgrade() {
                sync_ui(&ui, &b.state);
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
                        sync_ui(&ui, &b.state);
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
                sync_ui(&ui, &b.state);
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
                sync_ui(&ui, &b.state);
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
                sync_ui(&ui, &b.state);
            }
        }
    });
    ui.on_group_requested({
        let core = core.clone();
        move || {
            let mut b = core.lock().unwrap();
            b.state.group_by_process();
            if let Some(ui) = b.ui.upgrade() {
                sync_ui(&ui, &b.state);
            }
        }
    });
    ui.on_ungroup_requested({
        let core = core.clone();
        move || {
            let mut b = core.lock().unwrap();
            b.state.ungroup_from_process();
            if let Some(ui) = b.ui.upgrade() {
                sync_ui(&ui, &b.state);
            }
        }
    });
    ui.on_escape({
        let core = core.clone();
        move || {
            let b = core.lock().unwrap();
            if let Some(ui) = b.ui.upgrade() {
                let _ = ui.hide();
            }
            if let Some(ui) = b.ui.upgrade() {
                sync_ui(&ui, &b.state);
            }
        }
    });
    ui.on_settings_requested(|| {
        // Settings window (Step 8); no-op here.
    });
}

/// Convert [`WindowRow`]s to Slint's generated model and push to the UI.
fn push_rows(ui: &UiAppWindow, rows: &[WindowRow]) {
    let model: Vec<WindowRowData> = rows
        .iter()
        .map(|r| WindowRowData {
            title: SharedString::from(r.title.as_str()),
            process_name: SharedString::from(r.process_name.as_str()),
            monitor: r.monitor as i32,
            is_elevated: r.is_elevated,
            is_minimized: false,
            icon: placeholder_image(),
        })
        .collect();
    ui.set_windows(ModelRc::new(VecModel::from(model)));
}

/// 1×1 transparent placeholder until IconCache → Image lands (later step).
fn placeholder_image() -> Image {
    Image::from_rgba8(SharedPixelBuffer::<Rgba8Pixel>::new(1, 1))
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

fn update_empty_state(ui: &UiAppWindow, state: &SwitcherState) {
    let es = match state.empty_state() {
        None => UiEmptyState::r#None,
        Some(EmptyState::NoMatches) => UiEmptyState::NoMatches,
        Some(EmptyState::NoWindowsAtAll) => UiEmptyState::NoWindowsAtAll,
    };
    ui.set_empty_state(es);
    ui.set_is_search_active(!state.search_text().trim().is_empty());
}

fn sync_ui(ui: &UiAppWindow, state: &SwitcherState) {
    push_rows(ui, state.filtered());
    ui.set_selected_index(state.selected_index().map(|i| i as i32).unwrap_or(0));
    ui.set_window_count(state.filtered().len() as i32);
    update_empty_state(ui, state);
}