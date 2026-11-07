# Code signing policy

WinPalm is maintained by **RS Application** at [rahatsagor/WinPalm](https://github.com/rahatsagor/WinPalm).

## Current status

WinPalm's application for SignPath Foundation's free open-source signing program was submitted on **8 November 2026** and is awaiting review. Approval and a successful production signing run are still required. **The published 0.1.0 release is unsigned.** Existing release files and tags will not be replaced; signing will start with a new release after onboarding.

The attribution for releases signed through this program is:

> Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

For those releases, Windows displays **SignPath Foundation** as the certificate's publisher. RS Application remains the app's developer. Signing verifies publisher identity and file integrity; SmartScreen reputation may still take time to develop.

## Team and approvals

| Role | Responsible maintainer |
| --- | --- |
| Author / committer | [rahatsagor](https://github.com/rahatsagor) |
| Reviewer | [rahatsagor](https://github.com/rahatsagor) |
| Signing approver | [rahatsagor](https://github.com/rahatsagor) |

Changes from outside contributors must be reviewed by the maintainer. All team members must use multifactor authentication on GitHub and SignPath. Each production signing request requires manual approval by the signing approver after reviewing its source commit, workflow and artifact configuration.

## Scope of signing

Only WinPalm artifacts built from this repository's source by the public [Release workflow](https://github.com/rahatsagor/WinPalm/actions/workflows/release.yml) on GitHub-hosted runners may be signed:

- Native x64 and ARM64 `WinPalm.exe` and `winpalm_core.dll`.
- The generated `Uninstall.exe`.
- The universal `WinPalm-VERSION-Setup.exe`, also distributed byte-for-byte as `WinPalm-Setup.exe`.

The portable/update ZIPs contain the same signed app binaries as setup. Third-party components retain their upstream signatures, if any; our signing configuration does not re-sign third-party binaries. The app includes its dependency notices and corresponding AGPL source.

The [binaries](../packaging/signpath/binaries.xml) and [setup](../packaging/signpath/setup.xml) configurations allow only explicit paths and enforce WinPalm's product name, company and release version. The SignPath project must verify GitHub source/build provenance for this repository and restrict production signing to version tags on `main`'s history. Test signing is allowed for onboarding from `main` and never publishes a release.

Before publication, native Windows checks require valid, timestamped SignPath Foundation signatures and verify that installed and portable app files retain the exact signed bytes. A missing configuration, rejected signing request or verification failure stops publication. There is no unsigned fallback. Checksums are generated after signing.

## Privacy

See [Security: data and privacy](../SECURITY.md#data-and-privacy) and the [website's privacy overview](https://winpalm.rsapplication.com/#privacy).

WinPalm has no analytics or user accounts. Phone controls, clipboard and speech processing remain between the user's devices and local Windows components. Pairings, settings and local certificate material stay on the PC. The app checks GitHub for stable releases automatically unless the owner disables update checks; GitHub receives ordinary connection metadata and the app's user agent, not phone input, clipboard, speech or settings. Update downloads require the owner's action. Owner-configured website/app actions contact their selected destinations. [GitHub's privacy statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement) applies to its release service.

For signing, the maintainer submits publicly built release artifacts and their GitHub origin metadata to SignPath. No app-user data or private signing keys are included in the repository or released packages. See [SignPath's privacy policy](https://signpath.io/privacy-policy).
