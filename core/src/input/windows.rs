//! Native input on the interactive Windows desktop. No hooks or polling thread.
use super::{EventSink, GestureKind, Synth, momentum_phase, scroll_phase};
use crate::keycodes::{self, flags};
use crate::protocol::{MediaKey, MouseButton};
use std::collections::HashSet;
use std::time::{Duration, Instant};
use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, POINT};
use windows_sys::Win32::Graphics::Gdi::{MONITOR_DEFAULTTONEAREST, MonitorFromPoint};
use windows_sys::Win32::Security::{
    GetSidSubAuthority, GetSidSubAuthorityCount, GetTokenInformation, TOKEN_MANDATORY_LABEL, TOKEN_QUERY,
    TokenIntegrityLevel,
};
use windows_sys::Win32::System::StationsAndDesktops::{CloseDesktop, DESKTOP_SWITCHDESKTOP, OpenInputDesktop};
use windows_sys::Win32::System::Threading::{
    GetCurrentProcess, OpenProcess, OpenProcessToken, PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows_sys::Win32::UI::HiDpi::{GetDpiForMonitor, MDT_EFFECTIVE_DPI};
use windows_sys::Win32::UI::Input::KeyboardAndMouse::*;
use windows_sys::Win32::UI::WindowsAndMessaging::{
    CURSOR_SHOWING, CURSORINFO, GetCursorInfo, GetCursorPos, GetForegroundWindow, GetSystemMetrics,
    GetWindowThreadProcessId, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN, SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN,
};

const TAG: usize = 0x57504c4d;

fn integrity(process: HANDLE) -> Option<u32> {
    // The buffer contains a SID pointer, so keep pointer-sized alignment.
    unsafe {
        let mut token = std::ptr::null_mut();
        if OpenProcessToken(process, TOKEN_QUERY, &mut token) == 0 {
            return None;
        }
        let mut length = 0;
        GetTokenInformation(token, TokenIntegrityLevel, std::ptr::null_mut(), 0, &mut length);
        let mut buffer = vec![0usize; (length as usize).div_ceil(size_of::<usize>())];
        let read = GetTokenInformation(token, TokenIntegrityLevel, buffer.as_mut_ptr().cast(), length, &mut length);
        CloseHandle(token);
        if read == 0 || (length as usize) < size_of::<TOKEN_MANDATORY_LABEL>() {
            return None;
        }
        let label = &*buffer.as_ptr().cast::<TOKEN_MANDATORY_LABEL>();
        let count = *GetSidSubAuthorityCount(label.Label.Sid);
        if count == 0 {
            return None;
        }
        Some(*GetSidSubAuthority(label.Label.Sid, count as u32 - 1))
    }
}

/// Run the input thread ahead of ordinary work, so a busy PC doesn't delay
/// pointer frames.
pub(crate) fn raise_thread_priority() {
    use windows_sys::Win32::System::Threading::{GetCurrentThread, SetThreadPriority, THREAD_PRIORITY_ABOVE_NORMAL};
    // SAFETY: the pseudo-handle of the calling thread.
    unsafe { SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL) };
}

/// Pointer pacing and momentum post every few milliseconds, but Windows
/// wakes a waiting thread only every 15.6 ms by default, which turns an even
/// glide into uneven 64 Hz steps. The finer timer is asked for only while
/// the engine has frames to run, since it costs power.
#[derive(Default)]
pub(crate) struct TimerResolution {
    fine: bool,
}

impl TimerResolution {
    pub(crate) fn want(&mut self, fine: bool) {
        use windows_sys::Win32::Media::{timeBeginPeriod, timeEndPeriod};
        if fine == self.fine {
            return;
        }
        static OPT_OUT: std::sync::Once = std::sync::Once::new();
        OPT_OUT.call_once(honor_timer_resolution_in_background);
        // SAFETY: plain value arguments; every begin is matched by an end.
        unsafe {
            if fine {
                timeBeginPeriod(1);
            } else {
                timeEndPeriod(1);
            }
        }
        self.fine = fine;
    }
}

impl Drop for TimerResolution {
    fn drop(&mut self) {
        self.want(false);
    }
}

