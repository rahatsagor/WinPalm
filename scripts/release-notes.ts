import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { checkVersions, normalizeVersion } from './version'

const root = resolve(import.meta.dir, '..')
const [command, raw] = Bun.argv.slice(2)
if (Bun.argv.length !== 4 || !['check', 'notes'].includes(command ?? '')) {
  throw new Error('Usage: bun scripts/release-notes.ts check|notes VERSION')
}
const version = checkVersions(root, normalizeVersion(raw))
const read = (path: string) => readFileSync(resolve(root, path), 'utf8')
const lines = read('CHANGELOG.md').split('\n')
const headings = lines.filter(line => new RegExp(`^## \\[${version.replaceAll('.', '\\.')}\\](?: - \\d{4}-\\d{2}-\\d{2})?$`).test(line))
if (headings.length !== 1) throw new Error(`Expected one ## [${version}] release section in CHANGELOG.md.`)
const start = lines.indexOf(headings[0]!)
if (start < 0) throw new Error(`Add release notes for ${version} to CHANGELOG.md.`)
const rest = lines.slice(start + 1)
const end = rest.findIndex((line) => line.startsWith('## ['))
const notes = (end < 0 ? rest : rest.slice(0, end)).join('\n').trim()
if (!notes) throw new Error('Release notes must not be empty.')
const repository = read('windows/WinPalm/Services/ReleaseUpdates.cs').match(/const string Repository = "([^"]+)"/)?.[1]
if (repository !== 'rahatsagor/WinPalm' || (process.env.GITHUB_REPOSITORY && process.env.GITHUB_REPOSITORY !== repository)) throw new Error(`The updater targets ${repository}. Release only from its configured repository.`)
if (command === 'notes') {
  console.log(notes)
  const signing = process.env.WINPALM_SIGNED_RELEASE === 'true'
    ? 'Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/). Windows identifies the signing publisher as SignPath Foundation. SmartScreen may still warn while reputation develops.'
    : 'This release is unsigned. If SmartScreen appears, see the [installation guide](https://winpalm.rsapplication.com/docs/).'
  console.log(`\n## Install\n\nDownload **[WinPalm-Setup.exe](https://github.com/${repository}/releases/download/v${version}/WinPalm-Setup.exe)** and run it. Setup selects native x64 or ARM64, includes the .NET runtime, and installs for your Windows account without administrator access. Open WinPalm, allow Windows Firewall on your private network, and scan its QR code with your phone.\n\nPortable ZIPs for each architecture, corresponding source, and SHA-256 checksums are attached below. Installed and portable copies can use **Download and restart** for future updates.\n\n## Code signing policy\n\n${signing}\n\n[Code signing policy](https://github.com/${repository}/blob/main/docs/CODE_SIGNING_POLICY.md)\n\n[Website](https://winpalm.rsapplication.com) · [Documentation](https://winpalm.rsapplication.com/docs/) · [Guides](https://winpalm.rsapplication.com/guides/) · [Launch film](https://winpalm.rsapplication.com/#film)\n\nUsing a Mac? Meet **[CmdPalm](https://cmdpalm.app)**, also from RS Application.`)
} else {
  console.log(`WinPalm ${version}: release versions, notes and update repository agree.`)
}
