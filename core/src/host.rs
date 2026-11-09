//! The [`CoreHost`] object the Windows host owns, and state shared by server tasks.

use std::collections::HashMap;
use std::net::IpAddr;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU16, AtomicU64, Ordering};
use std::sync::mpsc::Sender;
use std::sync::{Arc, Mutex, MutexGuard, OnceLock, RwLock, Weak};
use std::time::{Duration, Instant};

use tokio::sync::{mpsc, oneshot, watch};

use crate::api::{
    ApprovalRequest, ConnectedDevice, HostConfig, HostDelegate, HostError, HostEvent, LinkHost, PairingInfo,
    VoiceUpdate,
};
use crate::auth::{self, Pairing, TrustStore, TrustedDevice};
use crate::deck::Deck;
use crate::input::{Command, InputEngine, Interrupts, LayoutMap, Notice, SessionId, Tuning};
use crate::prefs::DeviceSettings;
use crate::protocol::{Features, KeyboardLayout, NoticeLevel, ServerMessage, VolumeState};
use crate::{server, store, tls};

/// The saved deck, if it can be used. One that parses but isn't valid, say
/// from a newer version, is kept aside so the next save can't overwrite it.
fn load_deck(path: &Path) -> Option<Deck> {
    let deck = store::load::<Deck>(path)?;
    match deck.validate() {
        Ok(()) => Some(deck),
        Err(err) => {
            tracing::error!(%err, "ignoring a saved deck that isn't valid");
            store::quarantine(path);
            None
        }
    }
}

#[allow(clippy::large_enum_variant)]
pub(crate) enum Outgoing {
    Message(ServerMessage),
    Close { reason: String, reconnect: bool },
}

pub(crate) const STOPPED_SHARING: &str = "The PC stopped sharing.";

pub(crate) struct LiveSession {
    pub info: ConnectedDevice,
    /// Authorizes HTTP requests (app icons) made on behalf of this session.
    pub key: String,
    pub tx: mpsc::UnboundedSender<Outgoing>,
}

pub(crate) struct PendingApproval {
    pub request: ApprovalRequest,
    pub reply: oneshot::Sender<bool>,
}

pub(crate) struct State {
    pub trust: TrustStore,
    pub pairing: Pairing,
    pub deck: Deck,
    pub deck_revision: u64,
    pub layout: Option<KeyboardLayout>,
    pub volume: Option<VolumeState>,
    pub paused: bool,
    pub caps_lock: bool,
    pub sessions: HashMap<SessionId, LiveSession>,
    pub approvals: HashMap<u64, PendingApproval>,
}

pub(crate) type IconCache = HashMap<(String, u32), Arc<Vec<u8>>>;

/// Voice operations share this lock so revocation cannot race a queued start.
#[derive(Default)]
pub(crate) struct Voices {
    pub active: HashMap<SessionId, Option<u32>>,
    pub accepted: HashMap<SessionId, Vec<Option<u32>>>,
}

pub(crate) struct Shared {
    pub config: RwLock<HostConfig>,
    pub delegate: Arc<dyn HostDelegate>,
    pub engine: Mutex<Sender<Command>>,
    pub interrupts: Arc<Interrupts>,
    /// Serializes clipboard swaps so overlapping pastes can't lose the
    /// clipboard the user had before.
    pub paste_lock: Mutex<()>,
    /// Keeps the saved deck file and the in-memory deck in step, and holds
    /// the ticket of the deck saved last.
    pub deck_lock: Mutex<u64>,
    pub deck_tickets: AtomicU64,
    /// The trusted-device file revision written last; see [`Shared::save_trust`].
    trust_written: Mutex<u64>,
    /// Open connections per address; see `server::ConnectionSlot`.
    pub connections: Mutex<HashMap<IpAddr, usize>>,
    pub settings: Mutex<DeviceSettings>,
    pub voices: Mutex<Voices>,
    pub voice_epoch: AtomicU64,
    state: Mutex<State>,
    pub tls: RwLock<Option<Arc<tls::Identity>>>,
    pub port: AtomicU16,
    next_id: AtomicU64,
    pub icons: Mutex<IconCache>,
    pub permission: Box<dyn Fn() -> bool + Send + Sync>,
}

