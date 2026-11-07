//! Opt-in PowerShell, confined to a kill-on-close Windows Job Object.
//! The process starts suspended so no child can escape before assignment.
use crate::actions::{RunningCommand, SHELL_REPORT_WINDOW, STOPPED, read_errors, shell_failure};
use crate::input::{Interrupts, SessionId};
use base64::Engine as _;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::sync::Arc;
use std::time::{Duration, Instant};
use windows_sys::Win32::Foundation::{
    GENERIC_READ, GENERIC_WRITE, HANDLE, INVALID_HANDLE_VALUE, TRUE, WAIT_OBJECT_0, WAIT_TIMEOUT,
};
use windows_sys::Win32::Globalization::{CP_OEMCP, MultiByteToWideChar};
use windows_sys::Win32::Security::SECURITY_ATTRIBUTES;
use windows_sys::Win32::Storage::FileSystem::{
    CREATE_NEW, CreateFileW, FILE_ATTRIBUTE_TEMPORARY, FILE_FLAG_DELETE_ON_CLOSE, FILE_SHARE_DELETE, FILE_SHARE_READ,
    FILE_SHARE_WRITE, OPEN_EXISTING,
};
use windows_sys::Win32::System::{JobObjects::*, Threading::*};

/// A command that runs longer than this is stopped, with everything it started.
const TIMEOUT: Duration = Duration::from_secs(300);
const POLL_MS: u32 = 50;

enum Outcome {
    Exited(u32),
    Stopped,
    Lost,
}

