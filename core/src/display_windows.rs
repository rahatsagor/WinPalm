//! Native Windows desktop capture. COM, Direct3D and codecs stay on one MTA
//! worker. A synchronous generation gate invalidates queued starts and frames.
//! Pixels never cross the JSON desktop ABI and are never written to disk.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, OnceLock, Weak, mpsc};
use std::time::{Duration, Instant};

use sha2::{Digest, Sha256};
use windows::Win32::Foundation::{HMODULE, RECT};
use windows::Win32::Graphics::Direct3D::D3D_DRIVER_TYPE_UNKNOWN;
use windows::Win32::Graphics::Direct3D11::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::Win32::Graphics::Dxgi::*;
use windows::Win32::System::Com::{COINIT_MULTITHREADED, CoInitializeEx, CoUninitialize};
use windows::core::Interface;

use crate::api::{DisplayCommand, DisplayProfile};
use crate::display_capture_state::{CaptureBucket, Gate, IdleRefinement};
use crate::display_pixels as pixels;
use crate::host::CoreHost;
use crate::protocol::DisplayInfo;

#[path = "display_windows/encoder.rs"]
mod encoder;
mod power;
use encoder::Encoder;
pub(crate) use encoder::self_test;

type Result<T> = std::result::Result<T, String>;

struct Job {
    host: Weak<CoreHost>,
    session: u64,
    command: DisplayCommand,
}
struct Service {
    tx: mpsc::SyncSender<Job>,
    gate: Arc<Gate>,
    refresh: Arc<AtomicBool>,
}
static SERVICE: OnceLock<Service> = OnceLock::new();

pub(crate) fn command(host: &Arc<CoreHost>, session: u64, command: DisplayCommand) {
    let service = SERVICE.get_or_init(|| {
        let (tx, rx) = mpsc::sync_channel(32);
        let gate = Arc::new(Gate::default());
        let worker_gate = gate.clone();
        let refresh = Arc::new(AtomicBool::new(false));
        let worker_refresh = refresh.clone();
        // A failed spawn disconnects the queue; every request then gets a useful error.
        let _ = std::thread::Builder::new()
            .name("WinPalm display".into())
            .spawn(move || worker(rx, worker_gate, worker_refresh));
        Service { tx, gate, refresh }
    });
    match &command {
        DisplayCommand::Start { generation, .. } => service.gate.start(*generation),
        DisplayCommand::Stop { generation } => service.gate.stop(*generation),
        _ => {}
    }
    if let Err(error) = service.tx.try_send(Job { host: Arc::downgrade(host), session, command }) {
        let job = match error {
            mpsc::TrySendError::Full(j) | mpsc::TrySendError::Disconnected(j) => j,
        };
        match job.command {
            DisplayCommand::Start { generation, .. } => {
                service.gate.stop(generation);
                host.display_failed(
                    session,
                    generation,
                    "Windows is still stopping the previous capture. Try again in a moment.".into(),
                );
            }
            DisplayCommand::List { request_id } => host.display_sources(
                session,
                request_id,
                vec![],
                Some("Windows is still refreshing its displays. Try again in a moment.".into()),
            ),
            _ => {}
        }
    }
}

pub(crate) fn displays_changed() {
    if let Some(service) = SERVICE.get() {
        // Coalesced separately from commands, so a full command queue cannot
        // lose a topology change. The active worker wakes within 250 ms.
        service.refresh.store(true, Ordering::Release);
    }
}