impl Shared {
    pub(crate) fn new(
        config: HostConfig,
        delegate: Arc<dyn HostDelegate>,
        engine: &InputEngine,
        permission: Box<dyn Fn() -> bool + Send + Sync>,
    ) -> Self {
        let dir = data_dir(&config);
        let trust = TrustStore::load(dir.as_ref().map(|d| d.join("devices.json")));
        let mut settings = DeviceSettings::load(dir.as_ref().map(|d| d.join("settings")));
        // A device list that couldn't be read must not take every device's
        // settings with it.
        if trust.intact() {
            settings.retain(&trust.devices().into_iter().map(|d| d.id).collect::<Vec<_>>());
        }
        let deck = dir.as_ref().and_then(|d| load_deck(&d.join("deck.json"))).unwrap_or_default();
        Self {
            config: RwLock::new(config),
            delegate,
            engine: Mutex::new(engine.sender()),
            interrupts: engine.interrupts(),
            paste_lock: Mutex::new(()),
            deck_lock: Mutex::new(0),
            deck_tickets: AtomicU64::new(0),
            trust_written: Mutex::new(0),
            connections: Mutex::new(HashMap::new()),
            settings: Mutex::new(settings),
            voices: Mutex::new(Voices::default()),
            voice_epoch: AtomicU64::new(0),
            state: Mutex::new(State {
                trust,
                pairing: Pairing::new(Instant::now()),
                deck,
                deck_revision: 0,
                layout: None,
                volume: None,
                paused: false,
                caps_lock: false,
                sessions: HashMap::new(),
                approvals: HashMap::new(),
            }),
            tls: RwLock::new(None),
            port: AtomicU16::new(0),
            next_id: AtomicU64::new(1),
            icons: Mutex::new(HashMap::new()),
            permission,
        }
    }

    pub fn state(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    pub fn config(&self) -> HostConfig {
        self.config.read().unwrap_or_else(|p| p.into_inner()).clone()
    }

    pub fn next_id(&self) -> u64 {
        self.next_id.fetch_add(1, Ordering::Relaxed)
    }

    pub fn engine(&self, command: Command) {
        let _ = self.engine.lock().unwrap_or_else(|p| p.into_inner()).send(command);
    }

    /// Wait until the engine has processed everything queued so far.
    pub fn flush_engine(&self) {
        let (tx, rx) = std::sync::mpsc::channel();
        self.engine(Command::Flush(tx));
        let _ = rx.recv_timeout(Duration::from_secs(5));
    }

    /// Like [`Shared::flush_engine`], but with no time limit, for work that
    /// must not happen before queued input is posted. The engine answers in
    /// order, and returns at once if it has shut down.
    pub fn finish_engine(&self) {
        let (tx, rx) = std::sync::mpsc::channel();
        self.engine(Command::Flush(tx));
        let _ = rx.recv();
    }

    /// Call the delegate. Never call this while holding the state lock.
    pub fn emit(&self, event: HostEvent) {
        self.delegate.on_event(event);
    }

    pub fn broadcast(&self, message: ServerMessage) {
        for session in self.state().sessions.values() {
            let _ = session.tx.send(Outgoing::Message(message.clone()));
        }
    }

    pub fn send_to(&self, session: SessionId, message: ServerMessage) {
        if let Some(live) = self.state().sessions.get(&session) {
            let _ = live.tx.send(Outgoing::Message(message));
        }
    }

    /// Write changes to the trusted devices. Saving syncs to disk, so call it
    /// after the state lock is released, never while holding it.
    pub fn save_trust(&self) {
        let pending = self.state().trust.pending_save();
        if let Some(save) = pending {
            let revision = save.revision();
            if !save.write(&self.trust_written) {
                // Housekeeping tries again, so a device paired while the disk
                // was full isn't forgotten when WinPalm next opens.
                self.state().trust.save_failed(revision);
            }
        }
    }

    pub fn trust_unsaved(&self) -> bool {
        self.state().trust.unsaved()
    }

    pub fn device_settings(&self) -> MutexGuard<'_, DeviceSettings> {
        self.settings.lock().unwrap_or_else(|p| p.into_inner())
    }

