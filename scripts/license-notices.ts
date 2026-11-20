// Generate and verify notices from the exact locked dependency sources.
// Run after Bun install and .NET restore, with both Windows runtimes fetched.
import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { createHash } from 'node:crypto'
import { dirname, join, relative, resolve } from 'node:path'
import { homedir } from 'node:os'
import { parseArgs } from 'node:util'

const root = resolve(import.meta.dir, '..')
const hash = (data: string | Buffer) => createHash('sha256').update(data).digest('hex')
const normalized = (text: string) => text.replace(/\r\n?/g, '\n').replace(/[\t ]+$/gm, '').trimEnd()
const licenseTexts = new Map<string, string>()
function command(args: string[], cwd = root): string {
  const result = Bun.spawnSync(args, { cwd, stdout: 'pipe', stderr: 'pipe', maxBuffer: 64 * 1024 * 1024 })
  if (result.exitCode !== 0) throw new Error(`${args.join(' ')} failed: ${result.stderr.toString().trim()}`)
  return result.stdout.toString()
}

function noticeFiles(dir: string, licenseDirectory = false): string[] {
  const files: string[] = []
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory() && !['.git', 'target', 'node_modules'].includes(entry.name)) files.push(...noticeFiles(join(dir, entry.name), licenseDirectory || /^licen[cs]es?$/i.test(entry.name)))
    else if (entry.isFile() && (licenseDirectory || /^(?:unlicen[cs]e|licen[cs]e|copying|copyright|notice|patents?|third-party-notices)(?:[._-]|$)/i.test(entry.name))) files.push(join(dir, entry.name))
  }
  return files.sort()
}

function notices(name: string, version: string, license: string | null, dir: string, extra?: string | null): string {
  const files = new Set(noticeFiles(dir))
  if (extra) files.add(resolve(dir, extra))
  let provenance = ''
  if (!files.size) {
    const fallback = join(root, 'packaging/licenses/rust', `${name}-${version}`)
    if (existsSync(fallback)) {
      provenance = readFileSync(join(fallback, 'SOURCES.json'), 'utf8').trim()
      const sources = JSON.parse(provenance) as { name: string; version: string; files: { file: string; sha256: string }[] }
      if (sources.name !== name || sources.version !== version) throw new Error(`Wrong upstream notice version for ${name}`)
      for (const source of sources.files) {
        const file = join(fallback, source.file)
        if (hash(readFileSync(file)) !== source.sha256) throw new Error(`Changed upstream notice: ${file}`)
        files.add(file)
      }
      dir = fallback
    }
  }
  if (!files.size) throw new Error(`No upstream notice found for ${name} ${version}. Add a versioned source before shipping.`)
  const seen = new Set<string>()
  const parts = [`${name} ${version}\nLicense: ${license ?? 'See upstream text'}`]
  for (const file of [...files].sort()) {
    const text = normalized(readFileSync(file, 'utf8'))
    if (!text.trim() || seen.has(text)) continue
    seen.add(text)
    const digest = hash(text)
    licenseTexts.set(digest, text)
    parts.push(`--- ${relative(dir, file).replaceAll('\\', '/')} ---\nFull text: SHA-256 ${digest}`)
  }
  if (!seen.size) throw new Error(`Empty upstream notices for ${name} ${version}`)
  if (provenance) parts.push(`Upstream source provenance:\n${provenance}`)
  return parts.join('\n\n')
}

interface CargoPackage { name: string; version: string; authors: string[]; license: string | null; license_file: string | null; manifest_path: string }
function rustNotices(): string[] {
  // Cargo tree determines target/feature reachability. Metadata supplies text
  // locations only; traversing metadata alone also includes optional edges.
  const included = new Set<string>()
  const metadata = new Map<string, CargoPackage>()
  for (const target of ['x86_64-pc-windows-msvc', 'aarch64-pc-windows-msvc']) {
    const resolved = JSON.parse(command(['cargo', 'metadata', '--locked', '--offline', '--format-version', '1', '--filter-platform', target])) as { packages: CargoPackage[] }
    for (const pkg of resolved.packages) metadata.set(`${pkg.name}@${pkg.version}`, pkg)
    const tree = command(['cargo', 'tree', '--locked', '--offline', '--target', target, '--edges', 'normal,build', '--prefix', 'none', '--format', '{p}'])
    for (const line of tree.split('\n')) {
      const match = line.match(/^(\S+) v([^\s]+)/)
      if (match && match[1] !== 'winpalm-core') included.add(`${match[1]}@${match[2]}`)
    }
  }
  const packages = [...metadata.values()].filter(pkg => included.has(`${pkg.name}@${pkg.version}`))
  if (packages.length !== included.size) throw new Error('Missing resolved Rust package metadata')
  return packages.sort((a, b) => `${a.name}@${a.version}`.localeCompare(`${b.name}@${b.version}`, 'en')).map(pkg =>
    `Source: https://crates.io/crates/${pkg.name}/${pkg.version}\nAuthors listed by upstream: ${pkg.authors.join(', ')}\n\n${notices(pkg.name, pkg.version, pkg.license, dirname(pkg.manifest_path), pkg.license_file)}`)
}

