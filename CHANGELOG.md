# Changelog

## [Unreleased]

- Prepare SignPath Foundation release signing for both native architectures, the uninstaller and universal setup, with public policy and signature verification. Activation requires program approval and account setup.

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