    pub fn voices(&self) -> MutexGuard<'_, Voices> {
        self.voices.lock().unwrap_or_else(|p| p.into_inner())
    }

    pub fn cancel_voices(&self) {
        self.voice_epoch.fetch_add(1, Ordering::SeqCst);
        let mut voices = self.voices();
        // A stopped recording may still be delivering its final phrase
        // after a newer one completed. Cancel those native resources too.
        let accepted = std::mem::take(&mut voices.accepted);
        for id in accepted.keys().filter(|id| !voices.active.contains_key(id)) {
            self.delegate.voice_stop(*id, true);
        }
        for (id, request_id) in voices.active.drain() {
            self.delegate.voice_stop(id, true);
            // Older open clients react to voice replies, but do not yet
            // release their microphone on a paused/features message.
            self.send_to(
                id,
                ServerMessage::Voice {
                    id: request_id,
                    state: crate::protocol::VoiceState::Unavailable,
                    text: None,
                    is_final: false,
                    message: Some("Dictation was stopped on the PC.".into()),
                },
            );
        }
    }

    /// Whether work started for `session` should stop: input is paused or the
    /// device has disconnected.
    pub fn should_stop(&self, session: SessionId) -> bool {
        self.interrupts.should_stop(session) || !self.state().sessions.contains_key(&session)
    }

    pub fn features(&self, secure: bool) -> Features {
        let config = self.config();
        Features { voice: config.voice_available, shell: config.allow_shell, secure, permission: (self.permission)() }
    }

    pub fn data_dir(&self) -> Option<PathBuf> {
        data_dir(&self.config())
    }

    pub fn session_key_valid(&self, key: &str) -> bool {
        !key.is_empty()
            && self.state().sessions.values().any(|s| auth::constant_time_eq(s.key.as_bytes(), key.as_bytes()))
    }

    fn notice(&self, notice: Notice) {
        let (session, code, message) = match notice {
            Notice::CapsLock(on) => {
                self.state().caps_lock = on;
                self.broadcast(ServerMessage::CapsLock { on });
                return;
            }
            Notice::SecureInput(session) => (
                session,
                "secureInput",
                "A password field on the PC is blocking typed input. Click outside it or type on the PC.",
            ),
            Notice::NoPermission(session) => (
                session,
                "permission",
                "Windows blocked remote input. Close the UAC prompt or switch to a regular desktop app.",
            ),
        };
        self.send_to(
            session,
            ServerMessage::Notice { level: NoticeLevel::Warning, code: code.into(), message: message.into() },
        );
        if matches!(notice, Notice::NoPermission(_)) {
            self.emit(HostEvent::Notice { message: message.into() });
        }
    }
}

fn data_dir(config: &HostConfig) -> Option<PathBuf> {
    (!config.data_dir.is_empty()).then(|| PathBuf::from(&config.data_dir))
}

/// Interfaces that VPNs and mesh networks create on Unix-like systems:
/// WireGuard and most VPN apps use `utun`, ZeroTier uses `feth`.
#[cfg(not(windows))]
const VPN_INTERFACES: [&str; 8] = ["utun", "ipsec", "ppp", "tun", "tap", "wg", "zt", "feth"];

/// Words in Windows adapter names and descriptions that mark VPNs and mesh
/// networks.
const VPN_WORDS: [&str; 12] = [
    "tailscale",
    "wireguard",
    "vpn",
    "zerotier",
    "tunnel",
    "openvpn",
    "tap-windows",
    "wintun",
    "nordlynx",
    "anyconnect",
    "fortinet",
    "globalprotect",
];

/// Words that mark adapters a phone on the same Wi-Fi can rarely reach: WSL,
/// Hyper-V, Docker, VM host-only networks, Bluetooth and Wi-Fi Direct.
const VIRTUAL_WORDS: [&str; 9] =
    ["hyper-v", "vethernet", "virtualbox", "vmware", "wsl", "docker", "virtual", "bluetooth", "npcap"];

/// Rank a Windows adapter for the pairing QR code; lower is better. Adapter
/// names are user-visible labels such as "Wi-Fi" or "vEthernet (WSL)", so use
/// the interface type and default gateway instead of name prefixes.
/// Returns `(rank, is_vpn)`.
#[cfg_attr(not(windows), allow(dead_code))]
pub(crate) fn windows_adapter_rank(name: &str, description: &str, if_type: u32, gateway: bool) -> (u8, bool) {
    // IF_TYPE_PPP, IF_TYPE_PROP_VIRTUAL (Wintun, TAP) and IF_TYPE_TUNNEL.
    let text = format!("{name} {description}").to_ascii_lowercase();
    let vpn = matches!(if_type, 23 | 53 | 131) || VPN_WORDS.iter().any(|word| text.contains(word));
    let virtual_adapter = VIRTUAL_WORDS.iter().any(|word| text.contains(word));
    // IF_TYPE_ETHERNET_CSMACD and IF_TYPE_IEEE80211.
    let physical = matches!(if_type, 6 | 71) && !virtual_adapter;
    let rank = if vpn {
        4
    } else if gateway && physical {
        0
    } else if gateway {
        // A Hyper-V external switch moves the PC's real LAN address here.
        1
    } else if physical {
        2
    } else {
        3
    };
    (rank, vpn)
}

