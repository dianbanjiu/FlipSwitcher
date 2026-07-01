//! Build script — compile the `.slint` UI sources via `slint-build`.
//!
//! Step 7b: just compile `ui/appwindow.slint`. Style includes / palette live in
//! `ui/theme.slint`; both are added as the design's visual layer grows.

fn main() {
    slint_build::compile("ui/appwindow.slint").expect("Slint UI compilation failed");
}
