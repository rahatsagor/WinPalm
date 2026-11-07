//! WinPalm host core.
//!
//! The Windows app loads this crate as a DLL. It runs the phone server,
//! authenticates devices and injects input through SendInput. Desktop services
//! (apps, icons, audio, clipboard and speech) use [`api::HostDelegate`].

mod actions;
pub mod api;
pub mod auth;
pub mod deck;
pub mod ffi;
pub mod host;
pub mod input;
pub mod keycodes;
mod prefs;
pub mod protocol;
mod server;
mod session;
#[cfg(windows)]
mod shell;
mod store;
pub mod tls;

pub use api::{HostConfig, HostDelegate, HostError, HostEvent};
pub use host::CoreHost;

/// Version of the core library, for diagnostics.
pub fn core_version() -> String {
    env!("CARGO_PKG_VERSION").to_string()
}

/// Route `tracing` output to stderr (and so to Console.app via the app's
/// log capture). `filter` uses `RUST_LOG` syntax, e.g. `info,winpalm_core=debug`.
pub fn init_logging(filter: String) {
    use tracing_subscriber::EnvFilter;
    let filter = EnvFilter::try_new(&filter).unwrap_or_else(|_| EnvFilter::new("info"));
    let _ = tracing_subscriber::fmt().with_env_filter(filter).with_target(false).with_ansi(false).try_init();
}

#[cfg(test)]
mod tests;
