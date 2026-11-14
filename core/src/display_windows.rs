//! Native Windows desktop capture. COM, Direct3D and codecs stay on one MTA
//! worker. A synchronous generation gate invalidates queued starts and frames.
//! Pixels never cross the JSON desktop ABI and are never written to disk.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, OnceLock, Weak, mpsc};
use std::time::{Duration, Instant};

use sha2::{Digest, Sha256};
use windows::Win32::Foundation::HMODULE;
use windows::Win32::Graphics::Direct3D::D3D_DRIVER_TYPE_UNKNOWN;
use windows::Win32::Graphics::Direct3D11::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::Win32::Graphics::Dxgi::*;
use windows::Win32::System::Com::{COINIT_MULTITHREADED, CoInitializeEx, CoUninitialize};
use windows::core::Interface;

use crate::api::DisplayCommand;
use crate::display_capture_state::Gate;
use crate::display_pixels as pixels;
use crate::host::CoreHost;
use crate::protocol::DisplayInfo;

#[path = "display_windows/encoder.rs"]
mod encoder;
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
        let wait =
            active.as_ref().map(|c| c.next.saturating_duration_since(Instant::now()).min(Duration::from_millis(250)));
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
                        DisplayCommand::Start { generation, display_id, max_dimension, fps, bitrate, original } => {
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
                "Screen viewing stopped. Unlock or return to your Windows desktop, then start again.".into(),
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
        if Instant::now() < capture.next {
            continue;
        }
        capture.next = Instant::now() + Duration::from_micros(1_000_000 / capture.settings.fps.clamp(1, 60) as u64);
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
    software_only: bool,
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
    encoder: Option<Encoder>,
    size: (u32, u32),
    keyframe: bool,
    pending: bool,
    pending_since: Instant,
    next: Instant,
    began: Instant,
    first_frame_deadline: Instant,
    sent: bool,
    _timer: crate::input::windows::TimerResolution,
}
impl Capture {
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
                Some(Encoder::software(size, settings.fps, settings.bitrate)?)
            } else {
                Some(Encoder::new(size, settings.fps, settings.bitrate)?)
            };
            settings.software_only |= encoder.as_ref().is_some_and(|e| !e.hardware());
            Ok(Self {
                host,
                settings,
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
                encoder,
                size,
                keyframe: true,
                pending: false,
                pending_since: Instant::now(),
                next: Instant::now(),
                began: Instant::now(),
                first_frame_deadline: Instant::now() + Duration::from_secs(10),
                sent: false,
                _timer: timer,
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
        if let Some(desktop_changed) = update {
            if !self.pending {
                self.pending_since = Instant::now();
            }
            self.pending = true;
            self.background_dirty |= desktop_changed;
            self.picture_dirty = true;
        }
        if !gate.current(self.settings.generation)
            || !host.display_capture_active(self.settings.session, self.settings.generation)
            || !desktop_available()
        {
            return Ok(());
        }
        if self.settings.original {
            if self.pending && !self.raw.is_empty() {
                self.prepare_picture()?;
                let mut png = vec![];
                {
                    let mut encoder = png::Encoder::new(&mut png, self.size.0, self.size.1);
                    encoder.set_color(png::ColorType::Rgba);
                    encoder.set_depth(png::BitDepth::Eight);
                    encoder.set_compression(png::Compression::Fast);
                    let mut writer = encoder.write_header().map_err(|_| "The HD picture could not be prepared.")?;
                    writer.write_image_data(&self.picture).map_err(|_| "The HD picture could not be prepared.")?;
                }
                if png.len() > 16 * 1024 * 1024 {
                    return Err("This HD picture is too large. Choose Sharper text for this screen content.".into());
                }
                if host.display_frame(self.settings.session, self.settings.generation, png, timestamp, true) {
                    self.sent = true;
                    self.pending = false;
                }
            }
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
                Encoder::new(self.size, fps, bitrate)?
            } else {
                Encoder::software(self.size, fps, bitrate)?
            });
            self.settings.software_only |= self.encoder.as_ref().is_some_and(|e| !e.hardware());
            self.keyframe = true;
            self.pending = !self.picture.is_empty();
            self.pending_since = Instant::now();
            self.next = Instant::now();
            self.sent = false;
            self.first_frame_deadline = Instant::now() + Duration::from_secs(10);
        } else if let Some(encoder) = &self.encoder
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
        self.encoder = None;
        self.encoder = Some(Encoder::software(self.size, self.settings.fps, self.settings.bitrate)?);
        self.settings.software_only = true;
        self.keyframe = true;
        self.pending = true;
        self.pending_since = Instant::now();
        self.first_frame_deadline = Instant::now() + Duration::from_secs(10);
        self.sent = false;
        Ok(true)
    }
    fn encode_frame(&mut self, host: &CoreHost, gate: &Gate, timestamp: u64) -> Result<()> {
        let wants_input = (self.pending || self.keyframe) && !self.raw.is_empty();
        let ready = if let Some(encoder) = &mut self.encoder { wants_input && encoder.ready()? } else { false };
        if ready {
            // Rescale only the newest desktop when the encoder can accept
            // it. Pointer-only updates reuse the resized background, and
            // IDR retries reuse the already converted NV12 picture.
            self.prepare_picture()?;
            if self.video_dirty {
                pixels::nv12_into(&self.picture, self.size.0 as usize, self.size.1 as usize, &mut self.video_picture);
                self.video_dirty = false;
            }
            if let Some(encoder) = &mut self.encoder
                && encoder.input(&self.video_picture, timestamp, self.keyframe)?
            {
                // Keep requesting an IDR until it actually reaches the phone.
                // A driver may drop an accepted input under rate control.
                self.pending = false;
            }
        }
        let Some(encoder) = &mut self.encoder else { return Ok(()) };
        for (data, time, key) in encoder.output()? {
            if !gate.current(self.settings.generation) {
                break;
            }
            if host.display_frame(self.settings.session, self.settings.generation, data, time, key) {
                self.sent = true;
                if key {
                    self.keyframe = false;
                }
            } else {
                self.keyframe = true;
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
    /// None is idle; Some(false) updates only the pointer; Some(true) replaces
    /// the desktop background as well. This avoids rescaling for cursor moves.
    fn acquire(&mut self) -> windows::core::Result<Option<bool>> {
        // SAFETY: every acquired frame and mapped texture is released on all paths.
        unsafe {
            let duplication = self.duplication.as_ref().ok_or_else(windows::core::Error::from_thread)?;
            let mut info = DXGI_OUTDUPL_FRAME_INFO::default();
            let mut resource = None;
            match duplication.AcquireNextFrame(1, &mut info, &mut resource) {
                Err(e) if e.code() == DXGI_ERROR_WAIT_TIMEOUT => return Ok(None),
                result => result?,
            }
            let result = (|| {
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
                let desktop_changed = info.LastPresentTime != 0
                    || (info.LastMouseUpdateTime != 0 && !info.PointerPosition.Visible.as_bool())
                    || self.raw.is_empty();
                if desktop_changed {
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
                    let monitor = self.output.info();
                    if !pixels::texture_matches(
                        (monitor.width, monitor.height),
                        self.output.desc.Rotation.0 as u32,
                        (desc.Width, desc.Height),
                    ) {
                        // Some drivers resize a duplication before notifying
                        // ACCESS_LOST. Never stretch it using stale geometry.
                        return Err(windows::core::Error::from_hresult(DXGI_ERROR_ACCESS_LOST));
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
                        self.resizer = Some(pixels::Resizer::new(
                            (desc.Width as usize, desc.Height as usize),
                            self.size,
                            self.output.desc.Rotation.0 as u32,
                        ));
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
                }
                Ok(Some(desktop_changed))
            })();
            let release = duplication.ReleaseFrame();
            result.and_then(|changed| release.map(|_| changed))
        }
    }
}
