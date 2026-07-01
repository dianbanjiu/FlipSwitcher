//! Runtime glue between Slint, [`SwitcherState`], and the Win32 core.
//!
//! Step 7c thin layer: owns the Slint [`UiAppWindow`] component, the
//! [`SwitcherState`], a [`WindowService`] + [`Monitors`] for the production
//! [`SwitcherHost`], and a channel drain that turns [`HotkeyEvent`]s into state
//! transitions + Slint property writes. Not unit-tested (all FFI / event-loop
//! wiring); correctness lives in [`crate::app_bridge::SwitcherState`] and the
//! core modules. See `docs/rust-rewrite-design-step7.md` §C.
//!
//! Minimal closed loop wired here (milestone: `cargo run` shows the switcher):
//!   Alt+Tab (hidden) → place + show → enumerate off-thread → fill list →
//!   arrow/Tab navigate → Alt-release activates selected → Esc / focus-lost hide.

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

/// Production [`crate::app_bridge::SwitcherHost`]: threads effects through the
/// real Win32 core. `WindowService` holds `&mut` scratch, so it lives behind a
/// `Mutex` here (called from the UI-thread timer; enumeration could move to a
/// worker later without changing the trait surface).
struct ProductionHost {
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

/// Minimal `AppWindow` snapshot for activation / close — the legacy paths only
/// read `handle` + process identity + min/max flags. Min/max default false;
/// activation re-checks live state via `IsIconic`/`IsZoomed` internally.
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

/// Map a core [`CoreAppWindow`] + service-resolved metadata into a Slint row.
/// `&mut` because `is_elevated`/`monitor_number` consult mutable caches.
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

/// The runtime bridge. Owns the Slint handle, state, host, and the hotkey
/// channel. Created in `main`, driven by a polling [`Timer`].
pub struct AppBridge {
    ui: UiAppWindow,
    state: SwitcherState,
    host: ProductionHost,
    monitors: Monitors<WindowsApi>,
    #[allow(dead_code)]
    pinyin: PinyinService,
    rx: Receiver<HotkeyEvent>,
}

impl AppBridge {
    /// Build everything: select the winit backend with the overlay-window
    /// attributes hook, instantiate the Slint component, install the hotkey
    /// hook, wire callbacks, prime the list. Returns the bridge; call `run`.
    pub fn build() -> Result<Self, String> {
        // Backend + per-window attributes hook: borderless, topmost, starts
        // hidden, transparent (so the rounded-corner fill shows).
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

        let monitors = Monitors::new(WindowsApi);

        let bridge = AppBridge {
            ui: ui.clone_strong(),
            state,
            host,
            monitors,
            pinyin: PinyinService::new(),
            rx,
        };
        sync_ui(&bridge.ui, &bridge.state);

        // Keep the hotkey state handle for the callbacks; the hook itself is
        // leaked to live for the process (it uninstalls in its own Drop, which
        // never runs — fine for a single-instance daemon).
        wire_callbacks(&ui, hotkey_state);
        std::mem::forget(hook);

        Ok(bridge)
    }

    /// Drive the bridge: poll the hotkey channel on a timer, then enter the
    /// Slint event loop (daemon mode — stays alive with the window hidden).
    pub fn run(self) -> Result<(), String> {
        let ui = self.ui.clone_strong();
        let bridge = Arc::new(Mutex::new(self));

        // Channel drain timer — every ~16ms, apply pending hotkey events then
        // re-sync the UI. Cheap; Slint properties no-op when unchanged.
        let ui_for_timer = ui.clone_strong();
        let bridge_for_timer = bridge.clone();
        let timer = Timer::default();
        timer.start(TimerMode::Repeated, Duration::from_millis(16), move || {
            let mut b = bridge_for_timer.lock().unwrap();
            while let Ok(ev) = b.rx.try_recv() {
                b.handle_hotkey(ev);
            }
            sync_ui(&ui_for_timer, &b.state);
        });
        std::mem::forget(timer);

        // Place on the primary work area at first paint, then run.
        place(&ui, &bridge.lock().unwrap());
        slint::run_event_loop_until_quit().map_err(|e| format!("event loop: {e}"))
    }

    /// Dispatch one hotkey event → state transition.
    fn handle_hotkey(&mut self, ev: HotkeyEvent) {
        match ev {
            HotkeyEvent::HotkeyPressed => {
                self.state.reset_grouping();
                self.state.clear_search();
                self.refresh_list(true);
                let _ = self.ui.show();
                place(&self.ui, self);
            }
            HotkeyEvent::AltReleased => {
                if self.state.activate_selected(&mut self.host).is_some() {
                    let _ = self.ui.hide();
                }
            }
            HotkeyEvent::NavigationRequested(dir) => {
                self.state.move_selection(matches!(dir, NavDirection::Previous));
            }
            HotkeyEvent::CloseWindowRequested => {
                self.state.close_selected(&mut self.host);
            }
            HotkeyEvent::StopProcessRequested => {
                self.state.stop_selected(&mut self.host);
            }
            HotkeyEvent::EscapePressed => {
                let _ = self.ui.hide();
            }
            // Long-tail wiring after the smoke loop is verified:
            HotkeyEvent::SearchModeRequested
            | HotkeyEvent::SettingsRequested
            | HotkeyEvent::GroupByProcessRequested
            | HotkeyEvent::UngroupFromProcessRequested => {}
        }
    }

    fn refresh_list(&mut self, select_second: bool) {
        let rows = self.host.enumerate();
        self.state.apply_rows(rows, select_second);
    }
}

/// Wire the Slint `on_*` callbacks. Search typing is the user-facing input that
/// doesn't come through the hotkey hook; it writes back into the shared state
/// via the same `Arc<Mutex<AppBridge>>` the timer holds. For the 7c milestone
/// only the no-op stubs are wired (host reachability from a closure needs the
/// shared slot, added next).
fn wire_callbacks(ui: &UiAppWindow, _state: Arc<crate::core::win32::HotkeyState>) {
    ui.on_activated(|_row_index| {});
    ui.on_search_changed(|_text| {});
    ui.on_move_selection(|_prev| {});
    ui.on_close_selected(|| {});
    ui.on_stop_selected(|| {});
    ui.on_group_requested(|| {});
    ui.on_ungroup_requested(|| {});
    ui.on_escape(|| {});
    ui.on_settings_requested(|| {});
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

/// 1×1 transparent placeholder until IconCache → Image lands (Step 7c+).
fn placeholder_image() -> Image {
    Image::from_rgba8(SharedPixelBuffer::<Rgba8Pixel>::new(1, 1))
}

/// Place the window centred on the primary work area (Step 7c default path;
/// mouse-screen path wires with `settings.show_on_mouse_screen` later).
/// `compute_placement` returns physical pixels, so we pass `Physical` variants.
fn place(ui: &UiAppWindow, bridge: &AppBridge) {
    let snap = bridge.monitors.snapshot();
    let placement = if let Some(p) = snap.iter().find(|m| m.is_primary).or_else(|| snap.first()) {
        monitors::compute_placement(PlacementMode::PrimaryCenter {
            primary_work: &p.work,
            primary_dpi: p.dpi_scale,
        })
    } else {
        // No monitors: leave the default 640×520 at (0,0).
        return;
    };
    let _ = ui.window().set_position(WindowPosition::Physical(PhysicalPosition::new(
        placement.x,
        placement.y,
    )));
    let _ = ui.window().set_size(WindowSize::Physical(PhysicalSize::new(
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
