//! Bounded Media Foundation H.264 encoding. Hardware MFTs are preferred;
//! incompatible drivers fall back to Microsoft's synchronous encoder.
use crate::api::DisplayProfile;
use crate::display_capture_state::EncoderQueue;
use crate::display_pixels as pixels;
use std::mem::ManuallyDrop;
use std::time::{Duration, Instant};
use windows::Win32::Media::MediaFoundation::*;
use windows::Win32::System::Com::{
    CLSCTX_INPROC_SERVER, COINIT_MULTITHREADED, CoCreateInstance, CoInitializeEx, CoTaskMemFree, CoUninitialize,
};
use windows::Win32::System::Variant::VARIANT;
use windows::core::{GUID, Interface};
type Result<T> = std::result::Result<T, String>;

#[path = "media.rs"]
mod media;
use media::{MFCreateAlignedMemoryBuffer, MFCreateMediaType, MFCreateSample, MFShutdown, MFStartup, MFTEnumEx};

struct MediaPlatform;
impl MediaPlatform {
    fn new() -> Result<Self> {
        unsafe { MFStartup(MF_VERSION, MFSTARTUP_LITE) }
            .map_err(|_| {
                "Windows media components are unavailable. Install the Media Feature Pack on Windows N, or use HD text."
                    .into()
            })
            .map(|_| Self)
    }
}
impl Drop for MediaPlatform {
    fn drop(&mut self) {
        unsafe {
            let _ = MFShutdown();
        }
    }
}
pub(super) struct Encoder {
    transform: IMFTransform,
    codec: Option<ICodecAPI>,
    events: Option<IMFMediaEventGenerator>,
    activation: Option<IMFActivate>,
    need_input: u32,
    have_output: u32,
    fps: u32,
    sequence: Vec<u8>,
    queue: EncoderQueue,
    hardware: bool,
    wide_rate_limit: bool,
    _platform: MediaPlatform,
}
impl Encoder {
    pub(super) fn new(size: (u32, u32), fps: u32, bitrate: u32, profile: DisplayProfile) -> Result<Self> {
        Self::create(size, fps, bitrate, profile, true)
    }
    pub(super) fn software(size: (u32, u32), fps: u32, bitrate: u32, profile: DisplayProfile) -> Result<Self> {
        Self::create(size, fps, bitrate, profile, false)
    }
    pub(super) fn hardware(&self) -> bool {
        self.hardware
    }
    fn profile_candidates(profile: DisplayProfile) -> &'static [u32] {
        // Never upgrade above the negotiated profile; fall back to lower H.264
        // profiles (all with B-frames disabled) when a driver rejects High/Main.
        match profile {
            DisplayProfile::ConstrainedHigh => &[100, 77, 66],
            DisplayProfile::Main => &[77, 66],
            DisplayProfile::ConstrainedBaseline => &[66],
        }
    }
    fn create(
        size: (u32, u32),
        fps: u32,
        bitrate: u32,
        profile: DisplayProfile,
        prefer_hardware: bool,
    ) -> Result<Self> {
        let platform = MediaPlatform::new()?;
        // Prefer a compatible hardware MFT. Some drivers require GPU samples;
        // the Windows software encoder supplies a bounded fallback for those.
        unsafe {
            let mut activations = std::ptr::null_mut();
            let mut count = 0;
            let output = MFT_REGISTER_TYPE_INFO { guidMajorType: MFMediaType_Video, guidSubtype: MFVideoFormat_H264 };
            let mut hardware = vec![];
            if prefer_hardware
                && MFTEnumEx(
                    MFT_CATEGORY_VIDEO_ENCODER,
                    MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
                    None,
                    Some(&output),
                    &mut activations,
                    &mut count,
                )
                .is_ok()
                && !activations.is_null()
            {
                for activation in std::slice::from_raw_parts_mut(activations, count as usize) {
                    if let Some(a) = activation.take() {
                        hardware.push(a);
                    }
                }
                CoTaskMemFree(Some(activations.cast()));
            }
            for activation in hardware {
                if let Ok(transform) = activation.ActivateObject::<IMFTransform>() {
                    if let Ok((codec, events)) = Self::configure(&transform, size, fps, bitrate, profile) {
                        return Ok(Self {
                            transform,
                            codec,
                            events,
                            activation: Some(activation),
                            need_input: 0,
                            have_output: 0,
                            fps,
                            sequence: vec![],
                            queue: EncoderQueue::default(),
                            hardware: true,
                            wide_rate_limit: false,
                            _platform: platform,
                        });
                    }
                    Self::shutdown(&transform);
                }
                let _ = activation.ShutdownObject();
            }
            let transform: IMFTransform = CoCreateInstance(&CMSH264EncoderMFT, None, CLSCTX_INPROC_SERVER).map_err(
                |_| "H.264 encoding is unavailable on this PC. Try HD text or install the Windows media components.",
            )?;
            let (codec, events) = Self::configure(&transform, size, fps, bitrate, profile).map_err(|_| {
                Self::shutdown(&transform);
                "Windows could not prepare H.264 video. Try Save battery or HD text."
            })?;
            Ok(Self {
                transform,
                codec,
                events,
                activation: None,
                need_input: 0,
                have_output: 0,
                fps,
                sequence: vec![],
                queue: EncoderQueue::default(),
                hardware: false,
                wide_rate_limit: false,
                _platform: platform,
            })
        }
    }
    unsafe fn configure(
        transform: &IMFTransform,
        size: (u32, u32),
        fps: u32,
        bitrate: u32,
        profile: DisplayProfile,
    ) -> windows::core::Result<(Option<ICodecAPI>, Option<IMFMediaEventGenerator>)> {
        unsafe {
            let attributes = transform.GetAttributes()?;
            let asynchronous = attributes.GetUINT32(&MF_TRANSFORM_ASYNC).unwrap_or(0) != 0;
            if asynchronous {
                attributes.SetUINT32(&MF_TRANSFORM_ASYNC_UNLOCK, 1)?;
            }
            let codec = Some(transform.cast::<ICodecAPI>()?);
            if let Some(codec) = &codec {
                codec.SetValue(&CODECAPI_AVLowLatencyMode, &VARIANT::from(true))?;
                let _ = codec.SetValue(&CODECAPI_AVEncMPVDefaultBPictureCount, &VARIANT::from(0u32));
                // Relax periodic IDR interval to 30s; PLI/FIR still triggers immediate keyframes.
                let _ = codec.SetValue(&CODECAPI_AVEncMPVGOPSize, &VARIANT::from(fps.saturating_mul(30)));
                let _ = codec.SetValue(&CODECAPI_AVEncNumWorkerThreads, &VARIANT::from(2u32));
                let _ = codec.SetValue(&CODECAPI_AVEncCommonRateControlMode, &VARIANT::from(0u32));
                let _ = codec.SetValue(&CODECAPI_AVEncCommonMeanBitRate, &VARIANT::from(bitrate));
                // A 1.25-second HRD budget with room for IDR pictures.
                // H.264 specifies this property in bytes, not bits.
                let _ = codec.SetValue(&CODECAPI_AVEncCommonBufferSize, &VARIANT::from(bitrate / 8 * 5 / 4));
            }
            let mut chosen_profile = None;
            let mut last_err = None;
            for &profile_idc in Self::profile_candidates(profile) {
                if let Some(codec) = &codec {
                    let _ = codec.SetValue(&CODECAPI_AVEncH264CABACEnable, &VARIANT::from(profile_idc != 66));
                }
                let output = Self::media_type(size, fps, &MFVideoFormat_H264)?;
                output.SetUINT32(&MF_MT_AVG_BITRATE, bitrate)?;
                output.SetUINT32(&MF_MT_MPEG2_PROFILE, profile_idc)?;
                match transform.SetOutputType(0, &output, 0) {
                    Ok(()) => {
                        chosen_profile = Some(profile_idc);
                        break;
                    }
                    Err(err) => last_err = Some(err),
                }
            }
            if chosen_profile.is_none() {
                return Err(last_err.unwrap_or_else(windows::core::Error::from_thread));
            }
            let input = Self::media_type(size, fps, &MFVideoFormat_NV12)?;
            input.SetUINT32(&MF_MT_DEFAULT_STRIDE, size.0)?;
            input.SetUINT32(&MF_MT_SAMPLE_SIZE, size.0 * size.1 * 3 / 2)?;
            input.SetUINT32(&MF_MT_FIXED_SIZE_SAMPLES, 1)?;
            input.SetUINT32(&MF_MT_ALL_SAMPLES_INDEPENDENT, 1)?;
            transform.SetInputType(0, &input, 0)?;
            transform.ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0)?;
            transform.ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0)?;
            Ok((codec, if asynchronous { Some(transform.cast()?) } else { None }))
        }
    }
    unsafe fn media_type(size: (u32, u32), fps: u32, subtype: &GUID) -> windows::core::Result<IMFMediaType> {
        unsafe {
            let media = MFCreateMediaType()?;
            media.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Video)?;
            media.SetGUID(&MF_MT_SUBTYPE, subtype)?;
            media.SetUINT64(&MF_MT_FRAME_SIZE, ((size.0 as u64) << 32) | size.1 as u64)?;
            media.SetUINT64(&MF_MT_FRAME_RATE, ((fps as u64) << 32) | 1)?;
            media.SetUINT64(&MF_MT_PIXEL_ASPECT_RATIO, (1u64 << 32) | 1)?;
            media.SetUINT32(&MF_MT_INTERLACE_MODE, 2)?;
            // The CPU converter uses BT.709 video-range NV12.
            media.SetUINT32(&MF_MT_YUV_MATRIX, 1)?;
            media.SetUINT32(&MF_MT_VIDEO_NOMINAL_RANGE, 2)?;
            Ok(media)
        }
    }
    pub(super) fn bitrate(&mut self, bitrate: u32) -> Result<()> {
        self.wide_rate_limit = false;
        if let Some(codec) = &self.codec {
            unsafe {
                codec
                    .SetValue(&CODECAPI_AVEncCommonMeanBitRate, &VARIANT::from(bitrate))
                    .map_err(|_| "The Windows encoder could not adjust its video bitrate.")?;
                let _ = codec.SetValue(&CODECAPI_AVEncCommonBufferSize, &VARIANT::from(bitrate / 8 * 5 / 4));
            }
        }
        Ok(())
    }
    pub(super) fn set_wide_rate_limit(&mut self, wide: bool, bitrate: u32) {
        if self.wide_rate_limit == wide {
            return;
        }
        self.wide_rate_limit = wide;
        if let Some(codec) = &self.codec {
            let target = if wide { bitrate.saturating_mul(2).min(24_000_000) } else { bitrate };
            let buffer = if wide { bitrate / 4 } else { bitrate / 8 * 5 / 4 };
            unsafe {
                let _ = codec.SetValue(&CODECAPI_AVEncCommonMeanBitRate, &VARIANT::from(target));
                let _ = codec.SetValue(&CODECAPI_AVEncCommonBufferSize, &VARIANT::from(buffer));
            }
        }
    }
    fn poll(&mut self) -> Result<()> {
        let Some(events) = &self.events else {
            return Ok(());
        };
        unsafe {
            for _ in 0..16 {
                match events.GetEvent(MF_EVENT_FLAG_NO_WAIT) {
                    Err(e) if e.code() == MF_E_NO_EVENTS_AVAILABLE => break,
                    Err(_) => return Err("The Windows video encoder stopped responding. Start viewing again.".into()),
                    Ok(event) => {
                        event.GetStatus().and_then(|s| s.ok()).map_err(|_| "Windows video encoding failed.")?;
                        match event.GetType().map_err(|_| "Windows video encoding failed.")? {
                            t if t == METransformNeedInput.0 as u32 => {
                                // Preserve driver-issued input credits. The
                                // in-flight counter separately bounds samples.
                                self.need_input = self.need_input.saturating_add(1)
                            }
                            t if t == METransformHaveOutput.0 as u32 => {
                                self.have_output = self.have_output.saturating_add(1)
                            }
                            _ => {}
                        }
                    }
                }
            }
        }
        Ok(())
    }
    pub(super) fn ready(&mut self) -> Result<bool> {
        self.poll()?;
        Ok(self.queue.ready() && (self.events.is_none() || self.need_input > 0))
    }
    pub(super) fn input(&mut self, data: &[u8], timestamp: u64, keyframe: bool) -> Result<bool> {
        if !self.queue.ready() {
            return Ok(false);
        }
        unsafe {
            (|| -> windows::core::Result<bool> {
                if keyframe && let Some(codec) = &self.codec {
                    let _ = codec.SetValue(&CODECAPI_AVEncVideoForceKeyFrame, &VARIANT::from(1u32));
                }
                let sample = MFCreateSample()?;
                let mut info = MFT_INPUT_STREAM_INFO::default();
                self.transform.GetInputStreamInfo(0, &mut info)?;
                if info.cbSize > 16 * 1024 * 1024 {
                    return Err(windows::core::Error::from_hresult(windows::Win32::Foundation::E_INVALIDARG));
                }
                let alignment = pixels::buffer_alignment(info.cbAlignment)
                    .ok_or_else(|| windows::core::Error::from_hresult(windows::Win32::Foundation::E_INVALIDARG))?;
                let buffer = MFCreateAlignedMemoryBuffer((data.len() as u32).max(info.cbSize), alignment)?;
                let mut pointer = std::ptr::null_mut();
                buffer.Lock(&mut pointer, None, None)?;
                if pointer.is_null() {
                    let _ = buffer.Unlock();
                    return Err(windows::core::Error::from_hresult(windows::Win32::Foundation::E_POINTER));
                }
                std::ptr::copy_nonoverlapping(data.as_ptr(), pointer, data.len());
                buffer.Unlock()?;
                buffer.SetCurrentLength(data.len() as u32)?;
                sample.AddBuffer(&buffer)?;
                sample.SetSampleTime((timestamp * 10) as i64)?;
                sample.SetSampleDuration(10_000_000 / self.fps as i64)?;
                if let Err(error) = self.transform.ProcessInput(0, &sample, 0) {
                    if error.code() == MF_E_NOTACCEPTING {
                        return Ok(false);
                    }
                    return Err(error);
                }
                self.queue.accepted(timestamp, Instant::now());
                self.need_input = self.need_input.saturating_sub(1);
                Ok(true)
            })()
            .map_err(|_| "Windows could not encode the screen. Try Save battery or HD text.".into())
        }
    }
    pub(super) fn output(&mut self) -> Result<Vec<(Vec<u8>, u64, bool)>> {
        self.poll()?;
        let mut frames = vec![];
        unsafe {
            for _ in 0..4 {
                if self.events.is_some() && self.have_output == 0 {
                    break;
                }
                let info =
                    self.transform.GetOutputStreamInfo(0).map_err(|_| "Windows could not read its video encoder.")?;
                let sample = if info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES.0 as u32 == 0 {
                    let sample = MFCreateSample().map_err(|_| "Windows could not allocate a video picture.")?;
                    if info.cbSize > 16 * 1024 * 1024 {
                        return Err("The Windows encoder requested an oversized picture.".into());
                    }
                    let alignment = pixels::buffer_alignment(info.cbAlignment)
                        .ok_or("The Windows encoder requested unsupported buffer alignment.")?;
                    sample
                        .AddBuffer(
                            &MFCreateAlignedMemoryBuffer(info.cbSize.max(1024 * 1024), alignment)
                                .map_err(|_| "Windows could not allocate a video picture.")?,
                        )
                        .map_err(|_| "Windows could not prepare a video picture.")?;
                    Some(sample)
                } else {
                    None
                };
                let mut output = MFT_OUTPUT_DATA_BUFFER { pSample: ManuallyDrop::new(sample), ..Default::default() };
                let mut status = 0;
                let result = self.transform.ProcessOutput(0, std::slice::from_mut(&mut output), &mut status);
                let sample = ManuallyDrop::take(&mut output.pSample);
                let events = ManuallyDrop::take(&mut output.pEvents);
                if let Some(events) = events {
                    let count = events.GetElementCount().map_err(|_| "Windows video events were unavailable.")?;
                    if count > 32 {
                        return Err("The Windows encoder returned too many video events.".into());
                    }
                    for index in 0..count {
                        if let Ok(event) = events.GetElement(index).and_then(|e| e.cast::<IMFMediaEvent>()) {
                            event.GetStatus().and_then(|s| s.ok()).map_err(|_| "Windows video encoding failed.")?;
                        }
                    }
                }
                if let Err(error) = result {
                    if error.code() == MF_E_TRANSFORM_NEED_MORE_INPUT {
                        if self.events.is_some() {
                            return Err("The Windows hardware encoder returned an invalid output event.".into());
                        }
                        break;
                    }
                    if error.code() == MF_E_TRANSFORM_STREAM_CHANGE {
                        // This stream negotiated baseline H.264 at a fixed
                        // size. An arbitrary driver-preferred type can violate
                        // that contract. Recover through the software encoder.
                        return Err(
                            "The Windows encoder changed its video format. Try HD text if this continues.".into()
                        );
                    }
                    return Err("Windows video encoding stopped. Start viewing again or try HD text.".into());
                }
                self.have_output = self.have_output.saturating_sub(1);
                if output.dwStatus == MFT_OUTPUT_DATA_BUFFER_NO_SAMPLE.0 as u32 {
                    // Event-only output leaves the caller's sample untouched.
                    // It is not a picture and must not release input capacity.
                    continue;
                }
                if output.dwStatus != 0 && output.dwStatus != MFT_OUTPUT_DATA_BUFFER_INCOMPLETE.0 as u32 {
                    return Err("The Windows encoder changed or removed its video stream.".into());
                }
                let sample = sample.ok_or("The Windows encoder returned no video picture.")?;
                let buffer =
                    sample.ConvertToContiguousBuffer().map_err(|_| "Windows returned an invalid video picture.")?;
                let mut pointer = std::ptr::null_mut();
                let mut length = 0;
                buffer
                    .Lock(&mut pointer, None, Some(&mut length))
                    .map_err(|_| "Windows could not read a video picture.")?;
                let data = if !pointer.is_null() && length > 0 && length <= 1024 * 1024 {
                    Some(std::slice::from_raw_parts(pointer, length as usize).to_vec())
                } else {
                    None
                };
                let _ = buffer.Unlock();
                let data = data.ok_or("This video picture is too large. Choose Save battery and try again.")?;
                let mut data = pixels::annex_b(&data).ok_or("Windows returned an unsupported H.264 picture.")?;
                let key = pixels::has_nal(&data, 5);
                if key && (!pixels::has_nal(&data, 7) || !pixels::has_nal(&data, 8)) {
                    if self.sequence.is_empty() {
                        let media = self
                            .transform
                            .GetOutputCurrentType(0)
                            .map_err(|_| "Windows video headers were unavailable.")?;
                        if let Ok(length) = media.GetBlobSize(&MF_MT_MPEG_SEQUENCE_HEADER)
                            && length <= 64 * 1024
                        {
                            let mut header = vec![0; length as usize];
                            if media.GetBlob(&MF_MT_MPEG_SEQUENCE_HEADER, &mut header, None).is_ok() {
                                self.sequence = pixels::annex_b(&header)
                                    .filter(|header| pixels::has_nal(header, 7) && pixels::has_nal(header, 8))
                                    .unwrap_or_default();
                            }
                        }
                    }
                    if self.sequence.is_empty() {
                        return Err("Windows did not provide H.264 picture headers. Try HD text.".into());
                    }
                    let mut combined = self.sequence.clone();
                    combined.extend_from_slice(&data);
                    data = combined;
                }
                if data.len() > 1024 * 1024 {
                    return Err("This video picture is too large. Choose Save battery and try again.".into());
                }
                let time = sample.GetSampleTime().map_err(|_| "Windows returned an untimed video picture.")?;
                let timestamp = time.max(0) as u64 / 10;
                self.queue.completed(timestamp);
                frames.push((data, timestamp, key));
            }
        }
        if self.queue.stalled(Instant::now()) {
            return Err("The Windows video encoder stalled. Start viewing again or try HD text.".into());
        }
        Ok(frames)
    }
    unsafe fn shutdown(transform: &IMFTransform) {
        unsafe {
            let _ = transform.ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
            let _ = transform.ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
            // Releasing COM references alone leaves async MFT event queues and
            // driver work alive. Shut them down before releasing the platform.
            if let Ok(shutdown) = transform.cast::<IMFShutdown>() {
                let _ = shutdown.Shutdown();
            }
        }
    }
}
impl Drop for Encoder {
    fn drop(&mut self) {
        unsafe {
            Self::shutdown(&self.transform);
            if let Some(activation) = &self.activation {
                let _ = activation.ShutdownObject();
            }
        }
    }
}