/// Windows 11 ignores a process's timer resolution while none of its windows
/// are visible, which is how a tray app usually runs.
fn honor_timer_resolution_in_background() {
    use windows_sys::Win32::System::Threading::{
        PROCESS_POWER_THROTTLING_CURRENT_VERSION, PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
        PROCESS_POWER_THROTTLING_STATE, ProcessPowerThrottling, SetProcessInformation,
    };
    let state = PROCESS_POWER_THROTTLING_STATE {
        Version: PROCESS_POWER_THROTTLING_CURRENT_VERSION,
        ControlMask: PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
        StateMask: 0,
    };
    // SAFETY: a live structure of the size passed. Earlier Windows versions
    // reject the flag, which is harmless.
    unsafe {
        SetProcessInformation(
            GetCurrentProcess(),
            ProcessPowerThrottling,
            (&raw const state).cast(),
            size_of::<PROCESS_POWER_THROTTLING_STATE>() as u32,
        );
    }
}

pub fn has_post_permission() -> bool {
    unsafe {
        let desktop = OpenInputDesktop(0, 0, DESKTOP_SWITCHDESKTOP);
        if desktop.is_null() {
            return false;
        }
        CloseDesktop(desktop);
        let foreground = GetForegroundWindow();
        if foreground.is_null() {
            return true;
        }
        let mut pid = 0;
        GetWindowThreadProcessId(foreground, &mut pid);
        let process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if process.is_null() {
            return false;
        }
        let target = integrity(process);
        CloseHandle(process);
        static OWN: std::sync::OnceLock<Option<u32>> = std::sync::OnceLock::new();
        let own = OWN.get_or_init(|| integrity(GetCurrentProcess()));
        matches!((own, target), (Some(own), Some(target)) if target <= *own)
    }
}

/// Wheel units per phone point. 120 units is one notch: three lines in most
/// apps, 100 DIPs in Chromium and Electron.
const WHEEL_UNITS_PER_POINT: f64 = 3.0;

/// Ctrl+wheel units for a doubling of magnification during a pinch, about
/// three notches, as a precision touchpad sends to apps that zoom.
const ZOOM_UNITS_PER_DOUBLING: f64 = 360.0;

/// How long Caps Lock set by WinPalm is trusted over Windows' own state.
const CAPS_SETTLE: Duration = Duration::from_millis(250);

/// How long a permission check stays valid while the same window is in
/// front. It runs before every input event, pointer moves included.
const PERMISSION_TTL: Duration = Duration::from_millis(250);

