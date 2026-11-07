//! Running deck actions. Each device has one worker so a button's steps
//! never interleave with the next button's.

use std::sync::Arc;
use std::time::{Duration, Instant};

use tokio::sync::{mpsc, oneshot};

use crate::api::NativeCommand;
use crate::deck::{MAX_DELAY_MS, MAX_DEPTH};
use crate::host::Shared;
use crate::input::{Command, Input, Interrupts, SessionId, TypeMode};
use crate::keycodes::{self as kc, KeyCode, mods};
use crate::protocol::{Action, MouseButton, PressOp, ServerMessage, SystemAction};

pub(crate) struct Job {
    pub action: Action,
    pub id: Option<u32>,
}

/// How long the pasted text stays on the clipboard before it is restored.
/// Apps read the pasteboard asynchronously after ⌘V.
const PASTE_RESTORE_DELAY: Duration = Duration::from_millis(450);
const DELAY_SLICE: Duration = Duration::from_millis(50);
pub(crate) const STOPPED: &str = "Stopped because control was paused or the device disconnected.";
#[cfg(unix)]
const SHELL_POLL: Duration = Duration::from_millis(50);
#[cfg(unix)]
const SHELL_KILL_GRACE: Duration = Duration::from_secs(1);
/// How long a button waits for its command before carrying on, so a command
/// that fails right away can say why on the phone. PowerShell itself takes a
/// few hundred milliseconds to start.
pub(crate) const SHELL_REPORT_WINDOW: Duration = Duration::from_millis(1500);
/// How much of a failed command's error output the phone is shown.
pub(crate) const SHELL_ERROR_CHARS: usize = 240;
pub(crate) const SHELL_ERROR_READ: u64 = 16 * 1024;
/// Commands still being watched. Each has a thread, so a button pressed over
/// and over can't pile up threads without end.
const MAX_RUNNING_COMMANDS: usize = 32;
static RUNNING_COMMANDS: std::sync::atomic::AtomicUsize = std::sync::atomic::AtomicUsize::new(0);

/// Counts a command being watched until dropped.
pub(crate) struct RunningCommand(());

impl RunningCommand {
    pub(crate) fn start() -> Result<Self, String> {
        use std::sync::atomic::Ordering;
        if RUNNING_COMMANDS.fetch_add(1, Ordering::SeqCst) >= MAX_RUNNING_COMMANDS {
            RUNNING_COMMANDS.fetch_sub(1, Ordering::SeqCst);
            return Err("Too many PowerShell commands are still running. Pause WinPalm on the PC to stop them.".into());
        }
        Ok(Self(()))
    }
}

impl Drop for RunningCommand {
    fn drop(&mut self) {
        RUNNING_COMMANDS.fetch_sub(1, std::sync::atomic::Ordering::SeqCst);
    }
}

pub(crate) async fn worker(shared: Arc<Shared>, session: SessionId, mut jobs: mpsc::Receiver<Job>) {
    while let Some(job) = jobs.recv().await {
        let task_shared = shared.clone();
        let action = job.action;
        let result = tokio::task::spawn_blocking(move || execute(&task_shared, session, &action, 0))
            .await
            .unwrap_or_else(|_| Err("The action crashed.".into()));
        if job.id.is_some() || result.is_err() {
            let (ok, message) = match result {
                Ok(()) => (true, None),
                Err(message) => (false, Some(message)),
            };
            shared.send_to(session, ServerMessage::ActionResult { id: job.id, ok, message });
        }
    }
}

fn input(shared: &Shared, session: SessionId, input: Input) {
    shared.engine(Command::Input { session, input });
}

enum Plan {
    Code(KeyCode, u16),
    Letter(char, KeyCode, u16),
    Native(NativeCommand),
}

