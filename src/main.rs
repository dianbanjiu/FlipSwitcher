// Entry point. Step 1 secure startup (single-instance + admin correction), then
// Step 7c: build the Slint↔Core bridge and run the event loop as a daemon.

fn main() {
    flipswitcher::startup::run();
    // `startup::run` exits on the already-running / corrective-restart branches;
    // reaching here means we're the single, correctly-elevated instance.
    let (ui, core) = match flipswitcher::app_bridge_ui::build() {
        Ok(c) => c,
        Err(e) => {
            eprintln!("FlipSwitcher failed to start: {e}");
            std::process::exit(1);
        }
    };
    if let Err(e) = flipswitcher::app_bridge_ui::run(ui, core) {
        eprintln!("FlipSwitcher event loop error: {e}");
        std::process::exit(1);
    }
}