/// Addresses a phone might reach, Wi-Fi and Ethernet first, each marked
/// with whether it belongs to a VPN.
#[cfg(not(windows))]
fn network_addresses() -> Vec<(IpAddr, bool)> {
    let Ok(interfaces) = if_addrs::get_if_addrs() else {
        return Vec::new();
    };
    let mut found: Vec<(u8, IpAddr, bool)> = interfaces
        .into_iter()
        .filter(|iface| !iface.is_loopback())
        .filter_map(|iface| {
            let IpAddr::V4(v4) = iface.ip() else { return None };
            if v4.is_link_local() || v4.is_unspecified() {
                return None;
            }
            let name = iface.name.as_str();
            let lower = name.to_ascii_lowercase();
            let vpn = VPN_INTERFACES.iter().any(|prefix| name.starts_with(prefix))
                || VPN_WORDS.iter().any(|word| lower.contains(word));
            let rank = if name.starts_with("en") {
                0
            } else if vpn || name.starts_with("bridge") || name.starts_with("awdl") || name.starts_with("llw") {
                // VPNs, Internet Sharing and AirDrop interfaces are rarely what
                // a phone on the same Wi-Fi can reach.
                2
            } else {
                1
            };
            Some((rank, IpAddr::V4(v4), vpn))
        })
        .collect();
    found.sort();
    found.dedup_by_key(|(_, ip, _)| *ip);
    found.into_iter().map(|(_, ip, vpn)| (ip, vpn)).collect()
}

/// Addresses a phone might reach: the Wi-Fi or Ethernet adapter that has the
/// default gateway first, then by route metric, each marked with whether it
/// belongs to a VPN.
#[cfg(windows)]
fn network_addresses() -> Vec<(IpAddr, bool)> {
    use windows_sys::Win32::Foundation::{ERROR_BUFFER_OVERFLOW, ERROR_SUCCESS};
    use windows_sys::Win32::NetworkManagement::IpHelper::*;
    use windows_sys::Win32::NetworkManagement::Ndis::IfOperStatusUp;
    use windows_sys::Win32::Networking::WinSock::{AF_INET, SOCKADDR_IN};

    fn wide(text: *const u16) -> String {
        if text.is_null() {
            return String::new();
        }
        // SAFETY: GetAdaptersAddresses returns NUL-terminated UTF-16 strings
        // that live as long as its buffer.
        unsafe {
            let length = (0..).take_while(|&i| *text.add(i) != 0).count();
            String::from_utf16_lossy(std::slice::from_raw_parts(text, length))
        }
    }

    let flags = GAA_FLAG_INCLUDE_GATEWAYS | GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER;
    let mut size: u32 = 16 * 1024;
    let mut buffer: Vec<u64> = Vec::new();
    let mut result = ERROR_BUFFER_OVERFLOW;
    // Adapters can appear between the size query and the read; retry a little.
    for _ in 0..4 {
        buffer.resize((size as usize).div_ceil(8), 0);
        // SAFETY: the buffer is 8-byte aligned and `size` bytes long.
        result = unsafe {
            GetAdaptersAddresses(AF_INET as u32, flags, std::ptr::null(), buffer.as_mut_ptr().cast(), &mut size)
        };
        if result != ERROR_BUFFER_OVERFLOW {
            break;
        }
    }
    if result != ERROR_SUCCESS {
        return Vec::new();
    }
    let mut found: Vec<(u8, u32, IpAddr, bool)> = Vec::new();
    let mut adapter = buffer.as_ptr().cast::<IP_ADAPTER_ADDRESSES_LH>();
    // SAFETY: a linked list inside `buffer`, valid until it is dropped.
    unsafe {
        while let Some(current) = adapter.as_ref() {
            adapter = current.Next;
            // IF_TYPE_SOFTWARE_LOOPBACK.
            if current.OperStatus != IfOperStatusUp || current.IfType == 24 {
                continue;
            }
            let (rank, vpn) = windows_adapter_rank(
                &wide(current.FriendlyName),
                &wide(current.Description),
                current.IfType,
                !current.FirstGatewayAddress.is_null(),
            );
            let mut unicast = current.FirstUnicastAddress;
            while let Some(address) = unicast.as_ref() {
                unicast = address.Next;
                let socket = address.Address.lpSockaddr;
                if socket.is_null() || (*socket).sa_family != AF_INET {
                    continue;
                }
                let v4 = std::net::Ipv4Addr::from(u32::from_be((*socket.cast::<SOCKADDR_IN>()).sin_addr.S_un.S_addr));
                if v4.is_loopback() || v4.is_link_local() || v4.is_unspecified() {
                    continue;
                }
                found.push((rank, current.Ipv4Metric, IpAddr::V4(v4), vpn));
            }
        }
    }
    found.sort();
    let mut seen = std::collections::HashSet::new();
    found.into_iter().filter(|(_, _, ip, _)| seen.insert(*ip)).map(|(_, _, ip, vpn)| (ip, vpn)).collect()
}

