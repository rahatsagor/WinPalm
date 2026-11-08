import { afterEach, beforeEach, describe, expect, test } from 'bun:test'
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { spawnSync } from 'node:child_process'
import { bumpVersion, checkVersions, normalizeVersion } from './version'

const sourceRoot = resolve(import.meta.dir, '..')
const fixtureFiles = [
  'Cargo.toml',
  'Cargo.lock',
  'web/package.json',
  'windows/WinPalm/WinPalm.csproj',
  'windows/WinPalm/app.manifest',
  'windows/WinPalm/Services/ReleaseUpdates.cs',
  'CHANGELOG.md',
  'scripts/version.ts',
  'scripts/release-notes.ts',
]
const versionFiles = fixtureFiles.slice(0, 5)
let fixtureRoot = ''

function makeFixture() {
  const root = mkdtempSync(join(tmpdir(), 'winpalm-release-'))
  for (const path of fixtureFiles) {
    const destination = join(root, path)
    mkdirSync(dirname(destination), { recursive: true })
    copyFileSync(join(sourceRoot, path), destination)
  }
  // Keep fixtures stable when the actual app moves to its next release.
  bumpVersion(root, '0.1.0')
  return root
}

function writeFixture(path: string, text: string) {
  writeFileSync(join(fixtureRoot, path), text)
}

function readFixture(path: string) {
  return readFileSync(join(fixtureRoot, path), 'utf8')
}

function runReleaseNotes(command: 'check' | 'notes', version = '0.1.0', repository = 'rahatsagor/WinPalm') {
  return spawnSync(process.execPath, [join(fixtureRoot, 'scripts/release-notes.ts'), command, version], {
    cwd: fixtureRoot,
    env: { ...process.env, GITHUB_REPOSITORY: repository },
    encoding: 'utf8',
  })
}

beforeEach(() => {
  fixtureRoot = makeFixture()
})

afterEach(() => {
  rmSync(fixtureRoot, { recursive: true, force: true })
})

describe('release versions', () => {
  test('accepts canonical stable versions within Windows component limits', () => {
    expect(normalizeVersion('0.1.0')).toBe('0.1.0')
    expect(normalizeVersion('v1.23.456')).toBe('1.23.456')
    expect(normalizeVersion('65534.65534.65534')).toBe('65534.65534.65534')
  })

  test.each([undefined, '', 'v', '01.2.3', '1.02.3', '1.2.03', '1.2', '1.2.3.4', '1.2.-3', '1.2.3-beta.1', '1.65535.0', '65535.0.0'])('rejects invalid version %s', (version) => {
    expect(() => normalizeVersion(version)).toThrow()
  })

  test('checks all five version sources, including the core lock entry and four-part manifest version', () => {
    expect(checkVersions(fixtureRoot, '0.1.0')).toBe('0.1.0')
    const changedFiles = [
      ['Cargo.toml', 'version = "0.1.0"', 'version = "0.1.1"'],
      ['Cargo.lock', 'name = "winpalm-core"\nversion = "0.1.0"', 'name = "winpalm-core"\nversion = "0.1.1"'],
      ['web/package.json', '"version": "0.1.0"', '"version": "0.1.1"'],
      ['windows/WinPalm/WinPalm.csproj', '<Version>0.1.0</Version>', '<Version>0.1.1</Version>'],
      ['windows/WinPalm/app.manifest', 'version="0.1.0.0"', 'version="0.1.1.0"'],
    ] as const

    for (const [path, expected, replacement] of changedFiles) {
      const original = readFixture(path)
      writeFixture(path, original.replace(expected, replacement))
      expect(() => checkVersions(fixtureRoot, '0.1.0'), path).toThrow()
      writeFixture(path, original)
    }
  })

  test('bumps all five version sources and preserves unrelated Cargo.lock package versions', () => {
    const lockBefore = readFixture('Cargo.lock')
    expect(bumpVersion(fixtureRoot, '0.2.4')).toBe('0.2.4')
    expect(readFixture('Cargo.toml')).toContain('version = "0.2.4"')
    expect(readFixture('Cargo.lock')).toContain('name = "winpalm-core"\nversion = "0.2.4"')
    expect(readFixture('web/package.json')).toContain('"version": "0.2.4"')
    expect(readFixture('windows/WinPalm/WinPalm.csproj')).toContain('<Version>0.2.4</Version>')
    expect(readFixture('windows/WinPalm/app.manifest')).toContain('version="0.2.4.0"')
    expect(readFixture('Cargo.lock').replace('name = "winpalm-core"\nversion = "0.2.4"', 'name = "winpalm-core"\nversion = "0.1.0"'))
      .toBe(lockBefore)
  })

  test('leaves every version file untouched when a source is missing or duplicated', () => {
    for (const scenario of ['missing', 'duplicate'] as const) {
      const root = makeFixture()
      try {
        const paths = versionFiles.map(path => join(root, path))
        const manifest = join(root, 'windows/WinPalm/app.manifest')
        if (scenario === 'missing') rmSync(manifest)
        else writeFileSync(manifest, readFileSync(manifest, 'utf8').replace('version="0.1.0.0"', 'version="0.1.0.0"\n  <assemblyIdentity version="0.1.0.0" name="duplicate" />'))
        const before = new Map(paths.filter(existsSync).map(path => [path, readFileSync(path, 'utf8')]))

        expect(() => bumpVersion(root, '0.2.0')).toThrow()
        for (const [path, text] of before) expect(readFileSync(path, 'utf8')).toBe(text)
      } finally {
        rmSync(root, { recursive: true, force: true })
      }
    }
  })

  test('does not modify source files for an invalid requested version', () => {
    const before = versionFiles.map(path => readFixture(path))
    expect(() => bumpVersion(fixtureRoot, '0.65535.0')).toThrow()
    versionFiles.forEach((path, index) => expect(readFixture(path)).toBe(before[index]))
  })
})

describe('release notes command', () => {
  test.each([
    ['missing', '## [Unreleased]\n\n- Work in progress.\n'],
    ['empty', '## [0.1.0] - 2026-01-01\n\n## [Unreleased]\n'],
    ['duplicate', '## [0.1.0]\n\nFirst notes.\n\n## [0.1.0] - 2026-01-01\n\nSecond notes.\n'],
  ])('rejects %s changelog sections', (_scenario, changelog) => {
    writeFixture('CHANGELOG.md', changelog)
    const result = runReleaseNotes('check')
    expect(result.status).not.toBe(0)
  })

  test('rejects a release run from the wrong GitHub repository', () => {
    const result = runReleaseNotes('check', '0.1.0', 'someone-else/WinPalm')
    expect(result.status).not.toBe(0)
    expect(result.stderr).toContain('Release only from its configured repository.')
  })

  test('emits version-specific setup and the public docs, film, and CmdPalm links', () => {
    writeFixture('CHANGELOG.md', '## [0.1.0] - 2026-01-01\n\n- First release notes.\n')
    const result = runReleaseNotes('notes', '0.1.0')
    expect(result.status).toBe(0)
    expect(result.stdout).toContain('https://github.com/rahatsagor/WinPalm/releases/download/v0.1.0/WinPalm-Setup.exe')
    expect(result.stdout).toContain('https://winpalm.rsapplication.com/docs/')
    expect(result.stdout).toContain('https://winpalm.rsapplication.com/#film')
    expect(result.stdout).toContain('https://cmdpalm.app')
  })
})