pub fn run(command: &str, interrupts: Arc<Interrupts>, session: SessionId, epoch: u64) -> Result<(), String> {
    let running = RunningCommand::start()?;
    let stopped = move || interrupts.should_stop(session) || interrupts.shell_epoch() != epoch;
    let utf16: Vec<u8> = command.encode_utf16().flat_map(u16::to_le_bytes).collect();
    let encoded = base64::prelude::BASE64_STANDARD.encode(utf16);
    let executable = std::path::PathBuf::from(std::env::var_os("SystemRoot").unwrap_or_else(|| "C:\\Windows".into()))
        .join("System32\\WindowsPowerShell\\v1.0\\powershell.exe");
    let application: Vec<u16> = executable.as_os_str().to_string_lossy().encode_utf16().chain([0]).collect();
    let mut line: Vec<u16> =
        format!("\"{}\" -NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}", executable.display())
            .encode_utf16()
            .chain([0])
            .collect();
    let null = inheritable_file("NUL", GENERIC_READ | GENERIC_WRITE, OPEN_EXISTING, 0)
        .ok_or("Couldn't prepare the automation's input and output.")?;
    let errors = error_file();
    // SAFETY: every pointer passed below refers to a live local or owned
    // handle, and the attribute list outlives CreateProcessW.
    unsafe {
        let job = CreateJobObjectW(std::ptr::null(), std::ptr::null());
        if job.is_null() {
            return Err("Couldn't create an automation job.".into());
        }
        let job = OwnedHandle::from_raw_handle(job);
        if !set_kill_on_close(&job, true) {
            return Err("Couldn't confine the automation job.".into());
        }
        let stderr = errors.as_ref().map_or(null.as_raw_handle(), |file| file.as_raw_handle()) as HANDLE;
        // Only the standard handles reach PowerShell, not every inheritable
        // handle WinPalm happens to hold.
        let mut inherit: Vec<HANDLE> = vec![null.as_raw_handle() as HANDLE];
        if stderr != inherit[0] {
            inherit.push(stderr);
        }
        let mut size = 0;
        InitializeProcThreadAttributeList(std::ptr::null_mut(), 1, 0, &mut size);
        let mut storage = vec![0usize; size.div_ceil(size_of::<usize>())];
        let attributes: LPPROC_THREAD_ATTRIBUTE_LIST = storage.as_mut_ptr().cast();
        if InitializeProcThreadAttributeList(attributes, 1, 0, &mut size) == 0 {
            return Err("Couldn't prepare the automation.".into());
        }
        let updated = UpdateProcThreadAttribute(
            attributes,
            0,
            PROC_THREAD_ATTRIBUTE_HANDLE_LIST as usize,
            inherit.as_ptr().cast(),
            inherit.len() * size_of::<HANDLE>(),
            std::ptr::null_mut(),
            std::ptr::null(),
        );
        let mut startup: STARTUPINFOEXW = std::mem::zeroed();
        startup.StartupInfo.cb = size_of_val(&startup) as u32;
        startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        startup.StartupInfo.hStdInput = inherit[0];
        startup.StartupInfo.hStdOutput = inherit[0];
        startup.StartupInfo.hStdError = stderr;
        startup.lpAttributeList = attributes;
        let mut process: PROCESS_INFORMATION = std::mem::zeroed();
        let created = updated != 0
            && CreateProcessW(
                application.as_ptr(),
                line.as_mut_ptr(),
                std::ptr::null(),
                std::ptr::null(),
                TRUE,
                CREATE_SUSPENDED | CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT,
                std::ptr::null(),
                std::ptr::null(),
                &startup.StartupInfo,
                &mut process,
            ) != 0;
        DeleteProcThreadAttributeList(attributes);
        drop(null);
        if !created {
            return Err("Couldn't start Windows PowerShell.".into());
        }
        let handle = OwnedHandle::from_raw_handle(process.hProcess);
        let thread = OwnedHandle::from_raw_handle(process.hThread);
        if AssignProcessToJobObject(job.as_raw_handle(), handle.as_raw_handle()) == 0 {
            TerminateProcess(handle.as_raw_handle(), 1);
            return Err("Windows couldn't confine the automation; it was not run.".into());
        }
        if stopped() {
            return Err(STOPPED.into());
        }
        if ResumeThread(thread.as_raw_handle()) == u32::MAX {
            return Err("Couldn't resume the automation.".into());
        }
        drop(thread);
        let (done, finished) = std::sync::mpsc::channel();
        std::thread::spawn(move || {
            let _running = running;
            let deadline = Instant::now() + TIMEOUT;
            let outcome = loop {
                match WaitForSingleObject(handle.as_raw_handle(), POLL_MS) {
                    WAIT_OBJECT_0 => {
                        let mut code = 1;
                        GetExitCodeProcess(handle.as_raw_handle(), &mut code);
                        break Outcome::Exited(code);
                    }
                    WAIT_TIMEOUT if stopped() || Instant::now() >= deadline => break Outcome::Stopped,
                    WAIT_TIMEOUT => {}
                    _ => break Outcome::Lost,
                }
            };
            if matches!(outcome, Outcome::Exited(_)) {
                // Like a shell on the Mac, programs the command started, such
                // as an editor opened with Start-Process, outlive it.
                set_kill_on_close(&job, false);
            }
            // Otherwise this stops PowerShell and everything it started.
            drop(job);
            let _ = done.send(outcome);
        });
        match finished.recv_timeout(SHELL_REPORT_WINDOW) {
            Ok(Outcome::Stopped) => Err(STOPPED.into()),
            Ok(Outcome::Exited(code)) if code != 0 => {
                let text = decode(&errors.map(read_errors).unwrap_or_default());
                Err(shell_failure(Some(code as i32), &text))
            }
            // Still running, or finished fine: the command carries on by itself.
            _ => Ok(()),
        }
    }
}

fn set_kill_on_close(job: &OwnedHandle, kill: bool) -> bool {
    // SAFETY: a zeroed limit structure of the size passed, for a job handle we own.
    unsafe {
        let mut limits: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = std::mem::zeroed();
        limits.BasicLimitInformation.LimitFlags = if kill { JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE } else { 0 };
        SetInformationJobObject(
            job.as_raw_handle(),
            JobObjectExtendedLimitInformation,
            &limits as *const _ as _,
            size_of_val(&limits) as u32,
        ) != 0
    }
}

