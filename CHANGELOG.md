# Changelog

## [Unreleased]

## [1.0.0] - 2026-11-20

See your Windows PC's screen on your phone while you use the trackpad or
keyboard. This release also reorganizes phone settings, lowers idle background
activity, and hardens HTTPS, networking, and update recovery.

### Screen mirroring

- Open **Display** on your phone to view one Windows monitor. Use the preview
  on its own, above or beside the trackpad or keyboard, or fullscreen. Pinch to
  zoom with an adaptive zoom ceiling, drag to pan, and move the PC pointer onto
  the selected monitor.
- Choose adaptive Media Foundation H.264 video (preferring a compatible
  hardware encoder with automatic software fallback) or **HD text · lossless**
  for small text and code. HD text preserves source pixels within a 4K pixel
  budget and sends dirty-rect updates up to four times per second. Live video
  negotiates H.264 High/Main/Baseline profiles, uses BT.709 color, refines fine
  detail when the desktop settles, and requests a shorter playback delay on
  supported browsers.
- Screen mirroring is off by default. Enable **Allow screen mirroring** in
  **General**, then allow individual devices in **Devices** or approve a
  phone's access request on the PC.
- Display works over HTTP and HTTPS with encrypted WebRTC media and needs a
  direct UDP connection. The PC and display stay awake while you view them.
  Pausing, locking, sleeping, turning off displays, revoking access, or leaving
  Display stops capture immediately. No desktop audio is captured and no screen
  recordings are saved.
- Refresh capture after monitor rotation, resolution or arrangement changes,
  preserve the video clock during recovery, and stop if the selected monitor
  disappears. Keep the live view active through monitor-list refreshes and
  unrelated Windows capability changes.

### Windows app

- Reduce idle CPU wakeups across the Rust core and Windows host: block the
  input engine while no devices are connected, wake housekeeping on network or
  config changes, skip background broadcasts when idle, and back off hidden
  host polling.
- Preserve unreadable, corrupt and newer paired-device files and phone tokens.
  Refuse remembered pairing until recovery, and expose an intentional reset in
  **Devices**.
- Validate saved HTTPS key/certificate pairs and the full server chain,
  including recovery from interrupted authority replacement and live SAN
  refresh for custom addresses. Disable certificate export while stopped and
  warn if certificate bytes are not ready yet.
- Harden WebRTC UDP socket readers against transient resets and preserve
  Windows LAN adapter priority when ranking media endpoints. Add defense-in-depth
  URL scheme checks on Windows matching the core's shell-permission rules
  (including `file:`, `ssh:`, `sftp:`, `webdav:`, and `vnc:`).
- Keep local control usable when Remote Desktop Services is stopped by checking
  that the Default desktop is receiving input; locked, disconnected and
  unverifiable states remain blocked.
- Improve updater reliability: handle corrupt ZIP archives cleanly, roll back
  failed updates without blocking modals, fix `INetwork` COM vtable order in
  network checks, and guard audio volume COM errors.
- Improve approval popups with Escape-to-decline, screen-clamped stacked
  positioning, and clearer paired vs temporary access details.
- Restore an initially empty clipboard when quitting during a temporary paste,
  reject empty or duplicate Deck IDs in native imports and phone saves, and
  bundle current app, Rust, phone, NuGet/.NET and installer license notices
  accessible from **About**.
- Add a synthetic H.264 encoder check to the native x64 and ARM64 self-test.

### Phone client

- Organize Settings into **Trackpad**, **Gestures**, **Keyboard**, **Display**
  and **Deck** tabs. The connection card shows an **Encrypted** or **Not
  encrypted** badge, and **Keyboard → Dictation setup** adapts its guidance to
  the current connection.
- Configure three- and four-finger gestures separately. **Copy settings from
  another device** includes **Show touches** with Trackpad settings and
  supports Display preferences. Device backups include Display preferences and
  migrate legacy settings cleanly.
- Enlarge touch targets and improve keyboard navigation in settings and
  dialogs. **Tab** and **Shift+Tab** on a physical keyboard move focus through
  the phone UI while keeping Windows tab-reorder shortcuts local; use the
  on-screen **Keyboard → PC** layout to send Tab to the PC.
- Match trackpad hints to enabled gestures, use matching up/down chevrons at
  every scroll-strip width (including wide Display mode), and offer **Clear
  search** on an empty app search.
- Release click-bar, scroll-strip hold, repeating media keys, held touch
  controls and trackpad drag lock synchronously on pause, lost pointer capture,
  or disconnect. Count overlapping mouse and media holds across devices.
- Prevent shortened custom-key IDs from colliding, reject inherited object
  names as media controls, isolate cached service-worker builds, and fix
  formatter-tooling dependency advisories with scoped overrides.

## [0.3.2] - 2026-11-12