fn system_plan(name: SystemAction) -> Plan {
    use SystemAction as S;
    use mods::{COMMAND as WIN, CONTROL as CTRL, OPTION as ALT, SHIFT};
    let letter = |ch: char, mods: u16| Plan::Letter(ch, kc::named_key(&ch.to_string()).unwrap_or(kc::KEY_A), mods);
    match name {
        S::MissionControl | S::AppExpose => Plan::Code(kc::KEY_TAB, WIN),
        S::Launchpad => Plan::Native(NativeCommand::Launchpad),
        S::LockScreen => Plan::Native(NativeCommand::LockScreen),
        S::SleepDisplay => Plan::Native(NativeCommand::SleepDisplay),
        S::Sleep => Plan::Native(NativeCommand::Sleep),
        S::ShowDesktop => letter('d', WIN),
        S::Spotlight => letter('s', WIN),
        S::SpaceLeft => Plan::Code(kc::KEY_LEFT, CTRL | WIN),
        S::SpaceRight => Plan::Code(kc::KEY_RIGHT, CTRL | WIN),
        S::AppSwitcher => Plan::Code(kc::KEY_TAB, ALT),
        S::Screenshot => Plan::Native(NativeCommand::Screenshot),
        S::ScreenshotArea | S::ScreenshotTool => letter('s', WIN | SHIFT),
        S::ForceQuit => Plan::Code(kc::KEY_ESCAPE, CTRL | SHIFT),
        S::EmojiPicker => Plan::Code(0x2f, WIN),
        S::CloseWindow | S::QuitApp => Plan::Native(NativeCommand::CloseWindow),
        S::CloseTab => letter('w', CTRL),
        S::Minimize | S::HideApp => Plan::Native(NativeCommand::Minimize),
        S::Fullscreen => Plan::Code(0x67, 0),
        S::Undo => letter('z', CTRL),
        S::Redo => letter('y', CTRL),
        S::Copy => letter('c', CTRL),
        S::Cut => letter('x', CTRL),
        S::Paste => letter('v', CTRL),
        S::SelectAll => letter('a', CTRL),
        S::Find => letter('f', CTRL),
        S::NewTab => letter('t', CTRL),
        S::Reload => letter('r', CTRL),
        S::NextTab => Plan::Code(kc::KEY_TAB, CTRL),
        S::PreviousTab => Plan::Code(kc::KEY_TAB, CTRL | SHIFT),
        S::Back => Plan::Code(kc::KEY_LEFT, ALT),
        S::Forward => Plan::Code(kc::KEY_RIGHT, ALT),
        S::ZoomIn => Plan::Code(kc::KEY_EQUAL, CTRL),
        S::ZoomOut => Plan::Code(kc::KEY_MINUS, CTRL),
        S::ActualSize => Plan::Code(0x1d, CTRL),
    }
}

fn single_letter(text: &str) -> Option<char> {
    let mut chars = text.chars();
    match (chars.next(), chars.next()) {
        (Some(c), None) if c.is_alphabetic() => Some(c),
        _ => None,
    }
}

/// `queued` is told (or dropped) once ⌘V is in the input queue, so input the
/// device sends after the paste can't be posted before it.
pub(crate) fn paste(
    shared: &Shared,
    session: SessionId,
    text: &str,
    queued: oneshot::Sender<()>,
) -> Result<(), String> {
    let _serial = shared.paste_lock.lock().unwrap_or_else(|p| p.into_inner());
    if shared.should_stop(session) {
        return Ok(());
    }
    if !shared.delegate.begin_paste(text.to_string()) {
        return Err("Couldn't use the clipboard.".into());
    }
    input(shared, session, Input::CharTap { ch: 'v', fallback: kc::KEY_V, mods: mods::CONTROL });
    let _ = queued.send(());
    // Restoring the clipboard before ⌘V is posted, say behind a long text
    // from another device, would paste the person's previous clipboard.
    shared.finish_engine();
    std::thread::sleep(PASTE_RESTORE_DELAY);
    shared.delegate.end_paste();
    Ok(())
}