#[derive(Default)]
pub struct WindowsSink {
    modifiers: HashSet<u16>,
    move_remainder: (f64, f64),
    wheel_remainder: (f64, f64),
    /// Sub-pixel pointer position, and the pixel last sent, so slow motion
    /// accumulates until something else moves the pointer.
    pointer: Option<((f64, f64), (i32, i32))>,
    /// When permission was last checked, for which foreground window.
    permission: Option<(Instant, usize, bool)>,
    /// Ctrl held down for a pinch in progress, which isn't anyone's key.
    zoom_control: bool,
    zoom_remainder: f64,
    /// Motion pushing past the edge of the screen, still owed to apps that
    /// read raw mouse movement.
    edge_remainder: (f64, f64),
    /// Caps Lock as WinPalm last set it. Windows takes a moment to process
    /// a toggle, so a key typed straight after must not read the old state.
    caps: Option<(bool, Instant)>,
}
impl WindowsSink {
    pub fn new() -> Self {
        Self::default()
    }
    fn send(inputs: &[INPUT]) {
        if inputs.is_empty() {
            return;
        }
        // SendInput is subject to UIPI. The host never requests elevation.
        let sent = unsafe { SendInput(inputs.len() as u32, inputs.as_ptr(), size_of::<INPUT>() as i32) };
        if sent != inputs.len() as u32 {
            tracing::warn!("Windows blocked synthetic input (secure desktop or elevated app)");
        }
    }
    fn key(code: u16, down: bool) {
        if let Some(scan) = super::windows_keys::scan(code) {
            Self::send(&[keyboard(
                0,
                scan.code,
                KEYEVENTF_SCANCODE
                    | if scan.extended { KEYEVENTF_EXTENDEDKEY } else { 0 }
                    | if down { 0 } else { KEYEVENTF_KEYUP },
            )]);
        }
    }
    fn with_flags(&self, wanted: u64, operation: impl FnOnce()) {
        // Windows has no per-event modifier flags. Text edits deliberately
        // exclude modifiers held by another remote device, so suspend those
        // injected modifiers around this event and then restore ownership.
        let mut suspended: Vec<u16> = self
            .modifiers
            .iter()
            .copied()
            .filter(|code| keycodes::modifier(*code).is_some_and(|m| wanted & m.flags != m.flags))
            .collect();
        if self.zoom_control && wanted & flags::CONTROL == 0 {
            suspended.push(keycodes::KEY_CONTROL);
        }
        if suspended.iter().any(|code| {
            matches!(
                *code,
                keycodes::KEY_COMMAND | keycodes::KEY_RIGHT_COMMAND | keycodes::KEY_OPTION | keycodes::KEY_RIGHT_OPTION
            )
        }) {
            // Mask a lone Win/Alt release so text does not open Start or a menu.
            Self::send(&[keyboard(0xe8, 0, 0), keyboard(0xe8, 0, KEYEVENTF_KEYUP)]);
        }
        for code in &suspended {
            Self::key(*code, false);
        }
        operation();
        for code in suspended.iter().rev() {
            Self::key(*code, true);
        }
    }
    /// Phone motion is in points, like macOS. Scale it to this monitor's
    /// pixels so 125–250% display scaling feels the same as 100%.
    fn pixel_scale(point: POINT) -> f64 {
        let (mut x, mut y) = (0u32, 0u32);
        // SAFETY: plain value arguments and out-pointers to locals.
        let ok = unsafe {
            GetDpiForMonitor(MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST), MDT_EFFECTIVE_DPI, &mut x, &mut y)
        } >= 0;
        if ok && (48..=960).contains(&x) { f64::from(x) / 96.0 } else { 1.0 }
    }
    fn move_pointer(&mut self, dx: f64, dy: f64) {
        let mut actual = POINT { x: 0, y: 0 };
        let mut info = CURSORINFO { cbSize: size_of::<CURSORINFO>() as u32, ..Default::default() };
        // SAFETY: out-pointers to locals with their sizes set.
        let (located, shown) = unsafe { (GetCursorPos(&mut actual) != 0, GetCursorInfo(&mut info) != 0) };
        let scale = Self::pixel_scale(actual);
        let (dx, dy) = (dx * scale, dy * scale);
        // Games and pointer-locked pages hide the cursor and read raw relative
        // motion. Everywhere else, place the pointer absolutely so Windows'
        // "Enhance pointer precision" doesn't accelerate it a second time.
        if !located || !shown || info.flags & CURSOR_SHOWING == 0 {
            self.pointer = None;
            self.move_remainder.0 += dx;
            self.move_remainder.1 += dy;
            let x = self.move_remainder.0.trunc() as i32;
            let y = self.move_remainder.1.trunc() as i32;
            self.move_remainder.0 -= x as f64;
            self.move_remainder.1 -= y as f64;
            if x != 0 || y != 0 {
                Self::send(&[mouse(x, y, 0, MOUSEEVENTF_MOVE)]);
            }
            return;
        }
        let start = match self.pointer {
            Some((precise, sent)) if sent == (actual.x, actual.y) => precise,
            _ => (actual.x as f64 + 0.5, actual.y as f64 + 0.5),
        };
        // SAFETY: no arguments beyond metric indices.
        let (left, top, width, height) = unsafe {
            (
                GetSystemMetrics(SM_XVIRTUALSCREEN),
                GetSystemMetrics(SM_YVIRTUALSCREEN),
                GetSystemMetrics(SM_CXVIRTUALSCREEN).max(1),
                GetSystemMetrics(SM_CYVIRTUALSCREEN).max(1),
            )
        };
        let wanted = (start.0 + dx, start.1 + dy);
        let x = wanted.0.clamp(left as f64, (left + width) as f64 - 0.001);
        let y = wanted.1.clamp(top as f64, (top + height) as f64 - 0.001);
        let pixel = (x.floor() as i32, y.floor() as i32);
        self.pointer = Some(((x, y), pixel));
        if pixel != (actual.x, actual.y) {
            let flags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
            Self::send(&[mouse(absolute(pixel.0 - left, width), absolute(pixel.1 - top, height), 0, flags)]);
        }
        // Like a real mouse, keep reporting movement against the edge of the
        // screen to apps that read raw input, such as 3D tools. Windows keeps
        // the pointer where it is.
        self.edge_remainder.0 += wanted.0 - x;
        self.edge_remainder.1 += wanted.1 - y;
        let edge = (self.edge_remainder.0.trunc() as i32, self.edge_remainder.1.trunc() as i32);
        self.edge_remainder.0 -= f64::from(edge.0);
        self.edge_remainder.1 -= f64::from(edge.1);
        if edge != (0, 0) {
            Self::send(&[mouse(edge.0, edge.1, 0, MOUSEEVENTF_MOVE)]);
        }
    }
    /// Windows apps zoom on Ctrl+wheel, which is also what a precision
    /// touchpad's pinch sends them. Rotation and smart zoom have no
    /// system-wide equivalent and are ignored.
    fn gesture(&mut self, kind: GestureKind, phase: u8, value: f64) {
        if kind != GestureKind::Zoom {
            return;
        }
        let held =
            self.modifiers.contains(&keycodes::KEY_CONTROL) || self.modifiers.contains(&keycodes::KEY_RIGHT_CONTROL);
        match phase {
            scroll_phase::BEGAN | scroll_phase::CHANGED => {
                if phase == scroll_phase::BEGAN {
                    self.zoom_remainder = 0.0;
                }
                if !held && !self.zoom_control {
                    Self::key(keycodes::KEY_CONTROL, true);
                    self.zoom_control = true;
                }
                self.zoom_remainder += (1.0 + value).max(0.01).log2() * ZOOM_UNITS_PER_DOUBLING;
                let units = self.zoom_remainder.trunc() as i32;
                self.zoom_remainder -= units as f64;
                if units != 0 {
                    Self::send(&[mouse(0, 0, units as u32, MOUSEEVENTF_WHEEL)]);
                }
            }
            _ => self.end_zoom(),
        }
    }
    fn end_zoom(&mut self) {
        self.zoom_remainder = 0.0;
        if !std::mem::take(&mut self.zoom_control) {
            return;
        }
        // A lone Ctrl press and release shows the pointer's location when
        // that mouse setting is on. An unassigned key in between prevents it.
        Self::send(&[keyboard(0xe8, 0, 0), keyboard(0xe8, 0, KEYEVENTF_KEYUP)]);
        Self::key(keycodes::KEY_CONTROL, false);
    }
    fn caps_lock_on(&mut self) -> bool {
        if let Some((on, at)) = self.caps
            && at.elapsed() < CAPS_SETTLE
        {
            return on;
        }
        self.caps = None;
        // SAFETY: a plain key code. The low bit is the toggle state.
        unsafe { GetKeyState(i32::from(VK_CAPITAL)) & 1 != 0 }
    }
    fn set_caps_lock(&mut self, on: bool) {
        if self.caps_lock_on() != on {
            Self::key(keycodes::KEY_CAPS_LOCK, true);
            Self::key(keycodes::KEY_CAPS_LOCK, false);
        }
        self.caps = Some((on, Instant::now()));
    }
    fn media(key: MediaKey, down: bool) {
        let vk = match key {
            MediaKey::VolumeUp => VK_VOLUME_UP,
            MediaKey::VolumeDown => VK_VOLUME_DOWN,
            MediaKey::Mute => VK_VOLUME_MUTE,
            MediaKey::PlayPause => VK_MEDIA_PLAY_PAUSE,
            MediaKey::Next | MediaKey::FastForward => VK_MEDIA_NEXT_TRACK,
            MediaKey::Previous | MediaKey::Rewind => VK_MEDIA_PREV_TRACK,
            // Brightness is routed through the host's WMI/DDC implementation.
            _ => return,
        };
        Self::send(&[keyboard(vk, 0, if down { 0 } else { KEYEVENTF_KEYUP })]);
    }
}
fn keyboard(vk: u16, scan: u16, flags: u32) -> INPUT {
    INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 { ki: KEYBDINPUT { wVk: vk, wScan: scan, dwFlags: flags, time: 0, dwExtraInfo: TAG } },
    }
}
/// Normalized 0–65535 coordinate that Windows maps back to exactly this
/// pixel: Windows computes `pixel = normalized * size / 65536`.
fn absolute(offset: i32, size: i32) -> i32 {
    let offset = i64::from(offset.clamp(0, size - 1));
    ((offset * 65_536) / i64::from(size) + 1).min(65_535) as i32
}
fn mouse(dx: i32, dy: i32, data: u32, flags: u32) -> INPUT {
    INPUT {
        r#type: INPUT_MOUSE,
        Anonymous: INPUT_0 { mi: MOUSEINPUT { dx, dy, mouseData: data, dwFlags: flags, time: 0, dwExtraInfo: TAG } },
    }
}
impl EventSink for WindowsSink {
    fn post(&mut self, event: Synth) {
        match event {
            Synth::Flags { code, flags: state } => {
                if let Some(modifier) = keycodes::modifier(code) {
                    let device = modifier.flags
                        & !(flags::SHIFT | flags::CONTROL | flags::ALTERNATE | flags::COMMAND | flags::SECONDARY_FN);
                    let down = state & device != 0;
                    let changed = if down { self.modifiers.insert(code) } else { self.modifiers.remove(&code) };
                    // A phone's Ctrl pressed during a pinch takes over the
                    // Ctrl the pinch is holding.
                    let pinch = code == keycodes::KEY_CONTROL && std::mem::take(&mut self.zoom_control);
                    if changed && !(down && pinch) {
                        Self::key(code, down);
                    }
                }
            }
            Synth::Key { code, down, flags: wanted, .. } => self.with_flags(wanted, || Self::key(code, down)),
            Synth::Text { text } => {
                let mut events = Vec::with_capacity(text.len() * 2);
                for unit in text.encode_utf16() {
                    events.push(keyboard(0, unit, KEYEVENTF_UNICODE));
                    events.push(keyboard(0, unit, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
                }
                self.with_flags(0, || Self::send(&events));
            }
            Synth::Move { dx, dy, .. } => self.move_pointer(dx, dy),
            Synth::Button { button, down, .. } => {
                let (data, flag) = match (button, down) {
                    (MouseButton::Left, true) => (0, MOUSEEVENTF_LEFTDOWN),
                    (MouseButton::Left, false) => (0, MOUSEEVENTF_LEFTUP),
                    (MouseButton::Right, true) => (0, MOUSEEVENTF_RIGHTDOWN),
                    (MouseButton::Right, false) => (0, MOUSEEVENTF_RIGHTUP),
                    (MouseButton::Middle, true) => (0, MOUSEEVENTF_MIDDLEDOWN),
                    (MouseButton::Middle, false) => (0, MOUSEEVENTF_MIDDLEUP),
                    (MouseButton::Back, true) => (1, MOUSEEVENTF_XDOWN),
                    (MouseButton::Back, false) => (1, MOUSEEVENTF_XUP),
                    (MouseButton::Forward, true) => (2, MOUSEEVENTF_XDOWN),
                    (MouseButton::Forward, false) => (2, MOUSEEVENTF_XUP),
                };
                Self::send(&[mouse(0, 0, data, flag)]);
            }
            Synth::Scroll { dx, dy, phase, momentum, .. } => {
                // Convert point deltas to high-resolution wheel units; retain
                // fractions so very slow scrolling never disappears. Deltas
                // follow macOS, where positive X scrolls left; a positive
                // Windows horizontal wheel scrolls right.
                self.wheel_remainder.0 -= dx * WHEEL_UNITS_PER_POINT;
                self.wheel_remainder.1 += dy * WHEEL_UNITS_PER_POINT;
                let x = self.wheel_remainder.0.trunc() as i32;
                let y = self.wheel_remainder.1.trunc() as i32;
                self.wheel_remainder.0 -= x as f64;
                self.wheel_remainder.1 -= y as f64;
                if phase != scroll_phase::NONE && phase != scroll_phase::CHANGED || momentum == momentum_phase::END {
                    self.wheel_remainder = (0.0, 0.0);
                }
                if x != 0 {
                    Self::send(&[mouse(0, 0, x as u32, MOUSEEVENTF_HWHEEL)]);
                }
                if y != 0 {
                    Self::send(&[mouse(0, 0, y as u32, MOUSEEVENTF_WHEEL)]);
                }
            }
            Synth::Media { key, down } => Self::media(key, down),
            Synth::Gesture { kind, phase, value } => self.gesture(kind, phase, value),
            Synth::ToggleCapsLock => {
                let on = !self.caps_lock_on();
                self.set_caps_lock(on);
            }
            Synth::SetCapsLock(on) => self.set_caps_lock(on),
            Synth::Pause(duration) => std::thread::sleep(duration),
        }
    }
    fn cursor(&mut self) -> (f64, f64) {
        let mut point = POINT { x: 0, y: 0 };
        unsafe {
            GetCursorPos(&mut point);
        }
        (point.x as f64, point.y as f64)
    }
    fn caps_lock(&mut self) -> Option<bool> {
        Some(self.caps_lock_on())
    }
    fn can_post(&mut self) -> bool {
        // SAFETY: no arguments.
        let foreground = unsafe { GetForegroundWindow() } as usize;
        let now = Instant::now();
        if let Some((at, window, allowed)) = self.permission
            && window == foreground
            && now.duration_since(at) < PERMISSION_TTL
        {
            return allowed;
        }
        let allowed = has_post_permission();
        self.permission = Some((now, foreground, allowed));
        allowed
    }
}
