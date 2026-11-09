//! Small UTF-8 JSON C ABI for the native Windows host. Every returned pointer
//! has one matching free function. Callbacks are rooted for the process lifetime.
use crate::CoreHost;
use crate::api::*;
use crate::protocol::{AppEntry, AppOp, MediaKey};
use base64::Engine as _;
use serde::de::DeserializeOwned;
use serde_json::{Value, json};
use std::ffi::{CStr, CString, c_char};
use std::sync::{Arc, Mutex, OnceLock};

type Invoke = unsafe extern "C" fn(*const c_char) -> *mut c_char;
type Release = unsafe extern "C" fn(*mut c_char);
static CALLBACK: OnceLock<(Invoke, Release)> = OnceLock::new();
static HOST: Mutex<Option<Arc<CoreHost>>> = Mutex::new(None);

struct Desktop;
impl Desktop {
    fn call<T: DeserializeOwned>(&self, op: &str, value: Value) -> Option<T> {
        let (invoke, release) = CALLBACK.get()?;
        let request = CString::new(json!({"op":op,"value":value}).to_string()).ok()?;
        // SAFETY: the desktop roots both delegates until process shutdown.
        let response = unsafe { invoke(request.as_ptr()) };
        if response.is_null() {
            return None;
        }
        let result = unsafe { serde_json::from_slice(CStr::from_ptr(response).to_bytes()).ok() };
        unsafe {
            release(response);
        }
        result
    }
}
impl HostDelegate for Desktop {
    fn on_event(&self, event: HostEvent) {
        self.call::<Value>("onEvent", json!(event));
    }
    fn list_apps(&self) -> Vec<AppEntry> {
        self.call("listApps", Value::Null).unwrap_or_default()
    }
    fn app_icon(&self, id: String, size: u32) -> Option<Vec<u8>> {
        let encoded: String = self.call("appIcon", json!({"id":id,"size":size}))?;
        base64::prelude::BASE64_STANDARD.decode(encoded).ok()
    }
    fn app_action(&self, id: String, op: AppOp, session: u64) -> bool {
        self.call("appAction", json!({"id":id,"op":op,"session":session})).unwrap_or(false)
    }
    fn open_url(&self, url: String) -> bool {
        self.call("openUrl", json!(url)).unwrap_or(false)
    }
    fn native(&self, command: NativeCommand) -> bool {
        self.call("native", json!(command)).unwrap_or(false)
    }
    fn automation_command(&self, name: String) -> Option<String> {
        self.call("automationCommand", json!(name))
    }
    fn platform_media(&self, key: MediaKey) -> bool {
        self.call("platformMedia", json!(key)).unwrap_or(false)
    }
    fn set_volume(&self, value: Option<f32>, muted: Option<bool>) {
        self.call::<Value>("setVolume", json!({"value":value,"muted":muted}));
    }
    fn clipboard_text(&self) -> Option<String> {
        self.call("clipboardText", Value::Null)
    }
    fn set_clipboard_text(&self, text: String) {
        self.call::<Value>("setClipboardText", json!(text));
    }
    fn begin_paste(&self, text: String) -> bool {
        self.call("beginPaste", json!(text)).unwrap_or(false)
    }
    fn end_paste(&self) {
        self.call::<Value>("endPaste", Value::Null);
    }
    fn voice_start(&self, session: u64, locale: Option<String>, request_id: Option<u32>) -> Result<(), String> {
        let result: Value = self
            .call("voiceStart", json!({"session":session,"locale":locale,"requestId":request_id}))
            .ok_or_else(|| "Windows speech recognition did not respond.".to_string())?;
        if result["started"].as_bool() == Some(true) {
            Ok(())
        } else {
            Err(result["message"].as_str().unwrap_or("Couldn't start speech recognition on the PC.").to_string())
        }
    }
    fn voice_audio(&self, session: u64, pcm: Vec<u8>) {
        self.call::<Value>("voiceAudio", json!({"session":session,"pcm":base64::prelude::BASE64_STANDARD.encode(pcm)}));
    }
    fn voice_stop(&self, session: u64, cancel: bool) {
        self.call::<Value>("voiceStop", json!({"session":session,"cancel":cancel}));
    }
}

/// # Safety
/// Both callbacks must be valid, thread-safe and live until process exit.
/// `invoke` returns a NUL-terminated UTF-8 allocation that `release` owns.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn winpalm_initialize(invoke: Invoke, release: Release) -> bool {
    CALLBACK.set((invoke, release)).is_ok()
}

