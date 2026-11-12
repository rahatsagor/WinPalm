# Changelog

## [Unreleased]

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