interface BunLicense { name: string; paths: string[] }
function webNotices(web: string): string[] {
  const licenses = JSON.parse(command(['bun', 'pm', 'licenses', '--prod', '--json'], web)) as Record<string, BunLicense[]>
  const packages = Object.values(licenses).flat().map(pkg => ({ name: pkg.name, path: pkg.paths[0]! }))
  // These build tools emit preflight CSS and the module-preload helper.
  for (const name of ['tailwindcss', 'vite']) packages.push({ name, path: join(web, 'node_modules', name) })
  return packages.sort((a, b) => a.name.localeCompare(b.name, 'en')).map(pkg => {
    const manifest = JSON.parse(readFileSync(join(pkg.path, 'package.json'), 'utf8')) as { name: string; version: string; license: string }
    return `Source: https://www.npmjs.com/package/${manifest.name}/v/${manifest.version}\n\n${notices(manifest.name, manifest.version, manifest.license, pkg.path)}`
  })
}

interface Assets {
  packageFolders: Record<string, unknown>
  libraries: Record<string, { type: string }>
  project: { frameworks: Record<string, { downloadDependencies?: { name: string; version: string }[] }> }
}
function nugetNotices(assetsPath: string): string[] {
  const assets = JSON.parse(readFileSync(assetsPath, 'utf8')) as Assets
  const packages = new Map<string, string>()
  for (const [id, library] of Object.entries(assets.libraries)) {
    if (library.type === 'package') { const [name, version] = id.split('/'); packages.set(name!, version!) }
  }
  for (const framework of Object.values(assets.project.frameworks)) {
    for (const pkg of framework.downloadDependencies ?? []) {
      if (pkg.name === 'System.CodeDom' || /^Microsoft\.(?:NETCore\.App\.(?:Runtime|Host)|WindowsDesktop\.App\.Runtime)\.win-(?:x64|arm64)$/i.test(pkg.name)) {
        const match = pkg.version.match(/^\[([^,\]]+),\s*\1\]$/)
        if (!match) throw new Error(`Unpinned package download: ${pkg.name}`)
        const resolved = packages.get(pkg.name)
        if (resolved && resolved !== match[1]) throw new Error(`Inconsistent restored versions for ${pkg.name}`)
        packages.set(pkg.name, match[1]!)
      }
    }
  }
  // Both architectures must be represented even when publishing only one.
  for (const rid of ['win-x64', 'win-arm64']) for (const component of ['NETCore.App.Runtime', 'NETCore.App.Host', 'WindowsDesktop.App.Runtime']) {
    if (!packages.has(`Microsoft.${component}.${rid}`)) throw new Error(`Missing ${rid} runtime metadata. Restore both RuntimeIdentifiers first.`)
  }
  const folders = Object.keys(assets.packageFolders)
  const packageDir = (name: string, version: string) => {
    const folder = folders.map(base => join(base, name.toLowerCase(), version)).find(existsSync)
    if (!folder) throw new Error(`Missing restored NuGet package ${name} ${version}`)
    return folder
  }
  const xmlText = (xml: string, tag: string) => xml.match(new RegExp(`<${tag}\\b[^>]*>([\\s\\S]*?)</${tag}>`, 'i'))?.[1].trim() ?? ''
  const runtime = packageDir('Microsoft.NETCore.App.Runtime.win-x64', packages.get('Microsoft.NETCore.App.Runtime.win-x64')!)
  return [...packages].sort(([a], [b]) => a.localeCompare(b, 'en')).map(([name, version]) => {
    const dir = packageDir(name, version)
    const nuspec = readdirSync(dir).find(file => file.endsWith('.nuspec'))
    if (!nuspec) throw new Error(`Missing NuGet provenance: ${name}`)
    const xml = readFileSync(join(dir, nuspec), 'utf8')
    const license = xmlText(xml, 'license')
    // System.* archives declare MIT and Microsoft copyright in their nuspec,
    // but omit a license file. Include the exact matching runtime's MIT grant.
    const extra = !noticeFiles(dir).length && name.startsWith('System.') && license === 'MIT' ? join(runtime, 'LICENSE.TXT') : null
    let body = notices(name, version, license, dir, extra)
    if (extra) body = body.replace(relative(dir, extra).replaceAll('\\', '/'), 'MIT terms from Microsoft.NETCore.App.Runtime.win-x64/LICENSE.TXT')
    return `Source: https://www.nuget.org/packages/${name}/${version}\nCopyright from upstream nuspec: ${xmlText(xml, 'copyright')}\n\n${body}`
  })
}

