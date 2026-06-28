//! FlipSwitcher — Rust rewrite.
//!
//! Core-only port. Step 1–3 from `docs/rust-rewrite-design-step1-3.md`:
//! `core/win32.rs`, `core/settings.rs`, `core/admin.rs`, single-instance + startup
//! (`main.rs`), `core/app_window.rs` + `core/enumeration.rs`, `core/icon_loader.rs`.
//! Pure logic + Win32 interop, unit-testable. No Slint, no hook yet.

pub mod core;
pub mod startup;

pub use core::admin;
pub use core::app_window;
pub use core::enumeration;
pub use core::icon_loader;
pub use core::settings;
pub use core::win32;