/// Encodes a synthetic color chart, without capturing the user's desktop.
/// Exercised by the disposable Windows self-test on x64 and ARM64.
pub(crate) fn self_test() -> Result<()> {
    std::thread::spawn(|| {
        unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) }
            .ok()
            .map_err(|_| "Could not initialize the video test.")?;
        let result = (|| {
            let mut encoder = Encoder::software((320, 180), 15, 400_000, DisplayProfile::ConstrainedBaseline)?;
            let mut image = vec![0; 320 * 180 * 4];
            for (i, p) in image.as_chunks_mut::<4>().0.iter_mut().enumerate() {
                p.copy_from_slice(&[if i % 320 < 160 { 255 } else { 0 }, 128, 64, 255]);
            }
            let frame = pixels::nv12(&image, 320, 180);
            let start = Instant::now();
            let mut timestamp = 0;
            while start.elapsed() < Duration::from_secs(8) {
                if encoder.ready()? {
                    encoder.input(&frame, timestamp, true)?;
                    timestamp += 66_667;
                }
                if encoder
                    .output()?
                    .iter()
                    .any(|(data, _, key)| *key && pixels::has_nal(data, 7) && pixels::has_nal(data, 8))
                {
                    return Ok(());
                }
                std::thread::sleep(Duration::from_millis(10));
            }
            Err("The synthetic H.264 encoder test timed out.".into())
        })();
        unsafe {
            CoUninitialize();
        }
        result
    })
    .join()
    .map_err(|_| "The Windows video test failed.".to_string())?
}