const { values } = parseArgs({ args: Bun.argv.slice(2), options: {
  output: { type: 'string' }, check: { type: 'boolean' }, 'web-package': { type: 'string' }, 'nuget-assets': { type: 'string' },
}, strict: true })
if (!values.output || !values['nuget-assets']) throw new Error('Usage: bun scripts/license-notices.ts --output NOTICES.txt --nuget-assets project.assets.json [--web-package package.json] [--check]')
const web = dirname(resolve(values['web-package'] ?? process.env.WINPALM_WEB_PACKAGE ?? join(root, 'web/package.json')))
const sections = [
  'WinPalm licenses and third-party notices\n\nGenerated from exact locked sources for Windows x64 and ARM64, including native runtime notices, nested upstream texts, and Rust build dependencies. Web production dependencies are conservatively included, with Tailwind CSS and Vite helpers. The presence of a notice does not imply every component is shipped. Line endings and trailing horizontal whitespace are normalized; upstream wording and provenance are retained.',
  `WinPalm\nSource: https://github.com/rahatsagor/WinPalm\nLicense: AGPL-3.0-only\n\n${readFileSync(join(root, 'LICENSE'), 'utf8').trimEnd()}\n\n${readFileSync(join(root, 'NOTICE'), 'utf8').trimEnd()}`,
  `Windows installer: NSIS\nSource: https://nsis.sourceforge.io/\n\n${readFileSync(join(root, 'packaging/NSIS-LICENSE.txt'), 'utf8').trimEnd()}`,
  ...rustNotices(), ...webNotices(web), ...nugetNotices(resolve(values['nuget-assets'])),
]
// Include identical upstream texts once, with every source file mapped above.
const appendix = [...licenseTexts].sort(([a], [b]) => a.localeCompare(b, 'en')).map(([digest, text]) => `License text SHA-256 ${digest}\n\n${text}`)
const content = normalized([...sections, ...appendix].join('\n\n' + '='.repeat(78) + '\n\n')) + '\n'
if (content.includes(homedir()) || content.includes(root)) throw new Error('Build-machine paths must not appear in bundled notices')
const output = resolve(values.output)
if (values.check) {
  const bundled = readFileSync(output, 'utf8')
  if (bundled !== content) {
    const expected = bundled.split('\n'), generated = content.split('\n')
    const first = expected.findIndex((line, index) => line !== generated[index])
    const index = first < 0 ? expected.length : first
    const sources = (text: string) => new Set(text.match(/^Source: https?:\S+$/gm) ?? [])
    const bundledSources = sources(bundled), generatedSources = sources(content)
    for (const source of bundledSources.difference(generatedSources)) console.error(`Only in bundled notices: ${source}`)
    for (const source of generatedSources.difference(bundledSources)) console.error(`Only in generated notices: ${source}`)
    console.error(`First difference at line ${index + 1}\n  Bundled: ${JSON.stringify(expected[index])}\n  Generated: ${JSON.stringify(generated[index])}`)
    throw new Error('Bundled notices are stale. Regenerate with the same arguments without --check and review the upstream changes.')
  }
} else {
  mkdirSync(dirname(output), { recursive: true })
  writeFileSync(output, content)
}
console.log(`${values.check ? 'Verified' : 'Wrote'} ${sections.length - 3} dependency notices: ${output}`)
