//! Delay-load Media Foundation from System32. Windows N installations without
//! the Media Feature Pack can still start WinPalm and use lossless HD pictures.
#![allow(non_snake_case)]

use std::ffi::c_void;
use std::sync::OnceLock;
use windows::Win32::Media::MediaFoundation::{
    IMFActivate, IMFMediaBuffer, IMFMediaType, IMFSample, MFT_ENUM_FLAG, MFT_REGISTER_TYPE_INFO,
};
use windows::core::{GUID, HRESULT, Interface};
use windows_sys::Win32::System::LibraryLoader::{GetProcAddress, LOAD_LIBRARY_SEARCH_SYSTEM32, LoadLibraryExW};

struct Api {
    startup: unsafe extern "system" fn(u32, u32) -> HRESULT,
    shutdown: unsafe extern "system" fn() -> HRESULT,
    media_type: unsafe extern "system" fn(*mut *mut c_void) -> HRESULT,
    sample: unsafe extern "system" fn(*mut *mut c_void) -> HRESULT,
    aligned_buffer: unsafe extern "system" fn(u32, u32, *mut *mut c_void) -> HRESULT,
    enumerate: unsafe extern "system" fn(
        GUID,
        u32,
        *const MFT_REGISTER_TYPE_INFO,
        *const MFT_REGISTER_TYPE_INFO,
        *mut *mut Option<IMFActivate>,
        *mut u32,
    ) -> HRESULT,
}

fn api() -> windows::core::Result<&'static Api> {
    static API: OnceLock<Option<Api>> = OnceLock::new();
    let api =
        API.get_or_init(|| unsafe {
            let name = "mfplat.dll\0".encode_utf16().collect::<Vec<_>>();
            let library = LoadLibraryExW(name.as_ptr(), std::ptr::null_mut(), LOAD_LIBRARY_SEARCH_SYSTEM32);
            if library.is_null() {
                return None;
            }
            // The library stays loaded for the process lifetime; every pointer is
            // checked before conversion to the signature declared in mfapi.h.
            Some(Api {
                startup: std::mem::transmute::<
                    unsafe extern "system" fn() -> isize,
                    unsafe extern "system" fn(u32, u32) -> HRESULT,
                >(GetProcAddress(library, c"MFStartup".as_ptr().cast())?),
                shutdown: std::mem::transmute::<
                    unsafe extern "system" fn() -> isize,
                    unsafe extern "system" fn() -> HRESULT,
                >(GetProcAddress(library, c"MFShutdown".as_ptr().cast())?),
                media_type: std::mem::transmute::<
                    unsafe extern "system" fn() -> isize,
                    unsafe extern "system" fn(*mut *mut c_void) -> HRESULT,
                >(GetProcAddress(library, c"MFCreateMediaType".as_ptr().cast())?),
                sample: std::mem::transmute::<
                    unsafe extern "system" fn() -> isize,
                    unsafe extern "system" fn(*mut *mut c_void) -> HRESULT,
                >(GetProcAddress(library, c"MFCreateSample".as_ptr().cast())?),
                aligned_buffer: std::mem::transmute::<
                    unsafe extern "system" fn() -> isize,
                    unsafe extern "system" fn(u32, u32, *mut *mut c_void) -> HRESULT,
                >(GetProcAddress(
                    library,
                    c"MFCreateAlignedMemoryBuffer".as_ptr().cast(),
                )?),
                enumerate: std::mem::transmute::<
                    unsafe extern "system" fn() -> isize,
                    unsafe extern "system" fn(
                        GUID,
                        u32,
                        *const MFT_REGISTER_TYPE_INFO,
                        *const MFT_REGISTER_TYPE_INFO,
                        *mut *mut Option<IMFActivate>,
                        *mut u32,
                    ) -> HRESULT,
                >(GetProcAddress(library, c"MFTEnumEx".as_ptr().cast())?),
            })
        });
    api.as_ref().ok_or_else(|| windows::core::Error::from_hresult(HRESULT::from_win32(126)))
}

pub(super) unsafe fn MFStartup(version: u32, flags: u32) -> windows::core::Result<()> {
    unsafe { (api()?.startup)(version, flags).ok() }
}
pub(super) unsafe fn MFShutdown() -> windows::core::Result<()> {
    unsafe { (api()?.shutdown)().ok() }
}
pub(super) unsafe fn MFCreateMediaType() -> windows::core::Result<IMFMediaType> {
    let mut result = std::ptr::null_mut();
    unsafe {
        (api()?.media_type)(&mut result).ok()?;
        Ok(IMFMediaType::from_raw(result))
    }
}
pub(super) unsafe fn MFCreateSample() -> windows::core::Result<IMFSample> {
    let mut result = std::ptr::null_mut();
    unsafe {
        (api()?.sample)(&mut result).ok()?;
        Ok(IMFSample::from_raw(result))
    }
}
pub(super) unsafe fn MFCreateAlignedMemoryBuffer(size: u32, alignment: u32) -> windows::core::Result<IMFMediaBuffer> {
    let mut result = std::ptr::null_mut();
    unsafe {
        (api()?.aligned_buffer)(size, alignment, &mut result).ok()?;
        Ok(IMFMediaBuffer::from_raw(result))
    }
}
pub(super) unsafe fn MFTEnumEx(
    category: GUID,
    flags: MFT_ENUM_FLAG,
    input: Option<*const MFT_REGISTER_TYPE_INFO>,
    output: Option<*const MFT_REGISTER_TYPE_INFO>,
    activations: *mut *mut Option<IMFActivate>,
    count: *mut u32,
) -> windows::core::Result<()> {
    unsafe {
        (api()?.enumerate)(
            category,
            flags.0 as u32,
            input.unwrap_or(std::ptr::null()),
            output.unwrap_or(std::ptr::null()),
            activations,
            count,
        )
        .ok()
    }
}