fn worker(rx: mpsc::Receiver<Job>, gate: Arc<Gate>, refresh: Arc<AtomicBool>) {
    // SAFETY: all COM objects below are created and released on this thread.
    if unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) }.is_err() {
        return;
    }
    let mut active: Option<Capture> = None;
    loop {
        let wait = active.as_ref().map(|c| c.next_wake(Instant::now()));
        let job = match wait {
            Some(wait) => rx.recv_timeout(wait),
            None => rx.recv().map_err(|_| mpsc::RecvTimeoutError::Disconnected),
        };
        match job {
            Ok(job) => {
                if let Some(host) = job.host.upgrade() {
                    match job.command {
                        DisplayCommand::List { request_id } => {
                            let (displays, message) = match outputs() {
                                Ok(v) => (v.iter().map(Output::info).collect(), None),
                                Err(e) => (vec![], Some(e)),
                            };
                            host.display_sources(job.session, request_id, displays, message);
                        }
                        DisplayCommand::Start {
                            generation,
                            display_id,
                            max_dimension,
                            fps,
                            bitrate,
                            original,
                            patches,
                            profile,
                        } => {
                            if !gate.current(generation) || !host.display_capture_active(job.session, generation) {
                                continue;
                            }
                            active = None;
                            let settings = Settings {
                                session: job.session,
                                generation,
                                display: display_id,
                                maximum: max_dimension,
                                fps,
                                bitrate,
                                original,
                                patches,
                                profile,
                                software_only: false,
                            };
                            match Capture::new(job.host, settings) {
                                Ok(capture) if gate.current(generation) => active = Some(capture),
                                Ok(_) => {}
                                Err(message) => host.display_failed(job.session, generation, message),
                            }
                        }
                        DisplayCommand::Stop { generation } => {
                            if active.as_ref().is_some_and(|c| c.settings.generation == generation) {
                                active = None;
                            }
                        }
                        DisplayCommand::Configure { generation, fps, bitrate } => {
                            if let Some(capture) = active
                                .as_mut()
                                .filter(|c| c.settings.generation == generation && gate.current(generation))
                                && let Err(message) = capture.configure(fps, bitrate)
                            {
                                host.display_failed(job.session, generation, message);
                                active = None;
                            }
                        }
                        DisplayCommand::KeyFrame { generation } => {
                            if let Some(capture) = active.as_mut().filter(|c| c.settings.generation == generation) {
                                capture.keyframe = true;
                            }
                        }
                        DisplayCommand::Focus { generation } => {
                            if let Some(capture) = active
                                .as_ref()
                                .filter(|c| c.settings.generation == generation && gate.current(generation))
                                && desktop_available()
                                && crate::input::windows::has_post_permission()
                            {
                                let Some(output) = outputs()
                                    .ok()
                                    .and_then(|v| v.into_iter().find(|o| o.id == capture.settings.display))
                                else {
                                    continue;
                                };
                                let r = output.desc.DesktopCoordinates;
                                // SAFETY: absolute coordinates in this selected monitor's desktop rectangle.
                                unsafe {
                                    windows_sys::Win32::UI::WindowsAndMessaging::SetCursorPos(
                                        r.left + (r.right - r.left) / 2,
                                        r.top + (r.bottom - r.top) / 2,
                                    );
                                }
                            }
                        }
                    }
                }
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => break,
            Err(mpsc::RecvTimeoutError::Timeout) => {}
        }
        let Some(capture) = active.as_mut() else {
            continue;
        };
        let Some(host) = capture.host.upgrade() else {
            active = None;
            continue;
        };
        if !gate.current(capture.settings.generation)
            || !host.display_capture_active(capture.settings.session, capture.settings.generation)
        {
            active = None;
            continue;
        }
        if !desktop_available() {
            host.display_failed(
                capture.settings.session,
                capture.settings.generation,
                "Screen mirroring stopped. Unlock or return to your Windows desktop, then start again.".into(),
            );
            active = None;
            continue;
        }
        if refresh.swap(false, Ordering::AcqRel)
            && let Err(message) = capture.refresh_output()
        {
            host.display_failed(capture.settings.session, capture.settings.generation, message);
            active = None;
            continue;
        }
        let now = Instant::now();
        if !capture.should_tick(now) {
            continue;
        }
        if now >= capture.next {
            capture.next = now + Duration::from_micros(1_000_000 / capture.settings.fps.clamp(1, 60) as u64);
        }
        if let Err(message) = capture.tick(&host, &gate) {
            host.display_failed(capture.settings.session, capture.settings.generation, message);
            active = None;
        }
    }
    drop(active);
    // SAFETY: matches this thread's successful initialization.
    unsafe {
        CoUninitialize();
    }
}

fn desktop_available() -> bool {
    use windows_sys::Win32::System::StationsAndDesktops::*;
    // SAFETY: inspect the current input desktop, never switch to another one.
    unsafe {
        let desktop = OpenInputDesktop(0, 0, DESKTOP_READOBJECTS | DESKTOP_SWITCHDESKTOP);
        if desktop.is_null() {
            return false;
        }
        let mut name = [0u16; 256];
        let mut needed = 0;
        let ok = GetUserObjectInformationW(
            desktop,
            UOI_NAME,
            name.as_mut_ptr().cast(),
            std::mem::size_of_val(&name) as u32,
            &mut needed,
        ) != 0;
        CloseDesktop(desktop);
        ok && String::from_utf16_lossy(&name[..name.iter().position(|c| *c == 0).unwrap_or(name.len())])
            .eq_ignore_ascii_case("Default")
    }
}

