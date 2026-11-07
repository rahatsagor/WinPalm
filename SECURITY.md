# Security

WinPalm lets paired devices control a Windows desktop. Pair only devices you trust, use a private network, and use trusted HTTPS where traffic could be observed. Plain HTTP does not protect a reconnect token from someone who can observe that connection.

## Pairing and access

QR pairing uses a single-use random code in the URL fragment. PIN and explicit-PC-approval pairing are optional, rate limited, and require the PC owner to display or approve the request. Reconnect tokens are random; only their hashes are persisted on the PC. Remove a device to revoke its token, close its sessions and remove its backed-up phone settings.

The server validates HTTP hosts, same-origin WebSocket upgrades, request sizes, static paths and deck contents. Windows drive paths and alternate data stream syntax are rejected. A session key protects icon requests. Custom hostnames and loopback reverse-proxy forwarding are owner opt-ins. Do not expose the server to the public internet or enable proxy trust for an untrusted local proxy.

## Windows boundary

The app uses SendInput at the current user's integrity level. It does not install a service, driver or input hook, request UIAccess, or bypass UAC. Secure desktops and higher-integrity foreground processes are refused. Closing or losing a session releases its input; an idle-device watchdog also releases keys and buttons. Pause blocks control, cancels dictation and interrupts queued text and running shell jobs. Completed native operations and apps already launched are not undone.

PowerShell and named automations are disabled by default. Both require the owner to enable command access on the PC. They inherit that owner's user permissions and can change or delete files. Jobs start suspended, enter a kill-on-close Job Object before execution, and stop on pause, disconnect, revocation, command-disable or a five-minute timeout. Disabling commands does not terminate unrelated apps. Ordinary URL actions allow HTTP, HTTPS and mailto; additional schemes require command permission. JavaScript and data URLs are always refused.

App actions accept catalog identifiers rather than arbitrary phone-supplied executable paths. Owner-selected app folders are trusted launch sources. App close is a graceful request, so applications can ask to save work. Display brightness and media commands depend on OS, app and hardware support.

## Data and privacy

Per-user data is stored under `%LOCALAPPDATA%\RS Application\WinPalm`. Its directory ACL grants access to the current Windows user and SYSTEM. Files include pairing-token hashes, preferences, shared deck, device settings and local TLS key material. File replacement is atomic. Backups may contain the same sensitive material and should be protected.

The phone retains its reconnect token and settings in browser storage. Dictation audio is sent to an installed local Windows speech recognizer; there is no cloud transcription integration. Audio queues are bounded and discarded on cancellation. Clipboard paste snapshots formats and restores them after the paste only if the clipboard has not changed meanwhile; formats that can't be read are left out of the restore. The temporary paste text is marked so Windows clipboard history, cloud clipboard sync and clipboard managers skip it. Clipboard contents that a password manager marks the same way are never sent to the phone.

WinPalm has no analytics, accounts or automatic update download. Normal app/website actions intentionally open their configured destinations using Windows. Diagnostic logs are bounded and contain error classes/codes; they exclude input, speech, clipboard content and pairing secrets. Protect test reports as ordinary local diagnostic files, since exception details can include paths.

The local certificate authority is constrained to local/VPN address space and explicit names allowed by the core. Export only its public certificate. Verify the fingerprint before trusting it on a phone and remove that trust when no longer needed. Never share the private key.

## Reporting

Report a suspected vulnerability privately to the project maintainer before publishing exploit details. Do not post pairing tokens, PINs, private keys, device databases or private deck commands in an issue. No security contact or public WinPalm repository has been provisioned yet; use the trusted channel through which you received this preview.

The first preview has local automated and cross-compilation checks. Windows runtime, provider, driver and hardware behavior needs the documented PC test; compilation alone is not a security or release certification.