fn inheritable_file(path: &str, access: u32, disposition: u32, flags: u32) -> Option<OwnedHandle> {
    let wide: Vec<u16> = path.encode_utf16().chain([0]).collect();
    let security = SECURITY_ATTRIBUTES {
        nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
        lpSecurityDescriptor: std::ptr::null_mut(),
        bInheritHandle: TRUE,
    };
    // SAFETY: a NUL-terminated path and a live security attribute structure.
    let handle = unsafe {
        CreateFileW(
            wide.as_ptr(),
            access,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            &security,
            disposition,
            flags,
            std::ptr::null_mut(),
        )
    };
    if handle == INVALID_HANDLE_VALUE || handle.is_null() {
        return None;
    }
    // SAFETY: a valid handle that nothing else owns.
    Some(unsafe { OwnedHandle::from_raw_handle(handle) })
}

/// A temporary file for the command's error output, removed by Windows once
/// PowerShell and WinPalm close it. Unlike a pipe, it never fills up, so a
/// long-running command can't block writing to it.
fn error_file() -> Option<std::fs::File> {
    use std::sync::atomic::{AtomicU64, Ordering};
    static NEXT: AtomicU64 = AtomicU64::new(0);
    let path = std::env::temp_dir().join(format!(
        "winpalm-shell-{}-{}.txt",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    ));
    let handle = inheritable_file(
        &path.to_string_lossy(),
        GENERIC_READ | GENERIC_WRITE,
        CREATE_NEW,
        FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE,
    )?;
    Some(std::fs::File::from(handle))
}

/// PowerShell writes redirected errors in the console's OEM code page, while
/// programs such as git write UTF-8.
fn decode(bytes: &[u8]) -> String {
    if let Ok(text) = std::str::from_utf8(bytes) {
        return text.to_string();
    }
    let Ok(len) = i32::try_from(bytes.len()) else { return String::from_utf8_lossy(bytes).into_owned() };
    // SAFETY: lengths match the buffers passed.
    unsafe {
        let needed = MultiByteToWideChar(CP_OEMCP, 0, bytes.as_ptr(), len, std::ptr::null_mut(), 0);
        if needed <= 0 {
            return String::from_utf8_lossy(bytes).into_owned();
        }
        let mut wide = vec![0u16; needed as usize];
        let written = MultiByteToWideChar(CP_OEMCP, 0, bytes.as_ptr(), len, wide.as_mut_ptr(), needed);
        String::from_utf16_lossy(&wide[..written.max(0) as usize])
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn run_now(command: &str) -> Result<(), String> {
        let interrupts = Arc::new(Interrupts::default());
        let epoch = interrupts.shell_epoch();
        run(command, interrupts, 1, epoch)
    }

    #[test]
    fn a_failing_powershell_command_says_why() {
        let error = run_now("Write-Output fine; nosuchcommand-winpalm").unwrap_err();
        assert!(error.starts_with("The command failed (exit code 1): nosuchcommand-winpalm"), "{error}");
        assert_eq!(run_now("exit 3").unwrap_err(), "The command failed (exit code 3).");
        run_now("Write-Output fine").unwrap();
    }

    #[test]
    fn pause_stops_a_running_command() {
        let dir = tempfile::tempdir().unwrap();
        let marker = dir.path().join("done");
        let interrupts = Arc::new(Interrupts::default());
        let epoch = interrupts.shell_epoch();
        let command = format!("Start-Sleep -Seconds 3; New-Item -ItemType File '{}'", marker.display());
        run(&command, interrupts.clone(), 1, epoch).unwrap();
        interrupts.set_paused(true);
        std::thread::sleep(Duration::from_millis(3500));
        assert!(!marker.exists(), "the paused command finished");
    }
}
