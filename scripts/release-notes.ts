import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

const root = resolve(import.meta.dir, '..')
const [command, raw] = Bun.argv.slice(2)
const version = raw?.replace(/^v/, '')
if (!version || !/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(version) || !['check', 'notes'].includes(command ?? '')) {
  throw new Error('Usage: bun scripts/release-notes.ts check|notes VERSION')
}
const read = (path: string) => readFileSync(resolve(root, path), 'utf8')
const lines = read('CHANGELOG.md').split('\n')
const start = lines.findIndex((line) => line.startsWith(`## [${version}]`))
if (start < 0) throw new Error(`Add release notes for ${version} to CHANGELOG.md.`)
const rest = lines.slice(start + 1)
const end = rest.findIndex((line) => line.startsWith('## ['))
const notes = (end < 0 ? rest : rest.slice(0, end)).join('\n').trim()
if (!notes) throw new Error('Release notes must not be empty.')
if (command === 'notes') console.log(notes)
else {
  const versions = [
    JSON.parse(read('web/package.json')).version,
    read('Cargo.toml').match(/^version = "([^"]+)"/m)?.[1],
    read('windows/WinPalm/WinPalm.csproj').match(/<Version>([^<]+)<\/Version>/)?.[1],
    read('windows/WinPalm/app.manifest').match(/assemblyIdentity version="([^"]+)"/)?.[1]?.replace(/\.0$/, ''),
  ]
  if (versions.some((value) => value !== version)) throw new Error(`Release tag ${version} must match web, Rust, Windows project and manifest versions: ${versions.join(', ')}`)
  const repository = read('windows/WinPalm/Services/ReleaseUpdates.cs').match(/const string Repository = "([^"]+)"/)?.[1]
  if (process.env.GITHUB_REPOSITORY && process.env.GITHUB_REPOSITORY !== repository) throw new Error(`The updater targets ${repository}. Set its repository before releasing from ${process.env.GITHUB_REPOSITORY}.`)
  console.log(`WinPalm ${version}: release versions, notes and update repository agree.`)
}
