// Entry point. Step 1: single-instance → admin correction → (stubs for theme/
// font/tray/updates) → release mutex. Implemented incrementally; for now this
// merely runs the secure startup sequence and exits when no GUI is wired.

fn main() {
    flipswitcher::startup::run();
}