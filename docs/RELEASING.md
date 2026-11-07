# Releasing WinPalm

WinPalm uses the same release routine as CmdPalm: bump versions, write the changelog, commit and push, then push a version tag. GitHub Actions checks the exact tagged source, builds both native Windows packages, assembles the universal setup and publishes a stable release.

The public version sequence starts at **0.1.0**. Private development-preview numbers are outside that sequence. Publishing credentials and signing material belong in GitHub settings, never in the source repository.

## Prepare a release

From a clean `main` checkout, after implementing the changes:

```powershell
bun scripts/bump-version.ts 0.1.1
# Move the relevant Unreleased notes into a dated ## [0.1.1] section.
bun scripts/release-notes.ts check 0.1.1
bun test scripts
```

The bump command updates `Cargo.toml`, the `winpalm-core` entry in `Cargo.lock`, `web/package.json`, `windows/WinPalm/WinPalm.csproj` and `windows/WinPalm/app.manifest`. The manifest uses a fourth `.0` component. Stable three-part versions are required; Windows version components must be below 65535. The release check rejects missing notes or mismatched versions, including a stale lockfile.

Review and commit the source changes, all five version files and `CHANGELOG.md`, then push `main`. Wait for its CI checks to pass before tagging:

```powershell
git push origin main
git tag -a v0.1.1 -m "WinPalm 0.1.1"
git push origin v0.1.1
```

Use `0.1.0` instead for the initial release. Do not move a published tag or replace a published release's assets. Fix a public release with a new version.

## What the workflow does

1. Validates the `vN.N.N` tag, changelog, all recorded versions and the updater's repository. Resolves the tag to an exact source commit.
2. Calls the same CI workflow used for `main` and pull requests. Native x64 and ARM64 Windows runners check phone lint/tests/build, release-script regressions, Rust format/Clippy/tests, host regressions, self-contained .NET publishing, the packaged Windows self-test and archive/binary validation.
3. Reuses those checked packages, verifies their checksums and compiles a universal setup EXE with NSIS 3.10+. Confirms the stable and versioned setup files are byte-identical.
4. Runs the actual setup on fresh x64 and ARM64 Windows runners. Checks the default installation, Start menu shortcut, installed app integration checks, native architecture, graceful replacement of a running tray app, uninstall and preservation of private app data.
5. Creates checksums, generates release notes from `CHANGELOG.md`, uploads the complete download set to a draft and then publishes it as the latest stable release. Website, docs, guides, film and CmdPalm links are included in the notes.

Only the publishing job has repository write permission. Build products, dependency caches and diagnostic reports stay in the runner's temporary directory or short-lived Actions artifacts. They are never committed.

To retry a failed release, rerun its workflow or run **Release → Run workflow** with the existing tag. A failed draft can be completed. The workflow refuses to overwrite an already published release.

GitHub native self-tests establish those automated checks on the runner. The setup checks cover silent installation on hosted Windows runners. Actual phone gestures, microphone recognition quality, interactive setup and physical-device behavior still need hardware checks.

## Downloads on every release

For version `0.1.0`, the release contains:

| File | Purpose |
| --- | --- |
| `WinPalm-Setup.exe` | Stable download for the website and README |
| `WinPalm-0.1.0-Setup.exe` | Identical setup with a versioned filename |
| `WinPalm-0.1.0-win-x64.zip` | Native Intel/AMD portable package and in-app update |
| `WinPalm-0.1.0-win-arm64.zip` | Native Windows on Arm package and in-app update |
| `WinPalm-0.1.0-source.zip` | Corresponding source for this release |
| `*.exe.sha256`, `*.zip.sha256` | SHA-256 checksums naming their exact download |
| `SHA256SUMS.txt` | Combined checksums for every EXE and ZIP |

Keep both native ZIP/checksum pairs on every stable release. The in-app updater uses the native ZIP even for copies installed by setup. Each package also serves its exact corresponding source at `web/source.zip`, with the license and dependency notices.

The setup selects the PC's native architecture, includes its .NET runtime and installs per user without elevation. It creates a Start menu shortcut and offers desktop and sign-in shortcuts. Re-running setup replaces a recognizable installation. A new installation requires a new or empty folder. Uninstall removes managed program files and retains private pairings, settings and certificates.

Initial releases are unsigned. Checksums detect corruption against the release metadata; they are not publisher signatures. Authenticode signing and independent signed update metadata remain future work. See [SECURITY.md](../SECURITY.md#updates).

## Build locally on Windows

Install Git, Bun, stable Rust with the native MSVC toolchain, the exact .NET SDK selected in `global.json`, Visual Studio C++ Build Tools, PowerShell 7 and NSIS 3.10+. Include the ARM64 C++ tools and Windows SDK for ARM64 cross-builds. Update the SDK pin and regenerate `windows/WinPalm/packages.lock.json` together when intentionally upgrading .NET; a floating SDK can change implicit package references and break locked restore.

```powershell
./scripts/build.ps1 -Architecture x64 -Package
./scripts/build.ps1 -Architecture arm64 -Package
bun scripts/build-installer.ts --payloads ../winpalm-dev/artifacts/builds --output ../winpalm-dev/artifacts/WinPalm-0.1.0-Setup.exe
```

`-DevelopmentDirectory` chooses a different output folder outside the source tree. `MAKENSIS` overrides the NSIS compiler path. The installer compiler can run on Windows, Linux or macOS; installation behavior must be checked on Windows.

## Installation and update acceptance

Before tagging, test the candidate setup on Windows. Check first install, architecture selection, app startup, firewall setup, shortcuts, sign-in startup and uninstall. Exercise normal phone controls, pairing/revocation and **General → Check speech setup**, then live dictation over trusted HTTPS where a compatible speech language is installed. Record the Windows build, architecture, speech language and hardware results outside public source.

For every update after 0.1.0, test an actual old-to-new upgrade on a disposable Windows installation. Check notification, release notes, download cancellation, network interruption, restart, install path, shortcuts, sign-in, pairings, settings, Deck, custom keyboards and certificate trust. Repeat on both architectures where available. Confirm Installed apps shows the new version and uninstall still works.

The updater restores previous files after replacement failure or the new process exiting before startup confirmation. If a new process stays running without confirmation, it keeps running and the backup remains available. Check recovery from a blocked destination file on a disposable profile.

After publication, confirm the [latest release](https://github.com/rahatsagor/WinPalm/releases/latest), [stable setup link](https://github.com/rahatsagor/WinPalm/releases/latest/download/WinPalm-Setup.exe), architecture downloads, checksums and website buttons. The deployed [website](https://winpalm.rsapplication.com), [docs](https://winpalm.rsapplication.com/docs/), [guides](https://winpalm.rsapplication.com/guides/) and [film](https://winpalm.rsapplication.com/#film) should all be reachable.