struct Output {
    id: String,
    name: String,
    adapter: IDXGIAdapter1,
    output: IDXGIOutput1,
    desc: DXGI_OUTPUT_DESC,
}
impl Output {
    fn info(&self) -> DisplayInfo {
        let r = self.desc.DesktopCoordinates;
        let name = self.name.clone();
        DisplayInfo {
            id: self.id.clone(),
            name: if r.left == 0 && r.top == 0 { format!("{name} · Main display") } else { name },
            width: (r.right - r.left) as u32,
            height: (r.bottom - r.top) as u32,
            main: r.left == 0 && r.top == 0,
        }
    }
}
fn wide(s: &[u16]) -> String {
    String::from_utf16_lossy(&s[..s.iter().position(|c| *c == 0).unwrap_or(s.len())])
}
fn outputs() -> Result<Vec<Output>> {
    if !desktop_available() {
        return Err("Unlock or wake the PC, then refresh its displays.".into());
    }
    // SAFETY: DXGI returns owned COM references; all are confined to the worker.
    unsafe {
        let factory: IDXGIFactory1 =
            CreateDXGIFactory1().map_err(|_| "Windows could not list its graphics adapters.")?;
        let mut result = vec![];
        for a in 0..32 {
            let Ok(adapter) = factory.EnumAdapters1(a) else {
                break;
            };
            for o in 0..32 {
                let Ok(output) = adapter.EnumOutputs(o) else {
                    break;
                };
                let Ok(desc) = output.GetDesc() else {
                    continue;
                };
                if !desc.AttachedToDesktop.as_bool() {
                    continue;
                }
                let Ok(output) = output.cast::<IDXGIOutput1>() else {
                    continue;
                };
                // Monitor interface identity survives resolution/rotation changes.
                // Hash it so source IDs expose no monitor serial or registry path.
                use windows_sys::Win32::Graphics::Gdi::{DISPLAY_DEVICEW, EnumDisplayDevicesW};
                let mut monitor =
                    DISPLAY_DEVICEW { cb: std::mem::size_of::<DISPLAY_DEVICEW>() as u32, ..Default::default() };
                let enumerated = EnumDisplayDevicesW(desc.DeviceName.as_ptr(), 0, &mut monitor, 1) != 0;
                let identity = if enumerated && monitor.DeviceID[0] != 0 {
                    wide(&monitor.DeviceID)
                } else {
                    // Bind virtual/fallback sources to this monitor handle as
                    // well as their logical name, so a reused index is insufficient.
                    format!("{}:{:x}", wide(&desc.DeviceName), desc.Monitor.0 as usize)
                };
                let short = wide(&desc.DeviceName).replace("\\\\.\\", "");
                let friendly = wide(&monitor.DeviceString);
                let name = if enumerated && !friendly.is_empty() { format!("{friendly} · {short}") } else { short };
                let digest = Sha256::digest(identity.as_bytes());
                let id = digest[..16].iter().map(|b| format!("{b:02x}")).collect::<String>();
                result.push(Output { id, name, adapter: adapter.clone(), output, desc });
            }
        }
        result.sort_by_key(|o| (!o.info().main, o.info().name));
        Ok(result)
    }
}

#[derive(Clone)]
struct Settings {
    session: u64,
    generation: u64,
    display: String,
    maximum: u32,
    fps: u32,
    bitrate: u32,
    original: bool,
    patches: bool,
    profile: DisplayProfile,
    software_only: bool,
}

enum AcquireUpdate {
    Idle,
    CursorOnly { dirty: Option<pixels::PixelRect> },
    Desktop { dirty: Option<pixels::PixelRect> },
}

