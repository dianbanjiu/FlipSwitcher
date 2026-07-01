//! Slint UI module (Step 7b).
//!
//! `slint-build` compiles `ui/*.slint` at build time into Rust code that the
//! [`slint::include_modules!`] macro pastes here. The macro defines the
//! generated `AppWindow` component (from `ui/appwindow.slint`) directly in this
//! module, so `crate::ui::AppWindow` is the handle the bridge (Step 7c,
//! `app_bridge.rs`) instantiates and drives.
//!
//! This module pulls in the Slint runtime but does **no** Win32 of its own —
//! the design's hard boundary (Slint declares visuals only, Rust owns all
//! OS interop) is preserved.

slint::include_modules!();

// The macro already `pub use`s the generated components (`AppWindow`,
// `EmptyState`, `WindowRowData`, `SwitcherTheme`) in this module, so
// `crate::ui::AppWindow` is reachable as-is. Step 7c drives its properties /
// callbacks.