/// LAN addresses a phone can reach, Wi-Fi and Ethernet first.
pub fn lan_addresses() -> Vec<IpAddr> {
    network_addresses().into_iter().map(|(ip, _)| ip).collect()
}

/// Plain and encrypted base URLs for an address typed by hand. A bare host
/// gets WinPalm's port; a URL is used as given, so a reverse proxy on the
/// default HTTPS port works. `None` if it isn't usable.
pub(crate) fn custom_link(address: &str, port: u16) -> Option<(String, String)> {
    let address = address.trim();
    let (scheme, authority) = match address.split_once("://") {
        Some((scheme, rest)) => (Some(scheme.to_ascii_lowercase()), rest.trim_end_matches('/')),
        None => (None, address.trim_end_matches('/')),
    };
    let (plain, authority) = match scheme.as_deref() {
        Some("https") => ("https", authority.to_string()),
        Some("http") => ("http", authority.to_string()),
        Some(_) => return None,
        None if authority.starts_with('[') => {
            let (_, after) = authority.split_once(']')?;
            ("http", if after.is_empty() { format!("{authority}:{port}") } else { authority.to_string() })
        }
        None if authority.matches(':').count() > 1 => ("http", format!("[{authority}]:{port}")),
        None if authority.rsplit_once(':').is_some_and(|(_, p)| p.parse::<u16>().is_ok()) => {
            ("http", authority.to_string())
        }
        None => ("http", format!("{authority}:{port}")),
    };
    let name = server::host_name(&authority);
    let valid = match name.strip_prefix('[') {
        Some(rest) => rest.strip_suffix(']').is_some_and(|ip| ip.parse::<std::net::Ipv6Addr>().is_ok()),
        None => {
            !name.is_empty()
                && name
                    .split('.')
                    .all(|label| !label.is_empty() && label.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-'))
        }
    };
    valid.then(|| (format!("{plain}://{authority}"), format!("https://{authority}")))
}

/// The host name of a custom pairing address, which the server accepts in
/// addition to the names in [`server::local_host`].
pub(crate) fn custom_host_name(config: &HostConfig) -> Option<String> {
    let LinkHost::Custom { address } = &config.link_host else { return None };
    let (base, _) = custom_link(address, config.port)?;
    let (_, authority) = base.split_once("://")?;
    Some(server::host_name(authority))
}

struct Running {
    runtime: tokio::runtime::Runtime,
    shutdown: watch::Sender<bool>,
}

/// The WinPalm host: owns the input engine and the local server.
#[derive()]
pub struct CoreHost {
    shared: Arc<Shared>,
    _engine: Option<InputEngine>,
    running: Mutex<Option<Running>>,
}

impl CoreHost {
    pub(crate) fn with_shared(shared: Arc<Shared>, engine: Option<InputEngine>) -> Self {
        Self { shared, _engine: engine, running: Mutex::new(None) }
    }

    fn running(&self) -> MutexGuard<'_, Option<Running>> {
        self.running.lock().unwrap_or_else(|p| p.into_inner())
    }

    fn close_sessions(&self, reason: &str, reconnect: bool) {
        let sessions: Vec<_> = self.shared.state().sessions.values().map(|s| s.tx.clone()).collect();
        for tx in sessions {
            let _ = tx.send(Outgoing::Close { reason: reason.into(), reconnect });
        }
    }

    fn close_device(&self, device_id: &str, reason: &str) {
        let sessions: Vec<_> = self
            .shared
            .state()
            .sessions
            .values()
            .filter(|s| s.info.device_id == device_id)
            .map(|s| s.tx.clone())
            .collect();
        for tx in sessions {
            let _ = tx.send(Outgoing::Close { reason: reason.into(), reconnect: false });
        }
    }
}

type Notify = Box<dyn Fn(Notice) + Send>;
type Permission = Box<dyn Fn() -> bool + Send + Sync>;

impl CoreHost {
    fn build(
        config: HostConfig,
        delegate: Arc<dyn HostDelegate>,
        engine: impl FnOnce(Notify) -> (InputEngine, Permission),
    ) -> Arc<Self> {
        let slot: Arc<OnceLock<Weak<Shared>>> = Arc::new(OnceLock::new());
        let notify_slot = slot.clone();
        let notify: Notify = Box::new(move |notice: Notice| {
            if let Some(shared) = notify_slot.get().and_then(Weak::upgrade) {
                shared.notice(notice);
            }
        });
        let (engine, permission) = engine(notify);
        let shared = Arc::new(Shared::new(config, delegate, &engine, permission));
        let _ = slot.set(Arc::downgrade(&shared));
        Arc::new(Self::with_shared(shared, Some(engine)))
    }