struct Capture {
    host: Weak<CoreHost>,
    settings: Settings,
    output: Output,
    device: ID3D11Device,
    context: ID3D11DeviceContext,
    duplication: Option<IDXGIOutputDuplication>,
    staging: Option<ID3D11Texture2D>,
    texture_size: (u32, u32),
    raw: Vec<u8>,
    resizer: Option<pixels::Resizer>,
    background: Vec<u8>,
    background_dirty: bool,
    picture: Vec<u8>,
    picture_dirty: bool,
    video_picture: Vec<u8>,
    video_dirty: bool,
    cursor: Vec<u8>,
    cursor_info: DXGI_OUTDUPL_POINTER_SHAPE_INFO,
    pointer: DXGI_OUTDUPL_POINTER_POSITION,
    last_cursor_rect: Option<pixels::PixelRect>,
    dirty_buf: Vec<RECT>,
    move_buf: Vec<DXGI_OUTDUPL_MOVE_RECT>,
    original_dirty_rect: Option<pixels::PixelRect>,
    original_needs_full: bool,
    original_payload: Option<(Vec<u8>, bool)>,
    original_retry_at: Option<Instant>,
    encoder: Option<Encoder>,
    bucket: CaptureBucket,
    refinement: IdleRefinement,
    size: (u32, u32),
    keyframe: bool,
    pending: bool,
    pending_since: Instant,
    next: Instant,
    began: Instant,
    first_frame_deadline: Instant,
    sent: bool,
    _timer: crate::input::windows::TimerResolution,
    _power: power::KeepAwake,
}
impl Capture {
    fn next_wake(&self, now: Instant) -> Duration {
        let mut wake = self.next.saturating_duration_since(now).min(Duration::from_millis(250));
        if let Some(retry) = self.original_retry_at {
            wake = wake.min(retry.saturating_duration_since(now));
        }
        if !self.settings.original {
            if let Some(due) = self.refinement.next_due() {
                wake = wake.min(due.saturating_duration_since(now));
            }
            if self.pending && !self.keyframe {
                wake = wake.min(self.bucket.wait_duration(now));
            }
        }
        wake
    }
    fn should_tick(&self, now: Instant) -> bool {
        now >= self.next
            || self.original_retry_at.is_some_and(|retry| now >= retry)
            || (!self.settings.original && self.refinement.is_due(now))
    }
    fn new(host: Weak<CoreHost>, mut settings: Settings) -> Result<Self> {
        let mut timer = crate::input::windows::TimerResolution::default();
        timer.want(!settings.original);
        let output = outputs()?
            .into_iter()
            .find(|o| o.id == settings.display)
            .ok_or("The selected display was disconnected. Refresh and choose another.")?;
        let info = output.info();
        let size = pixels::size(info.width, info.height, settings.maximum, settings.original)
            .ok_or("This display's resolution is unsupported.")?;
        // SAFETY: a device for the same adapter as the duplicated output.
        unsafe {
            let (mut device, mut context) = (None, None);
            D3D11CreateDevice(
                &output.adapter,
                D3D_DRIVER_TYPE_UNKNOWN,
                HMODULE::default(),
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                None,
                D3D11_SDK_VERSION,
                Some(&mut device),
                None,
                Some(&mut context),
            )
            .map_err(|_| "Windows could not prepare desktop capture. Check your graphics driver.")?;
            let device = device.ok_or("Windows did not create a capture device.")?;
            let context = context.ok_or("Windows did not create a capture context.")?;
            let duplication = output.output.DuplicateOutput(&device).map_err(|_| "Windows could not capture this display. Close other capture sessions or update the graphics driver, then try again.")?;
            let encoder = if settings.original {
                None
            } else if settings.software_only {
                Some(Encoder::software(size, settings.fps, settings.bitrate, settings.profile)?)
            } else {
                Some(Encoder::new(size, settings.fps, settings.bitrate, settings.profile)?)
            };
            settings.software_only |= encoder.as_ref().is_some_and(|e| !e.hardware());
            let now = Instant::now();
            Ok(Self {
                host,
                settings: settings.clone(),
                output,
                device,
                context,
                duplication: Some(duplication),
                staging: None,
                texture_size: (0, 0),
                raw: vec![],
                resizer: None,
                background: vec![],
                background_dirty: true,
                picture: vec![],
                picture_dirty: true,
                video_picture: vec![],
                video_dirty: true,
                cursor: vec![],
                cursor_info: Default::default(),
                pointer: Default::default(),
                last_cursor_rect: None,
                dirty_buf: vec![],
                move_buf: vec![],
                original_dirty_rect: None,
                original_needs_full: true,
                original_payload: None,
                original_retry_at: None,
                encoder,
                bucket: CaptureBucket::new(settings.fps, now),
                refinement: IdleRefinement::default(),
                size,
                keyframe: true,
                pending: false,
                pending_since: now,
                next: now,
                began: now,
                first_frame_deadline: now + Duration::from_secs(10),
                sent: false,
                _timer: timer,
                _power: power::KeepAwake::new()?,
            })
        }
    }
    fn tick(&mut self, host: &CoreHost, gate: &Gate) -> Result<()> {
        let update = match self.acquire() {
            Ok(update) => update,
            Err(error) if error.code() == DXGI_ERROR_ACCESS_LOST => {
                self.rebuild()?;
                return Ok(());
            }
            Err(_) => return Err("Windows screen capture stopped. Wake the display and start viewing again.".into()),
        };
        let timestamp = self.began.elapsed().as_micros() as u64;
        match update {
            AcquireUpdate::Idle => {}
            AcquireUpdate::CursorOnly { dirty } => {
                if !self.pending {
                    self.pending_since = Instant::now();
                }
                self.pending = true;
                self.picture_dirty = true;
                self.record_damage(dirty);
                self.refinement.cancel();
                if let Some(encoder) = &mut self.encoder {
                    encoder.set_wide_rate_limit(false, self.settings.bitrate);
                }
            }
            AcquireUpdate::Desktop { dirty } => {
                if !self.pending {
                    self.pending_since = Instant::now();
                }
                self.pending = true;
                self.background_dirty = true;
                self.picture_dirty = true;
                self.record_damage(dirty);
                self.refinement.cancel();
                if let Some(encoder) = &mut self.encoder {
                    encoder.set_wide_rate_limit(false, self.settings.bitrate);
                }
            }
        }
        if !gate.current(self.settings.generation)
            || !host.display_capture_active(self.settings.session, self.settings.generation)
            || !desktop_available()
        {
            return Ok(());
        }
        if self.settings.original {
            self.tick_original(host, timestamp)?;
        } else if let Err(message) = self.encode_frame(host, gate, timestamp) {
            if !self.fallback_encoder()? {
                return Err(message);
            }
            return Ok(());
        }
        if !self.settings.original && self.pending && self.pending_since.elapsed() > Duration::from_secs(5) {
            if self.fallback_encoder()? {
                return Ok(());
            }
            return Err(
                "The Windows video encoder stopped accepting pictures. Start viewing again or try HD text.".into()
            );
        }
        if !self.sent && Instant::now() > self.first_frame_deadline {
            return Err("Windows could not produce a screen picture. Check the display and graphics/media drivers, or try HD text.".into());
        }
        Ok(())
    }
    fn record_damage(&mut self, dirty: Option<pixels::PixelRect>) {
        if !self.settings.original {
            return;
        }
        // New damage invalidates any cached patch so the next send merges it.
        self.original_payload = None;
        if !self.sent || self.original_needs_full {
            self.original_needs_full = true;
            self.original_dirty_rect = None;
            return;
        }
        match dirty {
            Some(rect) => {
                self.original_dirty_rect = Some(self.original_dirty_rect.map_or(rect, |existing| existing.union(rect)));
            }
            None => {
                self.original_needs_full = true;
                self.original_dirty_rect = None;
            }
        }
    }
    fn encode_png(data: &[u8], width: u32, height: u32) -> Result<Vec<u8>> {
        let mut png = vec![];
        let mut encoder = png::Encoder::new(&mut png, width, height);
        encoder.set_color(png::ColorType::Rgba);
        encoder.set_depth(png::BitDepth::Eight);
        encoder.set_compression(png::Compression::Fast);
        let mut writer = encoder.write_header().map_err(|_| "The HD picture could not be prepared.")?;
        writer.write_image_data(data).map_err(|_| "The HD picture could not be prepared.")?;
        drop(writer);
        Ok(png)
    }
    fn tick_original(&mut self, host: &CoreHost, timestamp: u64) -> Result<()> {
        let now = Instant::now();
        if self.original_retry_at.is_some_and(|retry| now < retry) {
            return Ok(());
        }
        self.original_retry_at = None;
        if self.original_payload.is_none() {
            if !self.pending || self.raw.is_empty() {
                return Ok(());
            }
            self.prepare_picture()?;
            let (send_full, crop) = if !self.sent || self.original_needs_full || !self.settings.patches {
                (true, None)
            } else if let Some(dirty) = self.original_dirty_rect {
                if dirty.exceeds_patch_threshold(self.size) { (true, None) } else { (false, Some(dirty)) }
            } else {
                self.pending = false;
                return Ok(());
            };
            let payload = if send_full {
                Self::encode_png(&self.picture, self.size.0, self.size.1)?
            } else if let Some(rect) = crop {
                let cropped = pixels::crop_rgba(&self.picture, self.size.0, rect);
                let png = Self::encode_png(&cropped, rect.width, rect.height)?;
                pixels::patch_payload(rect, self.size, &png)
                    .ok_or("The HD picture could not be prepared. Try Sharper text or start viewing again.")?
            } else {
                return Ok(());
            };
            if payload.len() > 16 * 1024 * 1024 {
                return Err("This HD picture is too large. Choose Sharper text for this screen content.".into());
            }
            self.original_payload = Some((payload, send_full));
        }
        if let Some((payload, send_full)) = self.original_payload.take() {
            match host.try_display_frame(self.settings.session, self.settings.generation, payload, timestamp, send_full)
            {
                Ok(()) => {
                    self.sent = true;
                    self.pending = false;
                    self.original_needs_full = false;
                    self.original_dirty_rect = None;
                    self.original_retry_at = None;
                }
                Err(payload) => {
                    // Reuse the encoded patch while waiting for transport capacity.
                    self.original_payload = Some((payload, send_full));
                    self.original_retry_at = Some(now + Duration::from_millis(60));
                }
            }
        }
        Ok(())
    }
    fn rebuild(&mut self) -> Result<()> {
        // Only one duplication per output is allowed in a process. Release
        // resources first, and preserve the RTP clock across mode changes.
        self.duplication = None;
        self.staging = None;
        self.encoder = None;
        let mut replacement = Self::new(self.host.clone(), self.settings.clone())?;
        replacement.began = self.began;
        *self = replacement;
        Ok(())
    }
    fn refresh_output(&mut self) -> Result<()> {
        let output = outputs()?
            .into_iter()
            .find(|o| o.id == self.settings.display)
            .ok_or("The selected display was disconnected. Refresh and choose another.")?;
        if output.desc != self.output.desc {
            self.rebuild()?;
        }
        Ok(())
    }
    fn configure(&mut self, fps: u32, bitrate: u32) -> Result<()> {
        if self.settings.original {
            return Ok(());
        }
        let now = Instant::now();
        let fps = fps.clamp(1, 60);
        let fps_changed = fps != self.settings.fps;
        self.settings.fps = fps;
        self.settings.bitrate = bitrate;
        if fps_changed {
            // Media types and sample duration must agree with capture cadence.
            // Rebuild only on a frame-rate transition, not every BWE update.
            let hardware = self.encoder.as_ref().is_some_and(Encoder::hardware);
            self.encoder = None;
            self.encoder = Some(if hardware {
                Encoder::new(self.size, fps, bitrate, self.settings.profile)?
            } else {
                Encoder::software(self.size, fps, bitrate, self.settings.profile)?
            });
            self.settings.software_only |= self.encoder.as_ref().is_some_and(|e| !e.hardware());
            self.bucket.reset(fps, now);
            self.refinement.cancel();
            self.keyframe = true;
            self.pending = !self.picture.is_empty();
            self.pending_since = now;
            self.next = now;
            self.sent = false;
            self.first_frame_deadline = now + Duration::from_secs(10);
        } else if let Some(encoder) = &mut self.encoder
            && let Err(message) = encoder.bitrate(bitrate)
            && !self.fallback_encoder()?
        {
            return Err(message);
        }
        Ok(())
    }
    fn fallback_encoder(&mut self) -> Result<bool> {
        if !self.encoder.as_ref().is_some_and(Encoder::hardware) {
            return Ok(false);
        }
        // Some hardware drivers accept the media type but reject system-memory
        // samples or stall on their first picture. Recover within the same RTP
        // stream using a fresh software encoder and a fresh IDR.
        let now = Instant::now();
        self.encoder = None;
        self.encoder =
            Some(Encoder::software(self.size, self.settings.fps, self.settings.bitrate, self.settings.profile)?);
        self.settings.software_only = true;
        self.bucket.reset(self.settings.fps, now);
        self.refinement.cancel();
        self.keyframe = true;
        self.pending = true;
        self.pending_since = now;
        self.first_frame_deadline = now + Duration::from_secs(10);
        self.sent = false;
        Ok(true)
    }
    fn encode_frame(&mut self, host: &CoreHost, gate: &Gate, timestamp: u64) -> Result<()> {
        let now = Instant::now();
        let wants_normal = (self.pending || self.keyframe) && !self.raw.is_empty();
        let wants_refinement = !wants_normal && self.sent && !self.raw.is_empty() && self.refinement.is_due(now);
        let can_feed = if wants_normal { self.keyframe || self.bucket.ready(now) } else { wants_refinement };
        let ready = if let Some(encoder) = &mut self.encoder { can_feed && encoder.ready()? } else { false };
        if ready {
            // Rescale only the newest desktop when the encoder can accept
            // it. Pointer-only updates reuse the resized background, and
            // IDR/refinement retries reuse the already converted NV12 picture.
            self.prepare_picture()?;
            if self.video_dirty {
                pixels::nv12_into(&self.picture, self.size.0 as usize, self.size.1 as usize, &mut self.video_picture);
                self.video_dirty = false;
            }
            if let Some(encoder) = &mut self.encoder {
                if wants_refinement {
                    encoder.set_wide_rate_limit(true, self.settings.bitrate);
                    if encoder.input(&self.video_picture, timestamp, false)? {
                        self.refinement.mark_started();
                    }
                } else {
                    encoder.set_wide_rate_limit(false, self.settings.bitrate);
                    if encoder.input(&self.video_picture, timestamp, self.keyframe)? {
                        // Keep requesting an IDR until it actually reaches the phone.
                        // A driver may drop an accepted input under rate control.
                        self.pending = false;
                        self.bucket.spend(now);
                    }
                }
            }
        }
        let frames = {
            let Some(encoder) = &mut self.encoder else { return Ok(()) };
            encoder.output()?
        };
        let small_frame_size = (self.settings.bitrate as usize / 8 / 4).max(16 * 1024);
        for (data, time, key) in frames {
            if !gate.current(self.settings.generation) {
                break;
            }
            let encoded_len = data.len();
            let was_refinement = self.refinement.in_flight;
            if host.display_frame(self.settings.session, self.settings.generation, data, time, key) {
                self.sent = true;
                if key {
                    self.keyframe = false;
                }
                let finished = Instant::now();
                if was_refinement {
                    if self.refinement.should_continue(Some(encoded_len), small_frame_size) {
                        self.refinement.schedule_next(finished, self.settings.fps);
                    } else if let Some(encoder) = &mut self.encoder {
                        encoder.set_wide_rate_limit(false, self.settings.bitrate);
                    }
                } else {
                    if let Some(encoder) = &mut self.encoder {
                        encoder.set_wide_rate_limit(false, self.settings.bitrate);
                    }
                    self.refinement.schedule_initial(finished);
                }
            } else {
                self.keyframe = true;
                self.refinement.cancel();
                if let Some(encoder) = &mut self.encoder {
                    encoder.set_wide_rate_limit(false, self.settings.bitrate);
                }
            }
        }
        Ok(())
    }
    fn prepare_picture(&mut self) -> Result<()> {
        if !self.picture_dirty {
            return Ok(());
        }
        if self.background_dirty {
            self.resizer.as_ref().ok_or("Windows could not prepare the display geometry.")?.rgba(
                &self.raw,
                self.texture_size.0 as usize * 4,
                &mut self.background,
            );
            self.background_dirty = false;
        }
        self.picture.clone_from(&self.background);
        if self.pointer.Visible.as_bool() {
            let info = self.output.info();
            pixels::cursor(
                &mut self.picture,
                self.size,
                (info.width, info.height),
                pixels::Cursor {
                    data: &self.cursor,
                    width: self.cursor_info.Width as usize,
                    height: self.cursor_info.Height as usize,
                    pitch: self.cursor_info.Pitch as usize,
                    kind: self.cursor_info.Type,
                    x: self.pointer.Position.x,
                    y: self.pointer.Position.y,
                },
            );
        }
        self.picture_dirty = false;
        self.video_dirty = true;
        Ok(())
    }
    fn acquire(&mut self) -> windows::core::Result<AcquireUpdate> {
        // SAFETY: every acquired frame and mapped texture is released on all paths.
        unsafe {
            let duplication = self.duplication.as_ref().ok_or_else(windows::core::Error::from_thread)?;
            let mut info = DXGI_OUTDUPL_FRAME_INFO::default();
            let mut resource = None;
            match duplication.AcquireNextFrame(1, &mut info, &mut resource) {
                Err(e) if e.code() == DXGI_ERROR_WAIT_TIMEOUT => return Ok(AcquireUpdate::Idle),
                result => result?,
            }
            let result = (|| {
                if info.LastPresentTime == 0 && info.LastMouseUpdateTime == 0 && !self.raw.is_empty() {
                    return Ok(AcquireUpdate::Idle);
                }
                let previous_cursor = self.last_cursor_rect;
                if info.LastMouseUpdateTime != 0 {
                    self.pointer = info.PointerPosition;
                }
                if info.PointerShapeBufferSize > 0 && info.PointerShapeBufferSize <= 1024 * 1024 {
                    self.cursor.resize(info.PointerShapeBufferSize as usize, 0);
                    let mut required = 0;
                    duplication.GetFramePointerShape(
                        info.PointerShapeBufferSize,
                        self.cursor.as_mut_ptr().cast(),
                        &mut required,
                        &mut self.cursor_info,
                    )?;
                }
                let monitor = self.output.info();
                let next_cursor = if self.pointer.Visible.as_bool() {
                    pixels::cursor_rect(
                        self.size,
                        (monitor.width, monitor.height),
                        self.pointer.Position.x,
                        self.pointer.Position.y,
                        self.cursor_info.Width as usize,
                        self.cursor_info.Height as usize,
                        self.cursor_info.Type,
                    )
                } else {
                    None
                };
                self.last_cursor_rect = next_cursor;
                let cursor_damage = match (previous_cursor, next_cursor) {
                    (Some(a), Some(b)) => Some(a.union(b)),
                    (Some(a), None) | (None, Some(a)) => Some(a),
                    (None, None) => None,
                };
                let desktop_changed = info.LastPresentTime != 0
                    || (info.LastMouseUpdateTime != 0 && !info.PointerPosition.Visible.as_bool())
                    || self.raw.is_empty();
                if !desktop_changed {
                    return Ok(match cursor_damage {
                        Some(dirty) => AcquireUpdate::CursorOnly { dirty: Some(dirty) },
                        None => AcquireUpdate::Idle,
                    });
                }
                let had_raw = !self.raw.is_empty();
                let texture: ID3D11Texture2D = resource.ok_or_else(windows::core::Error::from_thread)?.cast()?;
                let mut desc = D3D11_TEXTURE2D_DESC::default();
                texture.GetDesc(&mut desc);
                if desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM
                    || desc.Width == 0
                    || desc.Height == 0
                    || u64::from(desc.Width) * u64::from(desc.Height) > 67_108_864
                {
                    return Err(windows::core::Error::from_hresult(windows::core::HRESULT(0x80004005u32 as i32)));
                }
                let rotation = self.output.desc.Rotation.0 as u32;
                if !pixels::texture_matches((monitor.width, monitor.height), rotation, (desc.Width, desc.Height)) {
                    // Some drivers resize a duplication before notifying
                    // ACCESS_LOST. Never stretch it using stale geometry.
                    return Err(windows::core::Error::from_hresult(DXGI_ERROR_ACCESS_LOST));
                }
                let mut dirty: Option<pixels::PixelRect> = None;
                let collect_patches = self.settings.original
                    && self.settings.patches
                    && had_raw
                    && self.texture_size == (desc.Width, desc.Height)
                    && info.TotalMetadataBufferSize > 0
                    && info.TotalMetadataBufferSize <= 4 * 1024 * 1024;
                if collect_patches {
                    let total = info.TotalMetadataBufferSize as usize;
                    let move_cap = total / std::mem::size_of::<DXGI_OUTDUPL_MOVE_RECT>() + 1;
                    self.move_buf.resize(move_cap, DXGI_OUTDUPL_MOVE_RECT::default());
                    let mut move_bytes = 0u32;
                    let moves_ok = duplication
                        .GetFrameMoveRects(
                            (self.move_buf.len() * std::mem::size_of::<DXGI_OUTDUPL_MOVE_RECT>()) as u32,
                            self.move_buf.as_mut_ptr(),
                            &mut move_bytes,
                        )
                        .is_ok();
                    let dirty_cap = total / std::mem::size_of::<RECT>() + 1;
                    self.dirty_buf.resize(dirty_cap, RECT::default());
                    let mut dirty_bytes = 0u32;
                    let dirty_ok = duplication
                        .GetFrameDirtyRects(
                            (self.dirty_buf.len() * std::mem::size_of::<RECT>()) as u32,
                            self.dirty_buf.as_mut_ptr(),
                            &mut dirty_bytes,
                        )
                        .is_ok();
                    if moves_ok && dirty_ok {
                        let move_count = move_bytes as usize / std::mem::size_of::<DXGI_OUTDUPL_MOVE_RECT>();
                        let dirty_count = dirty_bytes as usize / std::mem::size_of::<RECT>();
                        let mut union = cursor_damage;
                        for mv in &self.move_buf[..move_count.min(self.move_buf.len())] {
                            let d = mv.DestinationRect;
                            let w = d.right.saturating_sub(d.left);
                            let h = d.bottom.saturating_sub(d.top);
                            for rect in [
                                (d.left, d.top, d.right, d.bottom),
                                (
                                    mv.SourcePoint.x,
                                    mv.SourcePoint.y,
                                    mv.SourcePoint.x.saturating_add(w),
                                    mv.SourcePoint.y.saturating_add(h),
                                ),
                            ] {
                                if let Some(mapped) =
                                    pixels::transform_texture_rect(rect, (desc.Width, desc.Height), rotation, self.size)
                                {
                                    union = Some(union.map_or(mapped, |u| u.union(mapped)));
                                }
                            }
                        }
                        for r in &self.dirty_buf[..dirty_count.min(self.dirty_buf.len())] {
                            if let Some(mapped) = pixels::transform_texture_rect(
                                (r.left, r.top, r.right, r.bottom),
                                (desc.Width, desc.Height),
                                rotation,
                                self.size,
                            ) {
                                union = Some(union.map_or(mapped, |u| u.union(mapped)));
                            }
                        }
                        dirty = union;
                    }
                }
                if self.texture_size != (desc.Width, desc.Height) {
                    desc.Usage = D3D11_USAGE_STAGING;
                    desc.BindFlags = 0;
                    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
                    desc.MiscFlags = 0;
                    let mut staging = None;
                    self.device.CreateTexture2D(&desc, None, Some(&mut staging))?;
                    self.staging = staging;
                    self.texture_size = (desc.Width, desc.Height);
                    self.resizer =
                        Some(pixels::Resizer::new((desc.Width as usize, desc.Height as usize), self.size, rotation));
                }
                let staging = self.staging.as_ref().ok_or_else(windows::core::Error::from_thread)?;
                self.context.CopyResource(staging, &texture);
                let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
                self.context.Map(staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped))?;
                if mapped.pData.is_null() || mapped.RowPitch < desc.Width * 4 {
                    self.context.Unmap(staging, 0);
                    return Err(windows::core::Error::from_hresult(windows::core::HRESULT(0x80004005u32 as i32)));
                }
                self.raw.resize(desc.Width as usize * desc.Height as usize * 4, 0);
                for y in 0..desc.Height as usize {
                    let row = std::slice::from_raw_parts(
                        mapped.pData.cast::<u8>().add(y * mapped.RowPitch as usize),
                        desc.Width as usize * 4,
                    );
                    self.raw[y * desc.Width as usize * 4..(y + 1) * desc.Width as usize * 4].copy_from_slice(row);
                }
                self.context.Unmap(staging, 0);
                Ok(AcquireUpdate::Desktop { dirty })
            })();
            let release = duplication.ReleaseFrame();
            result.and_then(|changed| release.map(|_| changed))
        }
    }
}
