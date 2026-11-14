# Contributing

WinPalm is a Windows tray host around a Rust server/input engine and a Vue phone client. Keep platform behavior native to Windows and preserve the shared phone interactions.

- Use Bun for every JavaScript/TypeScript install, script and test.
- Put caches, build products, screenshots and personal reports in `../winpalm-dev/`, outside this source repository.
- Keep changes to pairing, origins, permissions, held input, shell cancellation and clipboard ownership covered by meaningful regression tests.
- Do not add telemetry, background update execution, elevated services or remote code execution without an explicit design review.
- Keep public branding under RS Application and preserve CmdPalm attribution and AGPL licensing.

## Layout

| Directory | Purpose |
| --- | --- |
| `core/` | Rust server, protocol, pairing, settings, deck and Windows input |
| `web/` | Phone client and component/unit tests |
| `windows/WinPalm/` | Native .NET desktop host, services and disposable Windows checks |
| `scripts/` | Reproducible source build and package assembly |
| `packaging/` | Per-user installer and user-run test launcher |
| `docs/` | Public protocol and release documentation |

Use `scripts/build.ps1` for a complete Windows build. It creates a disposable web dependency workspace and stores .NET/Cargo output outside this checkout. CI overrides the development directory with the runner's temporary folder.

For individual Rust checks, `cargo fmt --all --check`, `cargo clippy --locked --all-targets -- -D warnings` and `cargo test --locked --all-targets` are the baseline. `.cargo/config.toml` keeps target output in the sibling development folder and links the Windows C runtime statically. The Windows host is `net10.0-windows`; install the exact SDK selected in `global.json` so the SDK's implicit dependencies match the locked package versions. Desktop service changes require running `Test-WinPalm.cmd` on Windows.

For phone work, copy `web/` into the private development workspace, run `bun install --frozen-lockfile`, then `bun run lint`, `bun run test` and `bun run build`. Set `WINPALM_WEB_OUT` to select its output directory. `WINPALM_HOST` sets Vite's host proxy target. The Rust `dev-host` example accepts `WINPALM_WEB_ROOT` and `WINPALM_PORT`; it logs/discards input unless `--live` is explicitly selected on Windows.

### Screen viewing checks

Phone tests cover display listing, approval requests, viewer leases, stale
answers, backgrounding, page deactivation, revocation, PNG chunk validation,
and Display controls. Rust tests cover authorization, stream lifetimes,
rotated source geometry, native cancellation and bounded encoder backpressure.
The portable .NET tests also inject settings-write and live-update failures
to check that screen revocation remains effective and failed opt-in rolls back.
They validate display-power notification parsing and overlapping display-off,
lock and suspend states. These generated notifications do not test OS delivery.
The Windows self-test implementation includes a synthetic H.264 encoding
check. It encodes generated pixels and verifies a keyframe with its SPS and PPS headers.
This describes the check in source, not a claim that it ran on either
architecture. It does not capture a physical monitor, check Windows screen
capture, or exercise a phone. Do not describe these checks as desktop or phone
validation.
Manual testing on a real Windows PC and phone is still needed for monitor
capture, encoder compatibility, network behavior, lock and sleep, and power
use. In particular, test unplug/replug, primary-monitor and resolution changes,
portrait rotation, repeated Start/Stop, display power-off and wake, and
frame-rate recovery on a slower connection. No desktop audio is part of screen viewing.

To exercise the browser transport with generated display content, build the
phone client and run the synthetic host with an Annex-B H.264 fixture. The
fixture needs AUD NALs to split frames and an IDR frame for every picture:

```powershell
$env:WINPALM_DISPLAY_FIXTURE = "C:\fixtures\display-test.h264"
$env:WINPALM_WEB_ROOT = "../winpalm-dev/artifacts/web"
$env:WINPALM_PORT = "0"
# Optional PNG for HD text mode, at most 16 MiB and within the 3840 × 2160 pixel budget:
$env:WINPALM_DISPLAY_IMAGE = "C:\fixtures\display-test.png"
cargo run -p winpalm-core --example display-smoke
```

Set `WINPALM_DISPLAY_FEATURE_REFRESH_MS=2000` to reproduce the capability
updates Windows sends as foreground apps change. A live viewer must continue
playing through these updates for at least 30 seconds, with its lease intact.

Open the printed pairing URL in a browser on the same PC. `WINPALM_PORT=0`
selects an available port. `WINPALM_DISPLAY_IMAGE` is optional and is used
only for HD text mode. This helper creates a temporary loopback-only host,
serves a synthetic monitor, and automatically grants screen viewing to
remembered devices paired with that helper. It does not capture the desktop or
post input. Loopback binding and automatic grants apply only to this synthetic
host, not to WinPalm. This smoke check does not replace physical PC and phone
testing.

## Review and release

Check x64 and ARM64 builds, native ABI integration, package contents, resource use and actual Windows interaction. Include the tested OS, architecture, keyboard layout, monitor scaling and hardware-dependent results. Avoid claiming phone/hardware behavior from unit tests or a cross-build.

The universal installer is built with NSIS 3.10+ from both native payloads using `bun scripts/build-installer.ts`. Test installation, upgrading and uninstalling on Windows separately. A public release also needs a trusted distribution channel, Authenticode signing/reputation decisions, source availability and dependency notices. Never embed signing material in the repository. Use `bun scripts/bump-version.ts VERSION` to update all five version records, including Cargo.lock. Add a dated changelog section, run `bun test scripts` and `bun scripts/release-notes.ts check VERSION` and the portable updater regressions with `dotnet run --project windows/WinPalm.UpdateTests -c Release --artifacts-path ../winpalm-dev/cache/update-tests`. See [RELEASING.md](docs/RELEASING.md) for the tag-triggered GitHub release workflow and upgrade acceptance checks.
