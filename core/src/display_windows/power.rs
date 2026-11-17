//! One power request object per capture, including replacements during rebuild.
//! Explicit user sleep still wins. No away-mode or execution request is taken.
use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, INVALID_HANDLE_VALUE};
use windows_sys::Win32::System::Power::{
    PowerClearRequest, PowerCreateRequest, PowerRequestDisplayRequired, PowerRequestSystemRequired, PowerSetRequest,
};
use windows_sys::Win32::System::Threading::{POWER_REQUEST_CONTEXT_SIMPLE_STRING, REASON_CONTEXT, REASON_CONTEXT_0};

pub(super) struct KeepAwake(HANDLE);

impl KeepAwake {
    pub(super) fn new() -> Result<Self, String> {
        let mut reason: Vec<u16> = "WinPalm is mirroring a display to an allowed device.\0".encode_utf16().collect();
        let context = REASON_CONTEXT {
            Version: 0,
            Flags: POWER_REQUEST_CONTEXT_SIMPLE_STRING,
            Reason: REASON_CONTEXT_0 { SimpleReasonString: reason.as_mut_ptr() },
        };
        // SAFETY: PowerCreateRequest copies the reason; the owned handle stays
        // on the capture worker. Each successful request is cleared exactly once.
        unsafe {
            let handle = PowerCreateRequest(&context);
            if handle == INVALID_HANDLE_VALUE || handle.is_null() {
                return Err("Windows could not keep the display awake for screen mirroring. Try again.".into());
            }
            if PowerSetRequest(handle, PowerRequestSystemRequired) == 0 {
                CloseHandle(handle);
                return Err("Windows could not keep the PC awake for screen mirroring. Try again.".into());
            }
            if PowerSetRequest(handle, PowerRequestDisplayRequired) == 0 {
                PowerClearRequest(handle, PowerRequestSystemRequired);
                CloseHandle(handle);
                return Err("Windows could not keep the display awake for screen mirroring. Try again.".into());
            }
            Ok(Self(handle))
        }
    }
}

impl Drop for KeepAwake {
    fn drop(&mut self) {
        // SAFETY: this capture owns both counts on this live handle. Distinct
        // handles ensure dropping an old capture cannot clear its replacement.
        unsafe {
            PowerClearRequest(self.0, PowerRequestDisplayRequired);
            PowerClearRequest(self.0, PowerRequestSystemRequired);
            CloseHandle(self.0);
        }
    }
}
