import { readFileSync, writeFileSync } from 'node:fs'
import { resolve } from 'node:path'

export function normalizeVersion(raw: string | undefined): string {
  const version = raw?.replace(/^v/, '')
  if (!version || !/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(version)
    || version.split('.').some(part => Number(part) > 65534)) {
    throw new Error('Use a stable three-part version, for example 0.1.0 (each part must be below 65535).')
  }
  return version
}

const sources = [
  { path: 'Cargo.toml', pattern: /^(version = ")([^"]+)(")$/m },
  { path: 'Cargo.lock', pattern: /(\[\[package\]\]\nname = "winpalm-core"\nversion = ")([^"]+)(")/ },
  { path: 'web/package.json', pattern: /^(  "version": ")([^"]+)(",?)$/m },
  { path: 'windows/WinPalm/WinPalm.csproj', pattern: /(<Version>)([^<]+)(<\/Version>)/ },
  { path: 'windows/WinPalm/app.manifest', pattern: /(<assemblyIdentity version=")([^"]+)(")/, manifest: true },
] as const

function readSources(root: string) {
  return sources.map(source => {
    const text = readFileSync(resolve(root, source.path), 'utf8')
    const matches = [...text.matchAll(new RegExp(source.pattern, source.pattern.flags + 'g'))]
    if (matches.length !== 1) throw new Error(`Expected exactly one version in ${source.path}.`)
    return { ...source, text, version: matches[0]![2]! }
  })
}

export function checkVersions(root: string, raw: string | undefined): string {
  const version = normalizeVersion(raw)
  for (const source of readSources(root)) {
    const expected = 'manifest' in source ? `${version}.0` : version
    if (source.version !== expected) throw new Error(`${source.path} has version ${source.version}; expected ${expected}. Run bun scripts/bump-version.ts ${version}.`)
  }
  return version
}

export function bumpVersion(root: string, raw: string | undefined): string {
  const version = normalizeVersion(raw)
  // Read and validate every source before changing any file.
  const updates = readSources(root).map(source => ({
    path: resolve(root, source.path),
    text: source.text.replace(source.pattern, (_match, before: string, _old: string, after: string) => `${before}${version}${'manifest' in source ? '.0' : ''}${after}`),
  }))
  for (const update of updates) writeFileSync(update.path, update.text)
  return version
}
