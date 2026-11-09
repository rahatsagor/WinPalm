//! Thread-safe desktop host contract; the C ABI is in `ffi`.

use crate::protocol::{AppEntry, AppOp, MediaKey, VoiceState};
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HostConfig {
    /// Preferred TCP port. If it is taken the next free port is used.
    pub port: u16,
    /// Where pairing data, the deck and certificates are stored.
    pub data_dir: String,
    /// Directory containing the built web client.
    pub web_root: String,
    /// Bonjour host name, e.g. `Studio.local`.
    pub host_name: String,
    /// The PC's display name shown on the phone.
    pub computer_name: String,
    pub app_version: String,
    /// Address used in the pairing link.
    pub link_host: LinkHost,
    /// Believe `X-Forwarded-*` headers on connections from this PC, for a
    /// reverse proxy in front of WinPalm. Off by default.
    pub allow_local_proxy: bool,
    pub allow_pin: bool,
    pub allow_approval: bool,
    /// Lets deck buttons run shell commands. Off by default.
    pub allow_shell: bool,
    /// Whether the PC can transcribe speech streamed from the phone.
    pub voice_available: bool,
    /// Listen on 127.0.0.1 only. The Windows self-tests use this so they
    /// don't trigger the firewall prompt or accept network connections.
    #[serde(default)]
    pub loopback_only: bool,
}

/// Which address the pairing link and QR code use.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum LinkHost {
    /// The Bonjour name, which survives IP changes.
    HostName,
    /// The current LAN IP address, for networks without mDNS.
    IpAddress,
    /// An address chosen by the person: another of the PC's addresses (a
    /// VPN's, say), a name from their own DNS, or a URL such as
    /// `https://mac.example.com` for a reverse proxy. Its host name is also
    /// accepted by the server.
    Custom { address: String },
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PairingInfo {
    /// Link encoded in the QR code. Contains the one-time pairing code in the
    /// URL fragment so it never reaches server logs or proxies.
    pub link: String,
    /// Address to type by hand, without the pairing code.
    pub address: String,
    pub secure_address: String,
    pub pin: String,
    pub expires_in_secs: u32,
    pub port: u16,
    pub addresses: Vec<String>,
    /// The subset of `addresses` on VPN interfaces.
    pub vpn_addresses: Vec<String>,
    pub ca_fingerprint: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ConnectedDevice {
    pub session: u64,
    pub device_id: String,
    pub name: String,
    pub model: String,
    pub address: String,
    pub connected_at: i64,
    pub secure: bool,
    pub remembered: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ApprovalRequest {
    pub id: u64,
    pub device_name: String,
    pub model: String,
    pub address: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "event", rename_all = "camelCase", rename_all_fields = "camelCase")]
pub enum HostEvent {
    Started {
        port: u16,
        preferred_port_taken: bool,
    },
    Stopped,
    Failed {
        message: String,
    },
    SessionsChanged,
    DevicesChanged,
    PairingChanged,
    ApprovalRequested {
        request: ApprovalRequest,
    },
    /// The request was answered elsewhere or the device gave up.
    ApprovalEnded {
        id: u64,
    },
    Paired {
        name: String,
    },
    /// Something the person at the PC should know (e.g. missing permission).
    Notice {
        message: String,
    },
}

/// Actions that need Windows desktop services.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum NativeCommand {
    MissionControl,
    Launchpad,
    LockScreen,
    SleepDisplay,
    Sleep,
    Minimize,
    CloseWindow,
    Screenshot,
}

/// Implemented by the Windows desktop host. Called from background threads; implementations
/// must be thread-safe and must not call back into [`crate::host::CoreHost`]
/// synchronously from the main thread while waiting on these calls.
pub trait HostDelegate: Send + Sync {
    fn on_event(&self, event: HostEvent);
    fn list_apps(&self) -> Vec<AppEntry>;
    /// PNG data for an app icon at `size` pixels.
    fn app_icon(&self, id: String, size: u32) -> Option<Vec<u8>>;
    /// Opening an app must wait for its window to activate, and stop waiting
    /// when control for the requesting session is revoked.
    fn app_action(&self, id: String, op: AppOp, session: u64) -> bool;
    fn open_url(&self, url: String) -> bool;
    fn native(&self, command: NativeCommand) -> bool;
    fn automation_command(&self, name: String) -> Option<String>;
    fn platform_media(&self, key: MediaKey) -> bool;
    fn set_volume(&self, value: Option<f32>, muted: Option<bool>);
    fn clipboard_text(&self) -> Option<String>;
    fn set_clipboard_text(&self, text: String);
    /// Save the clipboard and replace it with `text` for a paste.
    fn begin_paste(&self, text: String) -> bool;
    /// Restore the clipboard saved by [`HostDelegate::begin_paste`].
    fn end_paste(&self);
    /// Return startup errors directly; synchronous voice-update callbacks
    /// here would reenter the voice lock before the recording is registered.
    fn voice_start(&self, session: u64, locale: Option<String>, request_id: Option<u32>) -> Result<(), String>;
    /// 16 kHz mono signed 16-bit little-endian PCM.
    fn voice_audio(&self, session: u64, pcm: Vec<u8>);
    fn voice_stop(&self, session: u64, cancel: bool);
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct VoiceUpdate {
    pub request_id: Option<u32>,
    pub state: VoiceState,
    pub text: Option<String>,
    pub is_final: bool,
    pub message: Option<String>,
}

#[derive(Debug, thiserror::Error)]
pub enum HostError {
    #[error("{message}")]
    Failed { message: String },
}

impl HostError {
    pub fn new(message: impl Into<String>) -> Self {
        HostError::Failed { message: message.into() }
    }
}