/// Why a link can't be opened, or `None` when it can.
fn link_problem(url: &str, allow_shell: bool) -> Option<&'static str> {
    const NOT_ALLOWED: &str = "That kind of link isn't allowed.";
    let Some((scheme, _)) = url.trim().split_once(':') else { return Some(NOT_ALLOWED) };
    let scheme = scheme.to_ascii_lowercase();
    if scheme.is_empty() || !scheme.chars().all(|c| c.is_ascii_alphanumeric() || "+-.".contains(c)) {
        return Some(NOT_ALLOWED);
    }
    match scheme.as_str() {
        "javascript" | "data" => Some(NOT_ALLOWED),
        "http" | "https" | "mailto" => None,
        // Other schemes reach whatever registered them, from file links that
        // can launch scripts to ms-msdt: and search-ms:. Treat them like
        // PowerShell commands.
        _ if !allow_shell => Some(
            "Only web and email links open while PowerShell commands are off. Turn them on in WinPalm settings on the PC for other links.",
        ),
        _ => None,
    }
}

/// Waits for the input queued before a step that acts outside the engine, so
/// an app never comes forward mid-typing and a command never reads the
/// clipboard before Ctrl+C lands.
fn settle(shared: &Shared, session: SessionId) -> Result<(), String> {
    shared.flush_engine();
    if shared.should_stop(session) {
        return Err(STOPPED.into());
    }
    Ok(())
}

/// Runs one action. Steps stop as soon as the device disconnects or input is
/// paused, even partway through a long sequence.
pub(crate) fn execute(shared: &Shared, session: SessionId, action: &Action, depth: usize) -> Result<(), String> {
    if shared.should_stop(session) {
        return Err(STOPPED.into());
    }
    match action {
        Action::Keys { code, mods, char } => {
            let input_event = match char.as_deref().and_then(single_letter) {
                Some(ch) => Input::CharTap { ch, fallback: *code, mods: *mods },
                None => Input::Tap { code: *code, mods: *mods },
            };
            input(shared, session, input_event);
        }
        Action::Text { text, enter } => {
            input(shared, session, Input::Text { text: text.clone(), mode: TypeMode::Unicode });
            if *enter {
                input(shared, session, Input::Tap { code: kc::KEY_RETURN, mods: 0 });
            }
        }
        Action::Media { key } => {
            if matches!(
                key,
                crate::protocol::MediaKey::FastForward
                    | crate::protocol::MediaKey::Rewind
                    | crate::protocol::MediaKey::BrightnessUp
                    | crate::protocol::MediaKey::BrightnessDown
                    | crate::protocol::MediaKey::IlluminationUp
                    | crate::protocol::MediaKey::IlluminationDown
                    | crate::protocol::MediaKey::IlluminationToggle
            ) {
                if !shared.delegate.platform_media(*key) {
                    return Err("This PC does not expose that hardware control to Windows.".into());
                }
            } else {
                input(shared, session, Input::Media { key: *key, op: PressOp::Click });
            }
        }
        Action::App { id, op } => {
            settle(shared, session)?;
            if !shared.delegate.app_action(id.clone(), *op) {
                return Err(format!("Couldn't find the app “{id}” on the PC."));
            }
        }
        Action::Url { url } => {
            let url = url.trim();
            if let Some(problem) = link_problem(url, shared.config().allow_shell) {
                return Err(problem.into());
            }
            settle(shared, session)?;
            if !shared.delegate.open_url(url.to_string()) {
                return Err("The PC couldn't open that link.".into());
            }
        }
        Action::System { name } => match system_plan(*name) {
            Plan::Code(code, mods) => input(shared, session, Input::Tap { code, mods }),
            Plan::Letter(ch, fallback, mods) => input(shared, session, Input::CharTap { ch, fallback, mods }),
            Plan::Native(command) => {
                // Let pending keystrokes land before the screen locks or sleeps,
                // and don't lock or sleep if Pause was pressed meanwhile.
                settle(shared, session)?;
                if !shared.delegate.native(command) {
                    return Err("The PC couldn't do that.".into());
                }
            }
        },
        Action::Shortcut { name } => {
            let epoch = shared.interrupts.shell_epoch();
            if !shared.config().allow_shell {
                return Err("PowerShell automations are turned off in WinPalm settings.".into());
            }
            settle(shared, session)?;
            let command = shared
                .delegate
                .automation_command(name.clone())
                .ok_or_else(|| format!("No automation named “{name}” is configured on this PC."))?;
            run_shell(&command, shared.interrupts.clone(), session, epoch)?;
        }
        Action::Shell { command } => {
            let epoch = shared.interrupts.shell_epoch();
            if !shared.config().allow_shell {
                return Err("Shell commands are turned off. Turn them on in WinPalm settings on the PC.".into());
            }
            settle(shared, session)?;
            run_shell(command, shared.interrupts.clone(), session, epoch)?;
        }
        Action::Mouse { button, clicks } => {
            let button: MouseButton = *button;
            input(shared, session, Input::Click { button, clicks: (*clicks).clamp(1, 3), mods: 0 });
        }
        Action::Delay { ms } => {
            shared.flush_engine();
            let until = Instant::now() + Duration::from_millis((*ms).min(MAX_DELAY_MS) as u64);
            while let Some(left) = until.checked_duration_since(Instant::now()).filter(|d| !d.is_zero()) {
                if shared.should_stop(session) {
                    return Err(STOPPED.into());
                }
                std::thread::sleep(left.min(DELAY_SLICE));
            }
        }
        Action::Sequence { steps } => {
            if depth >= MAX_DEPTH {
                return Err("The sequence is nested too deeply.".into());
            }
            for step in steps {
                execute(shared, session, step, depth + 1)?;
            }
        }
    }
    Ok(())
}

