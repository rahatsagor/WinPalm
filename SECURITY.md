# Security

WinPalm lets paired devices control a Windows desktop. Pair only devices you trust, use a private network, and use trusted HTTPS where traffic could be observed. Plain HTTP does not protect a reconnect token from someone who can observe that connection.

## Pairing and access

QR pairing uses a single-use random code in the URL fragment. PIN and explicit-PC-approval pairing are optional, rate limited, and require the PC owner to display or approve the request. Reconnect tokens are random; only their hashes are persisted on the PC. Remove a device to revoke its token, close its sessions and remove its backed-up phone settings.

The server validates HTTP hosts, same-origin WebSocket upgrades, request sizes, static paths and deck contents. Windows drive paths and alternate data stream syntax are rejected. A session key protects icon requests. Custom hostnames and loopback reverse-proxy forwarding are owner opt-ins. Do not expose the server to the public internet or enable proxy trust for an untrusted local proxy.

## Windows boundary

The app uses SendInput at the current user's integrity level. It does not install a service, driver or input hook, request UIAccess, or bypass UAC. Secure desktops and higher-integrity foreground processes are refused. Closing or losing a session releases its input; an idle-device watchdog also releases keys and buttons. Pause blocks control, cancels dictation and interrupts queued text and running shell jobs. Completed native operations and apps already launched are not undone.

PowerShell and named automations are disabled by default. Both require the owner to enable command access on the PC. They inherit that owner's user permissions and can change or delete files. Jobs start suspended, enter a kill-on-close Job Object before execution, and stop on pause, disconnect, revocation, command-disable or a five-minute timeout. Disabling commands does not terminate unrelated apps. Ordinary URL actions allow HTTP, HTTPS and mailto; additional schemes require command permission. JavaScript and data URLs are always refused.

App actions accept catalog identifiers rather than arbitrary phone-supplied executable paths. Owner-selected app folders are trusted launch sources. App close is a graceful request, so applications can ask to save work. Display brightness and media commands depend on OS, app and hardware support.

App-opening sequences wait for the intended window to become active, with a bounded timeout and checks for pause or disconnect. Windows session lock, remote-session disconnection and sleep are tracked independently; ending one does not resume control while another remains active.

## Data and privacy

Per-user data is stored under `%LOCALAPPDATA%\RS Application\WinPalm`. Its directory ACL grants access to the current Windows user and SYSTEM. Files include pairing-token hashes, preferences, shared deck, device settings and local TLS key material. File replacement is atomic. Backups may contain the same sensitive material and should be protected.

The phone retains its reconnect token and settings in browser storage. Dictation audio is sent to an installed local Windows speech recognizer; there is no cloud transcription integration. Audio queues are bounded and discarded on cancellation. Clipboard paste snapshots formats and restores them after the paste only if the clipboard has not changed meanwhile; formats that can't be read are left out of the restore. The temporary paste text is marked so Windows clipboard history, cloud clipboard sync and clipboard managers skip it. Clipboard contents that a password manager marks the same way are never sent to the phone.

Device setup exports include settings, gesture actions and custom keyboards. They exclude pairing tokens, device identity and the shared Deck. These files can still contain private shortcut labels or configured actions, so protect them accordingly. Restores validate the complete file before replacing a setup and preserve the current device's identity. Deck edits and phone settings are acknowledged after successful persistence; rejected or unconfirmed edits remain available for review or retry.

Dictation messages carry the recording's request ID through the phone, core and Windows recognizer. Cancelling control clears accepted recordings, so late recognition from a cancelled session is ignored even after control resumes.

WinPalm has no analytics or accounts. Automatic GitHub release checks send the app user agent and ordinary network metadata to GitHub; they send no input, paired-device or settings data. Update downloads start only when the owner chooses Download and restart. Normal app/website actions intentionally open their configured destinations using Windows. Diagnostic logs are bounded and contain error classes/codes; they exclude input, speech, clipboard content and pairing secrets. Protect test reports as ordinary local diagnostic files, since exception details can include paths.

The local certificate authority is constrained to local/VPN address space and explicit names allowed by the core. Export only its public certificate. Verify the fingerprint before trusting it on a phone and remove that trust when no longer needed. Never share the private key.

## Updates

The native desktop host checks the latest stable release in `rahatsagor/WinPalm`. Only the owner can start a download from the Windows UI; the phone protocol exposes no update operation. Automatic checks can be disabled. Drafts, prereleases, nonnumeric tags and downgrades are excluded. Release and asset URLs are restricted to the configured repository over HTTPS; download redirects are restricted to GitHub's known asset hosts.

Updates require an architecture-specific portable ZIP and a separate SHA-256 checksum naming that exact asset. Downloads, expanded sizes and file counts are bounded. The app rejects unsafe Windows paths, alternate data streams, reserved device names, duplicate paths, links, unexpected roots, missing required files, incorrect PE architectures and a mismatched Windows executable version before installation. Downloads are staged in the private per-user data directory.

The current host launches a private copy of itself as the update helper. It validates the handoff, waits for the original process to exit, backs up managed package files and replaces them without elevation or a shell command. It retains unrelated files in the app directory and never updates the private device-data directory. Replacement failures restore the backup. If the new process exits before confirming that its server started, the helper restores and reopens the previous version. If a process remains running without confirmation, it is left running and the backup is retained rather than terminating remote input. Read-only, network and linked app folders require a manual package update.

A checksum detects a damaged or substituted download against the release's checksum. It is not a publisher signature and cannot protect against a compromised repository or GitHub account. Public packages are currently unsigned. Authenticode signing and independent signed update metadata are not implemented. See the [Windows download and installation warnings](README.md#windows-download-and-installation-warnings) for the current install steps and future certificate-funding plan. Keep the publishing account protected and never replace assets in an already published release.

## Reporting

Report a suspected vulnerability privately to the project maintainer before publishing exploit details. Do not post pairing tokens, PINs, private keys, device databases or private deck commands in an issue. Use private vulnerability reporting in the GitHub Security tab when enabled. Otherwise contact the maintainer through the contact details on their GitHub profile, without including secrets in public issues.

The release includes automated checks and cross-compilation validation. Windows recognizer, driver and hardware behavior still depends on the actual PC; compilation alone does not establish those results.
