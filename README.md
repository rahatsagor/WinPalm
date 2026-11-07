<div align="center">

<img src="web/public/icon.svg" width="112" height="112" alt="WinPalm app icon" />

# WinPalm

### Your PC, in your palm.

Turn your phone into a Windows trackpad, keyboard and shortcut deck.<br />
**No phone app. No account. Your controls stay on your network.**

<a href="https://github.com/rahatsagor/WinPalm/releases/latest/download/WinPalm-Setup.exe"><img src="https://img.shields.io/badge/Download_for_Windows-f9813d?style=for-the-badge&logo=windows&logoColor=white" alt="Download WinPalm setup for Windows" height="36" /></a>
&nbsp;
<a href="https://winpalm.rsapplication.com"><img src="https://img.shields.io/badge/Website-1f1f1f?style=for-the-badge&logo=googlechrome&logoColor=white" alt="WinPalm website" height="36" /></a>

[![Latest release](https://img.shields.io/github/v/release/rahatsagor/WinPalm?style=flat-square&label=release&color=f9813d)](https://github.com/rahatsagor/WinPalm/releases/latest)
[![x64 and ARM64](https://img.shields.io/badge/Windows-x64%20%2B%20ARM64-1f1f1f?style=flat-square)](#requirements)
[![AGPL-3.0 License](https://img.shields.io/badge/license-AGPL--3.0-1f1f1f?style=flat-square)](LICENSE)

**Using a Mac? Meet [CmdPalm](https://cmdpalm.app), the original macOS app.**<br />
[CmdPalm on GitHub](https://github.com/rahatsagor/CmdPalm) · [WinPalm website](https://winpalm.rsapplication.com)

</div>

## Install and connect

1. Download **[WinPalm-Setup.exe](https://github.com/rahatsagor/WinPalm/releases/latest/download/WinPalm-Setup.exe)** and run it. It automatically chooses the native x64 or ARM64 app and includes its .NET runtime.
2. Open WinPalm from Start. When Windows Firewall asks, allow it on your **Private** home network.
3. Keep your phone and PC on that network, then scan WinPalm's QR code with the phone camera. You can also open the displayed address and enter its PIN, or request approval on the PC.
4. Add the page to your phone's home screen for an app-like experience.

The installer works for your Windows account without administrator access. It adds a Start menu shortcut; a desktop shortcut and sign-in startup are optional. Closing the settings window keeps WinPalm in the notification area. Quit or pause control from its tray menu. **Ctrl + Alt + Pause** also toggles control if that shortcut is available.

The initial release is unsigned. If SmartScreen shows **Windows protected your PC**, inspect the source/download and use **More info → Run anyway** if you trust it. Smart App Control can block unsigned apps without an individual override. Authenticode signing and Windows reputation approval are not complete.

### Prefer a portable copy?

Download the matching `WinPalm-0.1.0-win-x64.zip` or `WinPalm-0.1.0-win-arm64.zip` from [Releases](https://github.com/rahatsagor/WinPalm/releases/latest), extract the **whole ZIP** to a permanent folder and open `WinPalm.exe`. Keep `winpalm_core.dll`, `web/` and `Resources/` alongside it. Check your processor under **Settings → System → About → System type**.

If you used a private development preview, quit that copy before installing 0.1.0. Preview versions used internal version numbers that do not follow the public release sequence, so this first public installation is manual. Your private device data is kept in the same per-user folder.

## What you can do

| Control | Features |
| --- | --- |
| Trackpad | Tap, right-click, drag, drag lock, three-finger drag, two-finger scroll, momentum, a one-thumb scroll strip, pinch zoom and configurable finger gestures. |
| Keyboard | The PC's active layout, left/right modifiers, function and media keys, Unicode text, keystroke typing and clipboard paste with restoration. A sideways keyboard fills an upright phone. |
| Custom keyboards | Hold several keys at once for games, slides and editing. Arrange your own layouts and reorder their tabs. |
| Shortcut Deck | Shared pages of shortcuts, snippets, apps, websites, clicks and multi-step actions. Reorder tabs/buttons, resize the buttons, and import/export from the PC. |
| Apps and media | Discover Start Menu, Store and portable apps; launch, switch, minimize and request a graceful close. Control playback, volume, mute and supported display brightness. |
| Multiple devices | QR/PIN/PC-approval pairing, revocation, per-device settings, and selective copying of settings or individual custom keyboards. |
| Local voice typing | Stream the phone microphone to a compatible installed offline Windows recognizer over trusted HTTPS. Check setup under General. |

Modifier keys press and release like a real keyboard: tap Win by itself to open Start, or hold Ctrl while another finger presses a letter. **Sticky shortcut modifiers**, in the phone's keyboard settings, optionally enable tap-for-next-key and double-tap-to-lock. Custom keyboard modifiers always follow your finger.

PowerShell is **off by default**. Enable it under Automations only for devices you trust. Commands use your user permissions, run without a profile, and are stopped by Pause, disconnect, command-disable or a five-minute timeout. A finished command's launched apps stay open. Imported decks do not enable command execution.

## Requirements

- **Windows 11 recommended**, on x64 Intel/AMD or ARM64 hardware. The installer accepts Windows 10 version 1809 or later, but Windows 10 and every PC/driver combination have not been certified. There is no 32-bit app.
- iOS/iPadOS 17+ or a current Android browser.
- A shared private LAN, with Windows Firewall allowing WinPalm. The server does not need a public internet port.
- Trusted HTTPS and a compatible installed offline speech recognizer for WinPalm's microphone feature.

Windows protects UAC prompts, the lock screen and higher-integrity applications. WinPalm uses normal user permissions; return to a normal desktop app to resume remote input. Some games, protected media apps and remote desktop software reject synthetic input. App activation follows Windows foreground rules. Brightness and seeking controls depend on the display, driver and app.

Windows equivalents include Task View for Mission Control, Start for Launchpad, Windows Search for Spotlight, virtual desktops for Spaces, minimize/close for hide/quit, and Ctrl shortcuts for Command shortcuts. Pinch zoom uses Ctrl and fine wheel steps or Ctrl+/Ctrl−. macOS rotation, Look Up and universal keyboard-backlight control are unavailable.

## HTTPS and voice typing

Ordinary controls work over HTTP on a trusted private LAN. Phone browsers require trusted HTTPS for microphone access.

1. Open **Phone setup** on the PC and export `WinPalm.crt`.
2. Transfer that public certificate privately to the phone. Install/trust it and verify its SHA-256 fingerprint against the PC. On iPhone, also enable full trust in **Settings → General → About → Certificate Trust Settings**.
3. Select **Use an encrypted HTTPS connection** on Connect and scan that QR code.
4. Open **General → Check speech setup**. It checks the offline recognizer, dictation grammar and audio input using silence, without typing anything. The installed speech languages and any last startup problem appear in General.
5. On the phone, choose **Keyboard → Type**, tap the microphone and allow microphone access.

If Windows cannot start recognition, the phone now identifies the failing stage and Windows error code. Install or repair a compatible offline speech language in Windows Settings, then restart WinPalm. The phone's language may differ from the installed Windows language; WinPalm uses the closest installed recognizer. Recognition quality and language support depend on that recognizer.

Android browser trust for user-installed certificates varies. Resolve certificate warnings before using the microphone. Keep the private CA key on the PC and remove the phone's certificate trust when you stop using WinPalm. Voice from this microphone feature is recognized on the PC, without a transcription service.

You can also use your phone keyboard's own dictation to enter the Type field. That uses the phone's dictation provider and privacy settings.

## Updates

WinPalm checks GitHub once a day and notifies you when a newer stable release is available. Turn automatic checks off in General or Updates, or choose **Check for updates** from the tray at any time. Checks contact GitHub without sending input, settings or device lists.

Choose **Download and restart** in Updates to verify the native package, replace this copy and reopen WinPalm. Phones reconnect using their existing pairings. Settings, Deck pages, custom keyboards and certificates stay in the private data folder. Replacement failures, or a new process that exits before confirming startup, restore the previous package. A live process that does not confirm startup is left running with its backup retained for recovery.

The same updater works for installed and portable copies in a writable local folder. For a network drive, a read-only or linked folder, or a release without the matching ZIP/checksum, use **Open GitHub releases** and install manually. Drafts and prereleases do not notify installed apps.

## Troubleshooting and checks

If the phone cannot connect, check both devices' network, the Windows Private network profile and Windows Firewall. WinPalm's Connect page offers a link when it detects a Public profile or a blocking firewall rule. Use a custom address in Advanced for your VPN or reverse proxy. Forwarded headers are trusted only from loopback after the owner enables the option. Do not forward WinPalm's port to the public internet.

Run **Test-WinPalm.cmd** from the installed or portable app folder for disposable checks of the native DLL, storage, icons, server, pairing and pause/revocation. It also offers an input test in a temporary editor. Leave the PC idle during the input test. Reports are saved to `%TEMP%\WinPalm-selftest.json` and `%TEMP%\WinPalm-input-test.json`; they do not contain pairing secrets.

Behavior can vary with Windows versions, speech languages, drivers and the receiving app. Run the diagnostic checks on your own hardware; automated tests and cross-builds do not certify every PC and phone combination. Report your Windows version, architecture, keyboard layout and phone/browser when [opening an issue](https://github.com/rahatsagor/WinPalm/issues).

## Build from source

Install Git, Bun, stable Rust with the MSVC toolchain, .NET 10 SDK, and Visual Studio Build Tools with Desktop development with C++. ARM64 builds also need the matching ARM64 tools/Windows SDK. In PowerShell 7:

```powershell
./scripts/build.ps1 -Architecture x64 -Package
./scripts/build.ps1 -Architecture arm64 -Package
# After both packages exist, with NSIS 3 (makensis) installed:
bun scripts/build-installer.ts --payloads ../winpalm-dev/artifacts/builds --output ../winpalm-dev/artifacts/WinPalm-0.1.0-Setup.exe
```

The universal setup selects the PC's native architecture. Portable ZIPs remain separate for the in-app updater. All caches and build products go to `../winpalm-dev/` by default; `-DevelopmentDirectory` chooses another folder. Each package serves its corresponding source ZIP and license from phone Settings.

The GitHub workflow builds and checks native x64/ARM64 packages, compiles the setup EXE and prepares a draft release. See [CONTRIBUTING.md](CONTRIBUTING.md), [the release guide](docs/RELEASING.md), [SECURITY.md](SECURITY.md) and [the protocol](docs/PROTOCOL.md).

## Also from RS Application

**[CmdPalm](https://cmdpalm.app)** brings the same phone-to-computer idea to macOS, with native Mac input and integrations. If you use both Windows and a Mac, use WinPalm for the PC and CmdPalm for the Mac. [Explore CmdPalm on GitHub](https://github.com/rahatsagor/CmdPalm).

WinPalm is built by **RS Application**, adapting CmdPalm 0.7.0's Rust engine and Vue phone client for Windows. See [NOTICE](NOTICE) for provenance. Licensed under [AGPL-3.0-only](LICENSE).