/// Runs a command for macOS development builds of the core, mirroring the
/// Windows runner in [crate::shell].
#[cfg(unix)]
fn run_shell(command: &str, interrupts: Arc<Interrupts>, session: SessionId, epoch: u64) -> Result<(), String> {
    use std::os::unix::process::CommandExt as _;
    use std::process::Stdio;
    let running = RunningCommand::start()?;
    let errors = error_file();
    let stderr = errors.as_ref().and_then(|f| f.try_clone().ok()).map_or_else(Stdio::null, Stdio::from);
    let mut child = std::process::Command::new("/bin/zsh")
        .arg("-lc")
        .arg(command)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(stderr)
        // Its own process group, so Pause can stop everything it started.
        .process_group(0)
        .spawn()
        .map_err(|e| format!("Couldn't run the command: {e}"))?;
    let group = child.id() as libc::pid_t;
    let (done, finished) = std::sync::mpsc::channel();
    // Reap the process when it finishes so it never lingers as a zombie, and
    // stop it if input is paused or the device leaves while it runs.
    std::thread::spawn(move || {
        let _running = running;
        let stopped = || interrupts.should_stop(session) || interrupts.shell_epoch() != epoch;
        let mut signalled: Option<Instant> = None;
        // As on Windows, what a finished command started carries on.
        let status = loop {
            match child.try_wait() {
                Ok(None) => {}
                Ok(Some(status)) => break Some(status),
                Err(_) => break None,
            }
            if stopped() {
                match signalled {
                    None => {
                        // SAFETY: signals only the process group created above.
                        unsafe { libc::killpg(group, libc::SIGTERM) };
                        signalled = Some(Instant::now());
                    }
                    Some(at) if at.elapsed() >= SHELL_KILL_GRACE => {
                        // SAFETY: as above.
                        unsafe { libc::killpg(group, libc::SIGKILL) };
                    }
                    Some(_) => {}
                }
            }
            std::thread::sleep(SHELL_POLL);
        };
        let _ = done.send((status, stopped()));
    });
    match finished.recv_timeout(SHELL_REPORT_WINDOW) {
        Ok((_, true)) => Err(STOPPED.into()),
        Ok((Some(status), false)) if !status.success() => {
            Err(shell_failure(status.code(), &String::from_utf8_lossy(&errors.map(read_errors).unwrap_or_default())))
        }
        // Still running, or finished fine: the command carries on by itself.
        _ => Ok(()),
    }
}

