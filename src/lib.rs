//! FlipSwitcher — Rust rewrite.
//!
//! Core-only port. Step 1–3 from `docs/rust-rewrite-design-step1-3.md`:
//! `core/win32.rs`, `core/settings.rs`, `core/admin.rs`, single-instance + startup
//! (`main.rs`), `core/app_window.rs` + `core/enumeration.rs`, `core/icon_loader.rs`.
//! Step 4–5 from `docs/rust-rewrite-design-step4-5.md`: `core/hotkey.rs`
//! (low-level keyboard hook + state machine), `core/activation.rs` (focus-stealing
//! fallback chain) + `core/window_control.rs` (WM_CLOSE retarget + process-tree kill).
//! Pure logic + Win32 interop, unit-testable. No Slint / UI wiring yet.
//! Step 7a (`app_bridge.rs::SwitcherState`) is the pure state-machine layer for
//! the Slint bridge — still no Slint dependency, still unit-testable.

pub mod app_bridge;
pub mod core;
pub mod startup;
pub mod ui;

pub use core::activation;
pub use core::admin;
pub use core::app_window;
pub use core::enumeration;
pub use core::hotkey;
pub use core::icon_loader;
pub use core::monitors;
pub use core::pinyin;
pub use core::settings;
pub use core::win32;
pub use core::window_control;