    /// A host that logs input instead of posting it (development and demos).
    pub fn dry_run(config: HostConfig, delegate: Arc<dyn HostDelegate>) -> Arc<Self> {
        Self::build(config, delegate, |notify| {
            (InputEngine::spawn(crate::input::LogSink::default, notify), Box::new(|| true))
        })
    }
}

impl CoreHost {
    pub fn new(config: HostConfig, delegate: Arc<dyn HostDelegate>) -> Arc<Self> {
        #[cfg(windows)]
        return Self::build(config, delegate, |notify| {
            (
                InputEngine::spawn(crate::input::windows::WindowsSink::new, notify),
                Box::new(crate::input::windows::has_post_permission),
            )
        });
        #[cfg(not(windows))]
        Self::dry_run(config, delegate)
    }

    /// Start serving. Returns the port in use.
    pub fn start(&self) -> Result<u16, HostError> {
        let mut running = self.running();
        if running.is_some() {
            return Ok(self.shared.port.load(Ordering::Relaxed));
        }
        let runtime = tokio::runtime::Builder::new_multi_thread()
            .worker_threads(2)
            .thread_name("winpalm-net")
            .enable_all()
            .build()
            .map_err(|e| {
                let message = format!("could not start the network runtime: {e}");
                self.shared.emit(HostEvent::Failed { message: message.clone() });
                HostError::new(message)
            })?;
        let (shutdown, shutdown_rx) = watch::channel(false);
        let shared = self.shared.clone();
        let (port, taken) = runtime.block_on(server::start(shared, shutdown_rx)).map_err(|e| {
            let message = e.to_string();
            self.shared.emit(HostEvent::Failed { message: message.clone() });
            HostError::new(message)
        })?;
        *running = Some(Running { runtime, shutdown });
        drop(running);
        self.shared.emit(HostEvent::Started { port, preferred_port_taken: taken });
        Ok(port)
    }

    pub fn stop(&self) {
        let Some(running) = self.running().take() else {
            return;
        };
        self.close_sessions(STOPPED_SHARING, true);
        let _ = running.shutdown.send(true);
        self.shared.cancel_voices();
        // Give sessions a moment to send their goodbye frames.
        std::thread::sleep(Duration::from_millis(100));
        running.runtime.shutdown_timeout(Duration::from_secs(1));
        // Session tasks may have been dropped mid-flight; release their input
        // and stop any dictation they left running.
        let ids: Vec<SessionId> = self.shared.state().sessions.drain().map(|(id, _)| id).collect();
        for id in ids {
            self.shared.interrupts.end(id);
            self.shared.engine(Command::EndSession(id));
            self.shared.delegate.voice_stop(id, true);
        }
        // A delegate start may have been in flight during runtime shutdown.
        // With sessions removed, cancel its registration under the voice lock.
        self.shared.cancel_voices();
        let approvals: Vec<u64> = self.shared.state().approvals.drain().map(|(id, _)| id).collect();
        for id in approvals {
            self.shared.emit(HostEvent::ApprovalEnded { id });
        }
        self.shared.emit(HostEvent::Stopped);
        self.shared.emit(HostEvent::SessionsChanged);
    }

    pub fn is_running(&self) -> bool {
        self.running().is_some()
    }

    pub fn update_config(&self, config: HostConfig) {
        let previous = self.shared.config();
        *self.shared.config.write().unwrap_or_else(|p| p.into_inner()) = config.clone();
        if previous.allow_shell && !config.allow_shell {
            self.shared.interrupts.cancel_shell();
        }
        if previous.voice_available && !config.voice_available {
            self.shared.cancel_voices();
        }
        if previous.allow_shell != config.allow_shell || previous.voice_available != config.voice_available {
            let permission = (self.shared.permission)();
            let sessions: Vec<_> =
                self.shared.state().sessions.values().map(|s| (s.tx.clone(), s.info.secure)).collect();
            for (tx, secure) in sessions {
                let features =
                    Features { voice: config.voice_available, shell: config.allow_shell, secure, permission };
                let _ = tx.send(Outgoing::Message(ServerMessage::Features { features }));
            }
        }
        if previous.link_host != config.link_host || previous.host_name != config.host_name {
            self.shared.emit(HostEvent::PairingChanged);
        }
    }

