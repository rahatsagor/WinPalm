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

For individual Rust checks, `cargo fmt --all --check`, `cargo clippy --locked --all-targets -- -D warnings` and `cargo test --locked --all-targets` are the baseline. `.cargo/config.toml` keeps target output in the sibling development folder and links the Windows C runtime statically. The Windows host is `net10.0-windows`; build it with the .NET 10 SDK. Desktop service changes require running `Test-WinPalm.cmd` on Windows.

For phone work, copy `web/` into the private development workspace, run `bun install --frozen-lockfile`, then `bun run lint`, `bun run test` and `bun run build`. Set `WINPALM_WEB_OUT` to select its output directory. `WINPALM_HOST` sets Vite's host proxy target. The Rust `dev-host` example accepts `WINPALM_WEB_ROOT` and `WINPALM_PORT`; it logs/discards input unless `--live` is explicitly selected on Windows.

## Review and release

Check x64 and ARM64 builds, native ABI integration, package contents, resource use and actual Windows interaction. Include the tested OS, architecture, keyboard layout, monitor scaling and hardware-dependent results. Avoid claiming phone/hardware behavior from unit tests or a cross-build.

The universal installer is built with NSIS 3 from both native payloads using `bun scripts/build-installer.ts`. Test installation, upgrading and uninstalling on Windows separately. A public release also needs a trusted distribution channel, Authenticode signing/reputation decisions, source availability and dependency notices. Never embed signing material in the repository. Update the Rust, web and Windows project and manifest versions together. Run `bun scripts/release-notes.ts check VERSION` and the portable updater regressions with `dotnet run --project windows/WinPalm.UpdateTests -c Release --artifacts-path ../winpalm-dev/cache/update-tests`. See [RELEASING.md](docs/RELEASING.md) for the GitHub draft workflow and upgrade acceptance checks.