- Fix scrolling inside the Windows app after switching pages. Long pages show their scrollbar immediately, and shorter pages clear the previous scroll position.
- Keep page widths aligned with the scrollbar after navigation, window resizing and reopening from the tray.
- Add native Windows regression checks for page changes, mouse-wheel scrolling and reaching the bottom of a page.

## [0.3.1] - 2026-11-12

A redesigned Windows app with clearer pairing controls, grouped settings, and a consistent dark interface.

### Windows app

- Refresh every desktop page with a Windows 11-style dark theme, grouped settings cards, rounded surfaces, clearer typography, and scalable icons.
- Make pairing easier to follow with a dedicated QR card, HTTP/HTTPS switcher, six-digit PIN countdown, and copyable phone browser address.
- Add matching switches, segmented pickers, a port stepper, dark dialogs, a styled tray menu, and pairing approval prompts that appear without taking keyboard focus.
- Guide HTTPS setup with certificate QR codes, phone-specific instructions, certificate export, and fingerprint copying.

### Reliability

- Lay out pages correctly on first launch and after reopening from the tray. Keep status badges, long device names, and dialog validation messages within their available space.
- Initialize the port stepper safely before resizing and keep dynamic status rows sized to their current text and buttons.
- Complete phone-initiated WebSocket disconnect handshakes while releasing held input and cancelling session actions.
- Keep startup and replacing a running app reliable when Windows setup launches WinPalm's quit helper.
- Expand native UI and input self-tests to verify layout, key release, pause/resume, and disconnect behavior without counting queued key repeats as paused input.

## [0.3.0] - 2026-11-12

Unpublished candidate. Superseded by 0.3.1 after installer validation found a startup-order issue.

## [0.2.0] - 2026-11-09

Undo Deck edits, back up your phone's setup, and keep unsaved changes ready to retry. Sequences wait for opened apps, dictation replies stay with their recording, and waking a locked PC keeps control paused.

### Deck

- Undo and redo confirmed edits, including deleted buttons and pages. Newer changes from another device clear the local history.
- Keep editor drafts after save failures, disconnects and conflicts. Saves wait for the PC to confirm persistence and reject stale revisions.
- Keep a held button tied to its identity when another device reorders the page, and cancel unfinished drags after remote changes.

### Device backups and settings

- Export and restore phone settings and custom keyboards from **Settings → Back up or restore this device**. Review the backup before restoring it. Pairing tokens are excluded, and the current device's name and pairing are preserved.
- Reject damaged, unsupported and oversized backups before replacing any setup. Shared Deck backups remain in the PC's Deck settings.
- Confirm settings were saved on the PC, retry temporary failures, and offer **Retry saving to the PC** in the phone's Settings while retaining the setup on the phone.

### Windows and voice

- Wait for an opened app's window to become active before continuing a sequence. Stop on activation failure, timeout, pause or disconnect.
- Keep control paused until overlapping Windows lock, remote disconnect and sleep pauses have all ended. Preserve an existing manual pause.
- Keep dictation replies tied to their recording, discard cancelled results after pause or disable, and prevent starting the microphone while control is paused.
- Release held input promptly even while a Windows recognizer is starting. Limit active and finishing recognizers together, clean them up on cancellation, and preserve specific Windows startup error messages.

### Maintenance

- Restore the regular unsigned release workflow and explain browser/Windows installation warnings, install steps and the future signing-certificate funding plan.

## [0.1.0] - 2026-11-08

The first public WinPalm release, built by RS Application and adapted from CmdPalm for Windows.

- A universal setup EXE installs the native x64 or ARM64 app for your Windows account, including its runtime. Portable ZIPs are also available.
- Phone trackpad with tap, right-click, dragging, two-finger scrolling, momentum, pinch zoom and configurable gestures.
- A Windows keyboard that follows the PC's layout, with normal modifier presses, optional sticky shortcuts, media/function keys and a sideways layout for upright phones.
- Custom multi-touch keyboards for presentations, games and editing. Reorder keyboard groups by dragging their tabs.
- A shared shortcut Deck with reorderable pages, apps, websites, text, clicks and multi-step actions. PowerShell automations are optional and disabled by default.
- App launching, switching, minimizing and graceful closing, with native icons and distinct built-in fallbacks. Media, volume and supported display brightness controls.
- QR, PIN or explicit PC approval pairing, device revocation, per-device settings and selective copying between paired devices.
- Optional local voice typing using an installed compatible Windows recognizer over trusted HTTPS. General includes a speech setup check, and recognition startup failures identify the failing stage and Windows error code.
- GitHub release checks, notifications, release notes, verified architecture-specific downloads and an owner-triggered update/restart with file recovery.
- AGPL source is included and available from the phone's settings. No account, analytics or phone app is required.

Native voice recognition, some display/media controls and synthetic input depend on Windows components, drivers and app support. See README.md for requirements and troubleshooting.