    pub fn pairing_info(&self) -> PairingInfo {
        let config = self.shared.config();
        let port = self.shared.port.load(Ordering::Relaxed);
        let network = network_addresses();
        let addresses: Vec<IpAddr> = network.iter().map(|(ip, _)| *ip).collect();
        let ip = addresses.first().map(IpAddr::to_string).unwrap_or_else(|| "127.0.0.1".into());
        let host = config.host_name.trim().trim_end_matches('.').to_string();
        let custom = match &config.link_host {
            LinkHost::Custom { address } => custom_link(address, port),
            _ => None,
        };
        let (address, secure_address) = custom.unwrap_or_else(|| {
            let link_host = match &config.link_host {
                LinkHost::IpAddress => ip.clone(),
                LinkHost::HostName | LinkHost::Custom { .. } if !host.is_empty() => host,
                LinkHost::HostName | LinkHost::Custom { .. } => ip.clone(),
            };
            // Bare Windows computer names are not within the local CA's
            // DNS constraints. Advertise its certified IP for HTTPS instead.
            let secure_host = if tls::permitted(&link_host) { &link_host } else { &ip };
            (format!("http://{link_host}:{port}"), format!("https://{secure_host}:{port}"))
        });
        let mut state = self.shared.state();
        let now = Instant::now();
        state.pairing.refresh(now);
        let info = PairingInfo {
            link: format!("{address}/#pair={}", state.pairing.code()),
            secure_address,
            address,
            pin: state.pairing.pin().to_string(),
            expires_in_secs: state.pairing.expires_in(now).as_secs() as u32,
            port,
            addresses: addresses.iter().map(IpAddr::to_string).collect(),
            vpn_addresses: network.iter().filter(|(_, vpn)| *vpn).map(|(ip, _)| ip.to_string()).collect(),
            ca_fingerprint: String::new(),
        };
        drop(state);
        let fingerprint = self
            .shared
            .tls
            .read()
            .unwrap_or_else(|p| p.into_inner())
            .as_ref()
            .map(|id| id.ca_fingerprint.clone())
            .unwrap_or_default();
        PairingInfo { ca_fingerprint: fingerprint, ..info }
    }

    /// Invalidate the current QR code and PIN.
    pub fn renew_pairing(&self) {
        self.shared.state().pairing.rotate(Instant::now());
        self.shared.emit(HostEvent::PairingChanged);
    }

    /// Tell the core whether the PIN is on screen. The PIN is only accepted
    /// while it is shown, and for a few minutes after it is hidden.
    pub fn set_pin_shown(&self, shown: bool) {
        self.shared.state().pairing.set_pin_shown(shown, Instant::now());
    }

    pub fn connected_devices(&self) -> Vec<ConnectedDevice> {
        let mut list: Vec<_> = self.shared.state().sessions.values().map(|s| s.info.clone()).collect();
        list.sort_by_key(|d| d.connected_at);
        list
    }

    pub fn trusted_devices(&self) -> Vec<TrustedDevice> {
        self.shared
            .state()
            .trust
            .devices()
            .into_iter()
            .map(|d| TrustedDevice { name: auth::visible_name(&d.name), ..d })
            .collect()
    }

    pub fn forget_device(&self, id: String) {
        let removed = self.shared.state().trust.remove(&id);
        self.shared.save_trust();
        self.shared.device_settings().remove(&id);
        self.close_device(&id, "This device was removed on the PC. Pair again to reconnect.");
        if removed {
            self.shared.emit(HostEvent::DevicesChanged);
        }
    }

    pub fn forget_all_devices(&self) {
        self.shared.state().trust.clear();
        self.shared.save_trust();
        self.shared.device_settings().retain(&[]);
        self.close_sessions("All devices were removed on the PC. Pair again to reconnect.", false);
        self.shared.emit(HostEvent::DevicesChanged);
    }

    pub fn rename_device(&self, id: String, name: String) {
        let renamed = {
            let mut state = self.shared.state();
            let renamed = state.trust.rename(&id, &name);
            if renamed {
                let shown = auth::visible_name(&state.trust.name(&id).unwrap_or_default());
                for session in state.sessions.values_mut().filter(|s| s.info.device_id == id) {
                    session.info.name = shown.clone();
                }
            }
            renamed
        };
        self.shared.save_trust();
        if renamed {
            self.shared.emit(HostEvent::DevicesChanged);
            self.shared.emit(HostEvent::SessionsChanged);
        }
    }

    pub fn disconnect(&self, session: u64) {
        if let Some(live) = self.shared.state().sessions.get(&session) {
            let _ = live.tx.send(Outgoing::Close { reason: "Disconnected from the PC.".into(), reconnect: false });
        }
    }

    pub fn respond_approval(&self, id: u64, allow: bool) {
        let pending = self.shared.state().approvals.remove(&id);
        if let Some(pending) = pending {
            let _ = pending.reply.send(allow);
            self.shared.emit(HostEvent::ApprovalEnded { id });
        }
    }

