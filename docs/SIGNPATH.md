# SignPath Foundation application and setup

This prepares WinPalm for the **free open-source program**, not a paid subscription. Acceptance, account access and a real signing test are pending. The existing 0.1.0 assets remain unsigned and unchanged.

## Application details

Apply at **https://signpath.org/apply**. Use the information below wherever the application asks for project details. The public application page does not expose a stable field list, so this is a ready-to-use project brief, not a reproduction of its form.

| Item | Value |
| --- | --- |
| Project | WinPalm |
| Maintainer | RS Application; GitHub account [rahatsagor](https://github.com/rahatsagor) |
| Website | https://winpalm.rsapplication.com |
| Source repository | https://github.com/rahatsagor/WinPalm |
| License | AGPL-3.0-only; [LICENSE](../LICENSE) |
| Released software | https://github.com/rahatsagor/WinPalm/releases/tag/v0.1.0 |
| Stable installer | https://github.com/rahatsagor/WinPalm/releases/latest/download/WinPalm-Setup.exe |
| Documentation | https://winpalm.rsapplication.com/docs/ |
| Guides | https://winpalm.rsapplication.com/guides/ |
| Code signing policy | https://github.com/rahatsagor/WinPalm/blob/main/docs/CODE_SIGNING_POLICY.md |
| Privacy | https://github.com/rahatsagor/WinPalm/blob/main/SECURITY.md#data-and-privacy and https://winpalm.rsapplication.com/#privacy |
| Release workflow | https://github.com/rahatsagor/WinPalm/blob/main/.github/workflows/release.yml |
| Build workflow | https://github.com/rahatsagor/WinPalm/blob/main/.github/workflows/ci.yml |
| Build hosts | GitHub-hosted Linux, Windows x64 and Windows ARM64 runners |
| Implementation | Rust native input/server library, .NET Windows Forms tray host, Vue/TypeScript phone UI; NSIS per-user installer |
| Signing scope | Four native PE files, one generated NSIS uninstaller, then one universal NSIS setup EXE |
| Contact | Use the maintainer's own SignPath account email. No personal contact details or API tokens belong in this file. |

Suggested description:

> WinPalm is a free, open-source Windows tray app that turns an iPhone, iPad or Android browser into a trackpad, keyboard and shortcut deck for a Windows PC. Pairing and device control work over the user's network. Optional speech recognition uses installed Windows components. The app installs per user without administrator access and includes an explicit uninstall flow and owner-controlled updates from GitHub Releases. It is maintained by RS Application and licensed under AGPL-3.0-only. WinPalm adapts the same maintainer's CmdPalm project for Windows, with provenance recorded in NOTICE. We request free Foundation signing for the native x64/ARM64 app and library, generated uninstaller and universal setup, built from this repository by GitHub-hosted Actions runners.

Reason for requesting signing:

> The first public release is unsigned, and Windows download/launch warnings make installation difficult for nontechnical users. We want verifiable release provenance and publisher signatures while keeping our normal version-tagged GitHub release process. We will use the required code signing policy, multifactor authentication, restricted artifact configurations and manual production signing approvals.

The maintainer must review and attest to the [Foundation terms](https://signpath.org/terms.html), including license eligibility, source ownership, privacy disclosures and MFA. The repository records the intended roles and controls; it does not establish that account-level MFA or SignPath approvals are already configured. Approval is not guaranteed. A commercial trial is not documented as an application prerequisite.

## Account and project setup

The maintainer completes these steps after receiving the onboarding instructions:

1. Create or accept the SignPath account invitation and enable MFA on both SignPath and GitHub. Accept the program terms yourself.
2. Configure the SignPath project for `rahatsagor/WinPalm`, with GitHub as its **trusted build system** and origin verification enabled. If SignPath requests its [GitHub App](https://github.com/apps/signpath), grant access only to the WinPalm repository and review the requested permissions.
3. Have SignPath Foundation assign the approved production certificate. Keep test and production policies separate. Add `rahatsagor` as signing approver and require manual approval. Restrict the production policy to `.github/workflows/release.yml`, this repository and stable `vN.N.N` tags. The workflow also verifies that the tagged commit belongs to `main`'s history. Do not enable automatic production approval.
4. Create artifact configuration **`binaries`** from [binaries.xml](../packaging/signpath/binaries.xml) and **`setup`** from [setup.xml](../packaging/signpath/setup.xml). Both must be available to the selected test and production policies. Each request supplies the required `version` parameter. The root is a ZIP because `actions/upload-artifact@v4` wraps its files in a ZIP. Ask the onboarding contact to review both configurations and the two-request NSIS flow.
5. Create a SignPath API token for a CI submitter permitted to request signing for this project/policies. The token must not grant signing-approval authority. Store it directly in GitHub, never in chat, source or an application message.

## GitHub settings

In [WinPalm → Settings → Secrets and variables → Actions](https://github.com/rahatsagor/WinPalm/settings/secrets/actions), add:

| Type | Name | Value |
| --- | --- | --- |
| Secret | `SIGNPATH_API_TOKEN` | The restricted CI submitter token from SignPath |
| Variable | `SIGNPATH_ORGANIZATION_ID` | SignPath organization ID |
| Variable | `SIGNPATH_PROJECT_SLUG` | Actual SignPath project slug |
| Optional variable | `SIGNPATH_RELEASE_POLICY_SLUG` | Production policy slug; defaults to `release-signing` |
| Optional variable | `SIGNPATH_TEST_POLICY_SLUG` | Test policy slug; defaults to `test-signing` |

The two artifact configuration slugs are fixed as `binaries` and `setup`. There are no signing keys to export or upload. The workflow's GitHub token needs only read access to Actions and contents for signing; only the final publishing job has contents-write permission.

## First signing rehearsal

After the account, configurations and token are ready:

1. Open [Actions → Release](https://github.com/rahatsagor/WinPalm/actions/workflows/release.yml) and choose **Run workflow** on `main`.
2. Keep **tag = `main`** and **test_signing = true**. This builds the current source using the test policy and cannot publish or replace a GitHub release.
3. Review the first request in SignPath: two native app EXEs, two native DLLs and one exported uninstaller. Approve if its repository, source commit, files and metadata are correct.
4. Review and approve the second request for the final setup EXE, which embeds the returned app binaries and uninstaller. Each request waits up to 30 minutes; start the run when you can approve both.
5. Confirm both native installer checks pass. Download test artifacts only for an isolated Windows test machine. A test certificate is not trusted for public distribution and can still produce Windows warnings. On disposable GitHub-hosted Windows runners only, the verifier temporarily trusts the returned self-signed test certificate, requires `Valid` Authenticode status, and removes the added trust in `finally`. It checks installed/portable byte identity too. It does not change trust on the maintainer's PC or establish production certificate trust.

The next step is a new version, such as `0.1.1`, after Foundation production approval. Use [RELEASING.md](RELEASING.md): bump, changelog, commit, push and tag. Tag-triggered runs always use the production signing policy. For a production retry through Run workflow, select the existing version tag and explicitly clear **test_signing**. Production verification requires valid, timestamped signatures with the SignPath Foundation publisher; a test certificate is refused.

Two signing requests are needed because NSIS embeds app binaries and generates its uninstaller. We export/sign the uninstaller with the native apps, rebuild setup with those exact files, then sign setup itself. Portable updater ZIPs contain those same signed native files, and all checksums are regenerated afterward. See [NSIS's documented external signing pattern](https://nsis.sourceforge.io/Signing_an_Uninstaller_externally).

## Required public links and status

The README and generated release notes link the [Code signing policy](CODE_SIGNING_POLICY.md). Keep that link on the website homepage/download surface too. Keep the status as pending and the 0.1.0 unsigned notice until an approved production release is actually available. The prescribed SignPath attribution appears in the policy as the planned attribution, and in signed release notes only after signature verification has passed.

After the first signed release, update the policy's status and download documentation to name that version, then test a real browser download on Windows. Authenticode signing does not guarantee immediate SmartScreen reputation. The current updater validates GitHub-hosted ZIPs and checksums; it does not independently enforce the new signing identity or signed update metadata on the PC.

Official references: [Foundation program](https://signpath.org/), [terms](https://signpath.org/terms.html), [GitHub integration](https://docs.signpath.io/trusted-build-systems/github), [artifact configuration reference](https://docs.signpath.io/artifact-configuration/reference).
