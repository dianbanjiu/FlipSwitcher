// Entry point. Step 1 secure startup (single-instance + admin correction), then
// Step 7c: build the Slint↔Core bridge and run the event loop as a daemon.

fn main() {
    flipswitcher::startup::run();
    // `startup::run` exits on the already-running / corrective-restart branches;
    // reaching here means we're the single, correctly-elevated instance.
    let bridge = match flipswitcher::app_bridge_ui::AppBridge::build() {
        Ok(b) => b,
        Err(e) => {
            eprintln!("FlipSwitcher failed to start: {e}");
            std::process::exit(1);
        }
    };
    if let Err(e) = bridge.run() {
        eprintln!("FlipSwitcher event loop error: {e}");
        std::process::exit(1);
    }
}
