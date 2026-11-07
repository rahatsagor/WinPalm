# Releasing WinPalm

The first public version is **0.1.0**. Private development-preview version numbers are not public releases. Public source uses a fresh, focused commit history, with the development history archived outside the repository.

The website is **https://winpalm.rsapplication.com**. Prepare and deploy the website and launch video before pushing the source and release tag to `rahatsagor/WinPalm`. No GitHub push or publication is part of local release preparation.

## Build and check

1. Keep `Cargo.toml`, the WinPalm entry in `Cargo.lock`, `web/package.json`, `windows/WinPalm/WinPalm.csproj` and `windows/WinPalm/app.manifest` in sync. The manifest appends `.0` to the three-part version.
2. Add the release section to `CHANGELOG.md` and run `bun scripts/release-notes.ts check 0.1.0`.
3. Build both native portable packages with `scripts/build.ps1 -Architecture x64 -Package` and `-Architecture arm64 -Package`. The updater harness checks their complete structure, PE architecture and native version on Windows.
4. Install NSIS 3, then run `bun scripts/build-installer.ts --payloads ../winpalm-dev/artifacts/builds --output ../winpalm-dev/artifacts/WinPalm-0.1.0-Setup.exe`. The same compiler can run on macOS, Linux or Windows. `MAKENSIS` overrides its path.
5. Test the installed app on Windows, including General → Check speech setup, live phone dictation over trusted HTTPS, ordinary controls, sign-in startup and uninstall. Keep OS, architecture and language-specific results in the companion acceptance record.

The setup EXE contains both native payloads and detects the PC's native x64/ARM64 architecture. It installs per user with no administrator requirement, adds a Start menu shortcut and offers desktop/sign-in shortcuts. It gracefully asks a running WinPalm to quit. A new installation requires a new/empty directory; replacement requires a recognizable WinPalm package. Uninstall removes managed program files while retaining private pairings, settings and certificates.

Local setup compilation and archive tests do not establish actual Windows installer execution. Do not claim SmartScreen reputation, Authenticode signing or ARM64 installer runtime results unless those have been verified.

## Expected downloads

- **`WinPalm-Setup.exe`**: stable download name for README and website buttons.
- `WinPalm-0.1.0-Setup.exe`: the same installer with a versioned name.
- `WinPalm-0.1.0-win-x64.zip` and `.zip.sha256`.
- `WinPalm-0.1.0-win-arm64.zip` and `.zip.sha256`.
- `WinPalm-0.1.0-source.zip`.
- Setup checksum files and `SHA256SUMS.txt`.

The setup alias and versioned setup must be byte-identical. Each package also includes exact corresponding source at `web/source.zip`, its license and third-party notices. Installer runtime notices are installed as `NSIS-LICENSE.txt`.

The in-app updater deliberately uses the native portable ZIP, including for installed copies. Keep both native ZIP/checksum pairs on every public release. A checksum line must name its exact ZIP after the 64-character SHA-256 hash. Preview users install 0.1.0 manually once because their internal preview version may compare higher than public 0.1.0.

## Website and reciprocal links

The WinPalm landing-page download button should use `https://github.com/rahatsagor/WinPalm/releases/latest/download/WinPalm-Setup.exe`. Link to the GitHub release page as an alternative and link the GitHub repository for source.

Promote **CmdPalm** on the WinPalm README and website: “Using a Mac? Meet CmdPalm.” Link https://cmdpalm.app and https://github.com/rahatsagor/CmdPalm. Promote **WinPalm** from CmdPalm's README and website: “Using Windows? Meet WinPalm.” Link https://winpalm.rsapplication.com and https://github.com/rahatsagor/WinPalm. Keep these recommendations outside the apps' control UI.

## Push and publish after the website is deployed

1. Review the clean local history and final acceptance record. Scan the public tree and history for private artifacts or credentials. Keep the private history bundle, builds and personal notes in `winpalm-dev`, outside public source.
2. Create the public `rahatsagor/WinPalm` repository without an unrelated generated README. Set the description, website and topics. Enable private vulnerability reporting in Security.
3. Push `main` and then the matching `v0.1.0` tag. Do not push the archived development history.
4. The release workflow builds/tests native x64 and ARM64 packages, combines them into the universal setup and creates a **draft** release. This first GitHub execution remains to be observed after the push.
5. Download the draft setup, install it on Windows and check the asset names/checksums. Test the website buttons and both products' reciprocal links. Add the prepared website/video links to the release notes when deployed.
6. Publish the reviewed draft as the latest stable release. Do not replace assets on an already published release; fix problems with a new version.

The public launch is not complete until those remote and Windows checks have been observed. A local build does not create a repository or publish a release.

## Future upgrade acceptance

Before publishing the first update after 0.1.0, test an actual 0.1.0-to-newer installation on Windows. Check tray notification, release notes, download cancellation, network interruption, restart, original install path, pinned/desktop shortcuts, sign-in command, pairings, settings, Deck, custom keyboards and certificate trust. Repeat on x64/ARM64 where available.

The updater restores previous files after replacement failure or an updated process exiting before startup confirmation. If a new process stays running without confirmation, it is left running and its backup is retained. Confirm recovery from a blocked destination file on a disposable test profile, and check Settings → Installed apps plus uninstall after an in-place update.

Downloads use HTTPS and SHA-256 checksums from the same release. Preview/public initial packages have no Authenticode signature or separate signed update manifest. See [SECURITY.md](../SECURITY.md#updates).