/// An unlinked temporary file for a command's error output. Unlike a pipe,
/// it never fills up, so a long-running command can't block on it or be
/// killed writing to it after WinPalm stopped reading.
#[cfg(unix)]
fn error_file() -> Option<std::fs::File> {
    use std::os::unix::fs::OpenOptionsExt as _;
    use std::sync::atomic::{AtomicU64, Ordering};
    static NEXT: AtomicU64 = AtomicU64::new(0);
    let path = std::env::temp_dir().join(format!(
        "winpalm-shell-{}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    ));
    let file = std::fs::OpenOptions::new().read(true).write(true).create_new(true).mode(0o600).open(&path).ok()?;
    let _ = std::fs::remove_file(&path);
    Some(file)
}

#[cfg(windows)]
fn run_shell(command: &str, interrupts: Arc<Interrupts>, session: SessionId, epoch: u64) -> Result<(), String> {
    crate::shell::run(command, interrupts, session, epoch)
}

/// The start of what a command wrote to standard error.
pub(crate) fn read_errors(mut file: std::fs::File) -> Vec<u8> {
    use std::io::{Read as _, Seek as _};
    let mut bytes = Vec::new();
    if file.seek(std::io::SeekFrom::Start(0)).is_ok() {
        let _ = file.take(SHELL_ERROR_READ).read_to_end(&mut bytes);
    }
    bytes
}

/// Explains a failed command with the reason it gave on standard error,
/// such as "The term 'foo' is not recognized\u2026".
pub(crate) fn shell_failure(code: Option<i32>, errors: &str) -> String {
    let how = match code {
        Some(code) => format!("exit code {code}"),
        None => "stopped by a signal".into(),
    };
    match error_reason(errors) {
        Some(detail) => format!("The command failed ({how}): {detail}"),
        None => format!("The command failed ({how})."),
    }
}

/// PowerShell writes each error as its message, a location line ("At
/// line:1 char:1", in the PC's language), the failing code and two
/// "+ CategoryInfo" and "+ FullyQualifiedErrorId" lines. Report the last
/// error's message. Other programs' errors end with their reason, so use
/// the last line.
fn error_reason(errors: &str) -> Option<String> {
    let lines: Vec<&str> = errors.lines().map(str::trim).collect();
    let record_end = lines.iter().rposition(|line| line.starts_with("+ CategoryInfo"));
    let message = match record_end {
        Some(end) => {
            let code = (0..end).rev().take_while(|&i| lines[i].starts_with('+')).last().unwrap_or(end);
            let begin = lines[..code]
                .iter()
                .rposition(|line| line.starts_with("+ FullyQualifiedErrorId") || line.is_empty())
                .map_or(0, |i| i + 1);
            let mut block: Vec<&str> = lines[begin..code].iter().copied().filter(|l| !l.is_empty()).collect();
            if block.len() > 1 {
                block.pop();
            }
            block.join(" ")
        }
        None => lines.iter().rfind(|line| !line.is_empty())?.to_string(),
    };
    if message.is_empty() {
        return None;
    }
    let mut message: String = message.chars().take(SHELL_ERROR_CHARS).collect();
    if message.chars().count() == SHELL_ERROR_CHARS {
        message.push('…');
    }
    Some(message)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn letters_follow_the_layout_but_digits_use_positions() {
        assert!(matches!(system_plan(SystemAction::Copy), Plan::Letter('c', kc::KEY_C, mods::CONTROL)));
        assert!(
            matches!(system_plan(SystemAction::ScreenshotArea), Plan::Letter('s', _, m) if m == mods::COMMAND | mods::SHIFT)
        );
        assert_eq!(single_letter("q"), Some('q'));
        assert_eq!(single_letter("["), None);
        assert_eq!(single_letter("qq"), None);
    }

    #[test]
    fn only_web_and_email_links_open_without_shell_permission() {
        let allowed = |url: &str, shell: bool| link_problem(url, shell).is_none();
        assert!(allowed("https://example.com", false));
        assert!(allowed(" https://example.com\n", false), "pasted links often carry spaces");
        assert!(allowed("mailto:me@example.com", false));
        for url in ["ms-msdt:/id/test", "search-ms:query=x", "file:///C:/x.bat", "ssh://host", "SMB://server/share"] {
            assert!(!allowed(url, false), "{url}");
            assert!(allowed(url, true), "{url}");
        }
        assert!(!allowed("no scheme", false));
        assert!(!allowed("JavaScript:alert(1)", true));
        assert!(!allowed("data:text/html,hi", true));
    }

    #[test]
    fn powershell_errors_report_their_message() {
        let not_found = "foo : The term 'foo' is not recognized as the name of a cmdlet, function, script file, or operable program.\r\nAt line:1 char:1\r\n+ foo\r\n+ ~~~\r\n    + CategoryInfo          : ObjectNotFound: (foo:String) [], CommandNotFoundException\r\n    + FullyQualifiedErrorId : CommandNotFoundException\r\n \r\n";
        assert_eq!(
            shell_failure(Some(1), not_found),
            "The command failed (exit code 1): foo : The term 'foo' is not recognized as the name of a cmdlet, function, script file, or operable program."
        );
        // The last of several errors, wrapped over two lines, in another language.
        let two = format!(
            "{not_found}Get-Item : Der Pfad \"C:\\nope\" kann nicht gefunden werden,\r\nweil er nicht vorhanden ist.\r\nIn Zeile:1 Zeichen:6\r\n+ foo; Get-Item C:\\nope\r\n+      ~~~~~~~~~~~~~~~~\r\n    + CategoryInfo          : ObjectNotFound: (C:\\nope:String) [Get-Item], ItemNotFoundException\r\n    + FullyQualifiedErrorId : PathNotFound,Microsoft.PowerShell.Commands.GetItemCommand\r\n"
        );
        assert_eq!(
            error_reason(&two).unwrap(),
            "Get-Item : Der Pfad \"C:\\nope\" kann nicht gefunden werden, weil er nicht vorhanden ist."
        );
        // A program's own error output, and none at all.
        assert_eq!(error_reason("warning: x\nfatal: not a git repository\n").unwrap(), "fatal: not a git repository");
        assert_eq!(shell_failure(Some(3), " \r\n"), "The command failed (exit code 3).");
        assert!(error_reason(&"x".repeat(500)).unwrap().ends_with('…'));
    }

    #[test]
    #[cfg(unix)]
    fn pause_stops_running_shell_commands() {
        let dir = tempfile::tempdir().unwrap();
        let marker = dir.path().join("done");
        let interrupts = Arc::new(Interrupts::default());
        // Still running when the button stops waiting for it.
        run_shell(&format!("sleep 3; touch '{}'", marker.display()), interrupts.clone(), 1, interrupts.shell_epoch())
            .unwrap();
        std::thread::sleep(Duration::from_millis(300));
        interrupts.set_paused(true);
        std::thread::sleep(Duration::from_millis(2600));
        assert!(!marker.exists(), "the paused command never finished");
    }

    #[test]
    #[cfg(unix)]
    fn turning_shell_commands_off_stops_the_running_ones() {
        let dir = tempfile::tempdir().unwrap();
        let marker = dir.path().join("done");
        let interrupts = Arc::new(Interrupts::default());
        let epoch = interrupts.shell_epoch();
        run_shell(&format!("sleep 3; touch '{}'", marker.display()), interrupts.clone(), 1, epoch).unwrap();
        std::thread::sleep(Duration::from_millis(300));
        interrupts.cancel_shell();
        std::thread::sleep(Duration::from_millis(2600));
        assert!(!marker.exists(), "the command was stopped");
        let later = dir.path().join("later");
        run_shell(&format!("touch '{}'", later.display()), interrupts.clone(), 1, interrupts.shell_epoch()).unwrap();
        assert!(later.exists(), "commands started after turning them on again run");
    }

    #[test]
    #[cfg(unix)]
    fn a_failing_shell_command_says_why() {
        let interrupts = Arc::new(Interrupts::default());
        let epoch = interrupts.shell_epoch();
        let error =
            run_shell("echo first >&2; echo 'no such thing' >&2; exit 3", interrupts.clone(), 1, epoch).unwrap_err();
        assert_eq!(error, "The command failed (exit code 3): no such thing");
        let error = run_shell("exit 1", interrupts, 1, epoch).unwrap_err();
        assert_eq!(error, "The command failed (exit code 1).");
    }
}