fn dispatch(request: Value) -> Result<Value, String> {
    let op = request["op"].as_str().ok_or("Missing operation")?;
    if op == "create" {
        if CALLBACK.get().is_none() {
            return Err("Desktop callbacks are not initialized".into());
        }
        let mut slot = HOST.lock().map_err(|_| "Host lock failed")?;
        if slot.is_some() {
            return Err("Host already exists".into());
        }
        let config: HostConfig =
            serde_json::from_value(request["config"].clone()).map_err(|_| "Invalid host configuration")?;
        *slot = Some(if request["dryRun"].as_bool().unwrap_or(false) {
            CoreHost::dry_run(config, Arc::new(Desktop))
        } else {
            CoreHost::new(config, Arc::new(Desktop))
        });
        return Ok(Value::Null);
    }
    // Never hold this lock across a host operation: callbacks may reenter.
    let host = HOST.lock().map_err(|_| "Host lock failed")?.clone().ok_or("Host has not been created")?;
    let text = |key: &str| request[key].as_str().unwrap_or_default().to_string();
    let id = || request["id"].as_u64().unwrap_or_default();
    match op {
        "actionAllowed" => Ok(json!(!host.is_paused() && host.connected_devices().iter().any(|d| d.session == id()))),
        "scanCodes" => Ok(json!(
            (0..=255)
                .filter_map(|code| crate::input::windows_keys::scan(code)
                    .map(|scan| json!({"code":code,"scan":scan.code,"extended":scan.extended})))
                .collect::<Vec<_>>()
        )),
        "start" => Ok(json!(host.start().map_err(|e| e.to_string())?)),
        "stop" => {
            host.stop();
            Ok(Value::Null)
        }
        "status" => Ok(
            json!({"running":host.is_running(),"paused":host.is_paused(),"pairing":host.pairing_info(),"connected":host.connected_devices(),"trusted":host.trusted_devices()}),
        ),
        "config" => {
            host.update_config(serde_json::from_value(request["config"].clone()).map_err(|_| "Invalid configuration")?);
            Ok(Value::Null)
        }
        "pause" => {
            host.set_paused(request["paused"].as_bool().unwrap_or(true));
            Ok(Value::Null)
        }
        "pinShown" => {
            host.set_pin_shown(request["shown"].as_bool().unwrap_or(false));
            Ok(Value::Null)
        }
        "renew" => {
            host.renew_pairing();
            Ok(Value::Null)
        }
        "forget" => {
            host.forget_device(text("deviceId"));
            Ok(Value::Null)
        }
        "forgetAll" => {
            host.forget_all_devices();
            Ok(Value::Null)
        }
        "rename" => {
            host.rename_device(text("deviceId"), text("name"));
            Ok(Value::Null)
        }
        "disconnect" => {
            host.disconnect(id());
            Ok(Value::Null)
        }
        "approve" => {
            host.respond_approval(id(), request["allow"].as_bool().unwrap_or(false));
            Ok(Value::Null)
        }
        "layout" => {
            host.set_layout(serde_json::from_value(request["layout"].clone()).map_err(|_| "Invalid keyboard layout")?);
            Ok(Value::Null)
        }
        "timing" => {
            host.set_input_timing(
                request["delay"].as_u64().unwrap_or(500) as u32,
                request["interval"].as_u64().unwrap_or(83) as u32,
                request["doubleClick"].as_u64().unwrap_or(500) as u32,
            );
            Ok(Value::Null)
        }
        "volume" => {
            if request["value"].is_null() {
                host.clear_volume_state();
            } else {
                host.set_volume_state(
                    request["value"].as_f64().unwrap_or(0.0) as f32,
                    request["muted"].as_bool().unwrap_or(false),
                );
            }
            Ok(Value::Null)
        }
        "appsChanged" => {
            host.apps_changed();
            Ok(Value::Null)
        }
        "permissionChanged" => {
            host.permission_changed();
            Ok(Value::Null)
        }
        "voiceUpdate" => {
            host.voice_update(
                id(),
                serde_json::from_value(request["update"].clone()).map_err(|_| "Invalid speech result")?,
            );
            Ok(Value::Null)
        }
        "deck" => Ok(serde_json::from_str(&host.deck_json()).unwrap_or(Value::Null)),
        "setDeck" => {
            host.set_deck_json(request["deck"].to_string()).map_err(|e| e.to_string())?;
            Ok(Value::Null)
        }
        "resetDeck" => {
            host.reset_deck();
            Ok(Value::Null)
        }
        "certificate" => Ok(json!(base64::prelude::BASE64_STANDARD.encode(host.ca_certificate()))),
        _ => Err("Unknown host operation".into()),
    }
}

/// # Safety
/// `request` must point to NUL-terminated UTF-8. The returned allocation must
/// be freed exactly once with `winpalm_free`, never the desktop allocator.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn winpalm_call(request: *const c_char) -> *mut c_char {
    let result = std::panic::catch_unwind(|| {
        if request.is_null() {
            return Err("Null request".to_string());
        }
        let bytes = unsafe { CStr::from_ptr(request).to_bytes() };
        if bytes.len() > 4 * 1024 * 1024 {
            return Err("Host request too large".into());
        }
        let request = serde_json::from_slice(bytes).map_err(|_| "Invalid JSON".to_string())?;
        dispatch(request)
    })
    .unwrap_or_else(|_| Err("Host operation failed".into()));
    let response = match result {
        Ok(value) => json!({"ok":true,"value":value}),
        Err(error) => json!({"ok":false,"error":error}),
    };
    CString::new(response.to_string()).expect("JSON contains no NUL bytes").into_raw()
}

/// # Safety
/// `pointer` must be null or an unfreed allocation returned by `winpalm_call`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn winpalm_free(pointer: *mut c_char) {
    if !pointer.is_null() {
        drop(unsafe { CString::from_raw(pointer) });
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn abi_rejects_invalid_requests_without_unwinding() {
        for request in [None, Some("not json"), Some("{}"), Some(r#"{"op":"unknown"}"#)] {
            let input = request.map(|s| CString::new(s).unwrap());
            let out = unsafe { winpalm_call(input.as_ref().map_or(std::ptr::null(), |s| s.as_ptr())) };
            let response: Value = unsafe { serde_json::from_slice(CStr::from_ptr(out).to_bytes()).unwrap() };
            assert_eq!(response["ok"], false);
            unsafe { winpalm_free(out) };
        }
    }
}
