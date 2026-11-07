//! Runs the WinPalm host without the PC app, for working on the web client.
//!
//!     cargo run -p winpalm-core --example dev-host            # logs input
//!     cargo run -p winpalm-core --example dev-host -- --live  # really moves the pointer
//!
//! Set WINPALM_NAME to change the PC name shown on the phone.
//!
//! Then `cd web && bun run dev` and open the printed pairing link (or the
//! Vite URL and enter the PIN).

use std::sync::{Arc, Mutex};

use winpalm_core::api::{HostConfig, HostDelegate, HostEvent, LinkHost, NativeCommand};
use winpalm_core::host::CoreHost;
use winpalm_core::protocol::{AppEntry, AppOp};

struct Delegate {
    volume: Mutex<(f32, bool)>,
    clipboard: Mutex<String>,
}

impl HostDelegate for Delegate {
    fn on_event(&self, event: HostEvent) {
        println!("event: {event:?}");
    }
    fn list_apps(&self) -> Vec<AppEntry> {
        [
            ("builtin:browser", "Browser", true, true),
            ("builtin:explorer", "File Explorer", true, false),
            ("builtin:calculator", "Calculator", true, false),
            ("builtin:notepad", "Notepad", false, false),
            ("builtin:terminal", "Terminal", false, false),
        ]
        .into_iter()
        .map(|(id, name, running, active)| AppEntry {
            id: id.into(),
            name: name.into(),
            running,
            active,
            hidden: false,
        })
        .collect()
    }
    fn app_icon(&self, _id: String, _size: u32) -> Option<Vec<u8>> {
        None
    }
    fn app_action(&self, id: String, op: AppOp) -> bool {
        println!("app: {op:?} {id}");
        true
    }
    fn open_url(&self, url: String) -> bool {
        println!("open: {url}");
        true
    }
    fn native(&self, command: NativeCommand) -> bool {
        println!("native: {command:?}");
        true
    }
    fn automation_command(&self, _name: String) -> Option<String> {
        None
    }
    fn platform_media(&self, _key: winpalm_core::protocol::MediaKey) -> bool {
        false
    }
    fn set_volume(&self, value: Option<f32>, muted: Option<bool>) {
        let mut volume = self.volume.lock().unwrap();
        if let Some(value) = value {
            volume.0 = value;
        }
        if let Some(muted) = muted {
            volume.1 = muted;
        }
        println!("volume: {volume:?}");
    }
    fn clipboard_text(&self) -> Option<String> {
        Some(self.clipboard.lock().unwrap().clone())
    }
    fn set_clipboard_text(&self, text: String) {
        *self.clipboard.lock().unwrap() = text;
    }
    fn begin_paste(&self, text: String) -> bool {
        println!("paste: {} characters", text.chars().count());
        true
    }
    fn end_paste(&self) {}
    fn voice_start(&self, _session: u64, _locale: Option<String>) -> bool {
        false
    }
    fn voice_audio(&self, _session: u64, _pcm: Vec<u8>) {}
    fn voice_stop(&self, _session: u64, _cancel: bool) {}
}

fn main() {
    let live = std::env::args().any(|a| a == "--live");
    winpalm_core::init_logging(std::env::var("RUST_LOG").unwrap_or_else(|_| "info,winpalm::synth=info".into()));
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("..");
    let data_dir = std::env::temp_dir().join("winpalm-dev-host");
    let config = HostConfig {
        port: std::env::var("WINPALM_PORT").ok().and_then(|p| p.parse().ok()).unwrap_or(8790),
        data_dir: data_dir.to_string_lossy().into(),
        web_root: std::env::var("WINPALM_WEB_ROOT")
            .unwrap_or_else(|_| root.join("../winpalm-dev/artifacts/web").to_string_lossy().into()),
        host_name: String::new(),
        computer_name: std::env::var("WINPALM_NAME").unwrap_or_else(|_| "Dev Host".into()),
        app_version: env!("CARGO_PKG_VERSION").into(),
        link_host: LinkHost::IpAddress,
        allow_local_proxy: false,
        allow_pin: true,
        allow_approval: false,
        allow_shell: false,
        loopback_only: false,
        voice_available: false,
    };
    let delegate = Arc::new(Delegate { volume: Mutex::new((0.5, false)), clipboard: Mutex::new(String::new()) });
    if live && !cfg!(windows) {
        eprintln!("Live input is only supported on Windows.");
        std::process::exit(2);
    }
    let host = if live { CoreHost::new(config, delegate) } else { CoreHost::dry_run(config, delegate) };
    // The PIN is printed below, so it counts as shown for as long as this runs.
    host.set_pin_shown(true);
    match host.start() {
        Ok(port) => {
            let info = host.pairing_info();
            println!("\nWinPalm dev host on port {port} ({})", if live { "LIVE input" } else { "dry run" });
            println!("  link: {}\n  PIN:  {}\n", info.link, info.pin);
        }
        Err(err) => {
            eprintln!("could not start: {err}");
            std::process::exit(1);
        }
    }
    loop {
        std::thread::park();
    }
}