    /// Ignore all input from devices until resumed. Held keys are released.
    pub fn set_paused(&self, paused: bool) {
        {
            let mut state = self.shared.state();
            if state.paused == paused {
                return;
            }
            state.paused = paused;
        }
        self.shared.interrupts.set_paused(paused);
        // Release held input and notify the phone before waiting for a slow
        // native recognizer startup to leave the voice lock.
        self.shared.engine(Command::SetPaused(paused));
        self.shared.broadcast(ServerMessage::Paused { paused });
        if paused {
            self.shared.cancel_voices();
        }
    }

    pub fn is_paused(&self) -> bool {
        self.shared.state().paused
    }

    /// The PC's active keyboard layout changed.
    pub fn set_layout(&self, layout: KeyboardLayout) {
        self.shared.engine(Command::Layout(LayoutMap::from_layout(&layout)));
        self.shared.state().layout = Some(layout.clone());
        self.shared.broadcast(ServerMessage::Layout { layout });
    }

    pub fn set_volume_state(&self, value: f32, muted: bool) {
        let volume = VolumeState { value: value.clamp(0.0, 1.0), muted };
        {
            let mut state = self.shared.state();
            if state.volume == Some(volume) {
                return;
            }
            state.volume = Some(volume);
        }
        self.shared.broadcast(ServerMessage::Volume(volume));
    }

    pub fn clear_volume_state(&self) {
        if self.shared.state().volume.take().is_some() {
            self.shared.broadcast(ServerMessage::VolumeUnavailable);
        }
    }

    /// Apps launched, quit, hid or changed focus.
    pub fn apps_changed(&self) {
        self.shared.broadcast(ServerMessage::AppsChanged);
    }

    pub fn set_input_timing(&self, repeat_delay_ms: u32, repeat_interval_ms: u32, double_click_ms: u32) {
        let defaults = Tuning::default();
        let ms = |v: u32, lo: u64, hi: u64| Duration::from_millis((v as u64).clamp(lo, hi));
        self.shared.engine(Command::Tuning(Tuning {
            key_repeat_delay: ms(repeat_delay_ms, 100, 3_000),
            key_repeat_interval: ms(repeat_interval_ms, 15, 2_000),
            double_click_interval: ms(double_click_ms, 150, 5_000),
            ..defaults
        }));
    }

    /// Accessibility permission was granted or revoked.
    pub fn permission_changed(&self) {
        let permission = (self.shared.permission)();
        let config = self.shared.config();
        let sessions: Vec<_> = self.shared.state().sessions.values().map(|s| (s.tx.clone(), s.info.secure)).collect();
        for (tx, secure) in sessions {
            let features = Features { voice: config.voice_available, shell: config.allow_shell, secure, permission };
            let _ = tx.send(Outgoing::Message(ServerMessage::Features { features }));
        }
    }

    pub fn voice_update(&self, session: u64, update: VoiceUpdate) {
        // Serialize delivery with revocation, including a rapid pause/resume.
        let mut voices = self.shared.voices();
        let Some(accepted) = voices.accepted.get_mut(&session) else { return };
        if !accepted.contains(&update.request_id) {
            return;
        }
        if matches!(
            update.state,
            crate::protocol::VoiceState::Stopped
                | crate::protocol::VoiceState::Unavailable
                | crate::protocol::VoiceState::Error
        ) {
            accepted.retain(|id| *id != update.request_id);
            if accepted.is_empty() {
                voices.accepted.remove(&session);
            }
            if voices.active.get(&session) == Some(&update.request_id) {
                voices.active.remove(&session);
            }
        }
        if self.shared.should_stop(session) || !self.shared.config().voice_available {
            return;
        }
        self.shared.send_to(
            session,
            ServerMessage::Voice {
                id: update.request_id,
                state: update.state,
                text: update.text,
                is_final: update.is_final,
                message: update.message,
            },
        );
    }

    pub fn deck_json(&self) -> String {
        serde_json::to_string_pretty(&self.shared.state().deck).unwrap_or_default()
    }

    pub fn set_deck_json(&self, json: String) -> Result<(), HostError> {
        let deck: Deck = serde_json::from_str(&json).map_err(|e| HostError::new(format!("invalid deck: {e}")))?;
        server::save_deck(&self.shared, deck).map_err(|e| HostError::new(e.to_string()))
    }

    pub fn reset_deck(&self) {
        let _ = server::save_deck(&self.shared, Deck::default());
    }

    /// The CA certificate (DER) to install on devices for trusted HTTPS.
    pub fn ca_certificate(&self) -> Vec<u8> {
        self.shared
            .tls
            .read()
            .unwrap_or_else(|p| p.into_inner())
            .as_ref()
            .map(|id| id.ca_der.clone())
            .unwrap_or_default()
    }
}

impl Drop for CoreHost {
    fn drop(&mut self) {
        self.stop();
    }
}
