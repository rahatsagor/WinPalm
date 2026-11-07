import { copyFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { createHash } from 'node:crypto'
import { parseArgs } from 'node:util'

const root = resolve(import.meta.dir, '..')
const { values } = parseArgs({ args: Bun.argv.slice(2), options: { payloads: { type: 'string' }, output: { type: 'string' } }, strict: true })
if (!values.payloads || !values.output) throw new Error('Usage: bun scripts/build-installer.ts --payloads BUILDS_DIRECTORY --output SETUP.exe')
const { version } = JSON.parse(readFileSync(resolve(root, 'web/package.json'), 'utf8')) as { version: string }
if (!/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(version)) throw new Error('Use a stable three-part version.')

// Read the native VERSIONINFO resource, including from single-file apphosts.
// Unix FileVersionInfo cannot read that Windows resource, so inspect the PE
// resource tree directly when building installers on macOS or Linux.
function peInfo(file: string) {
  const data = readFileSync(file)
  const word = (at: number) => data.readUInt16LE(at)
  const dword = (at: number) => data.readUInt32LE(at)
  if (word(0) !== 0x5a4d) throw new Error(`Not a Windows executable: ${file}`)
  const pe = dword(0x3c)
  if (dword(pe) !== 0x4550) throw new Error(`Invalid PE header: ${file}`)
  const machine = word(pe + 4), optional = pe + 24, sectionsAt = optional + word(pe + 20)
  const sections = Array.from({ length: word(pe + 6) }, (_, i) => sectionsAt + i * 40)
  const offset = (rva: number) => {
    const section = sections.find(at => rva >= dword(at + 12) && rva < dword(at + 12) + Math.max(dword(at + 8), dword(at + 16)))
    if (section === undefined) throw new Error(`Invalid PE resource location: ${file}`)
    return dword(section + 20) + rva - dword(section + 12)
  }
  const magic = word(optional)
  if (magic !== 0x20b && magic !== 0x10b) throw new Error(`Unsupported PE format: ${file}`)
  const resource = offset(dword(optional + (magic === 0x20b ? 112 : 96) + 16))
  const entries = (at: number) => Array.from({ length: word(at + 12) + word(at + 14) }, (_, i) => ({ id: dword(at + 16 + i * 8), value: dword(at + 20 + i * 8) }))
  let value = entries(resource).find(item => item.id === 16)?.value
  if (value === undefined) throw new Error(`No native executable version: ${file}`)
  for (let depth = 0; value & 0x80000000; depth++) {
    if (depth > 3) throw new Error(`Invalid version resource tree: ${file}`)
    const next = entries(resource + (value & 0x7fffffff))[0]?.value
    if (next === undefined) throw new Error(`Empty version resource: ${file}`)
    value = next
  }
  const entry = resource + value, bytes = data.subarray(offset(dword(entry)), offset(dword(entry)) + dword(entry + 4))
  const fixed = bytes.indexOf(Buffer.from([0xbd, 0x04, 0xef, 0xfe]))
  if (fixed < 0) throw new Error(`Invalid fixed version resource: ${file}`)
  const ms = bytes.readUInt32LE(fixed + 8), ls = bytes.readUInt32LE(fixed + 12)
  return { machine, version: [ms >>> 16, ms & 65535, ls >>> 16, ls & 65535].join('.') }
}

const payloads = resolve(values.payloads), output = resolve(values.output)
for (const [arch, machine] of [['x64', 0x8664], ['arm64', 0xaa64]] as const) {
  const directory = resolve(payloads, arch)
  for (const file of ['WinPalm.exe', 'winpalm_core.dll', 'web/index.html', 'web/source.zip', 'web/LICENSE.txt', 'Resources/WinPalm.ico', 'LICENSE'])
    if (!existsSync(resolve(directory, file))) throw new Error(`Incomplete ${arch} payload: ${file}`)
  const app = peInfo(resolve(directory, 'WinPalm.exe'))
  if (app.machine !== machine || app.version !== `${version}.0`) throw new Error(`${arch} payload must be native WinPalm ${version}, got ${JSON.stringify(app)}`)
}
const compiler = process.env.MAKENSIS ?? Bun.which('makensis') ?? (process.platform === 'win32' ? 'C:\\Program Files (x86)\\NSIS\\makensis.exe' : '')
if (!compiler || !existsSync(compiler)) throw new Error('Install NSIS 3 (makensis), or set MAKENSIS to its compiler path.')
mkdirSync(dirname(output), { recursive: true })
const prefix = process.platform === 'win32' ? '/' : '-'
const argumentsFor = [`${prefix}DVERSION=${version}`, `${prefix}DX64_DIR=${resolve(payloads, 'x64')}`, `${prefix}DARM64_DIR=${resolve(payloads, 'arm64')}`, `${prefix}DOUTPUT_FILE=${output}`, resolve(root, 'packaging/WinPalm.nsi')]
const result = Bun.spawnSync([compiler, ...argumentsFor], { cwd: root, stdout: 'inherit', stderr: 'inherit' })
if (result.exitCode !== 0) throw new Error(`NSIS failed (${result.exitCode}).`)
const setup = peInfo(output)
if (setup.machine !== 0x14c || setup.version !== `${version}.0`) throw new Error('The setup executable has unexpected architecture or version metadata.')
const checksum = (file: string) => createHash('sha256').update(readFileSync(file)).digest('hex')
writeFileSync(output + '.sha256', `${checksum(output)}  ${output.split(/[\\/]/).at(-1)}\n`)
const alias = resolve(dirname(output), 'WinPalm-Setup.exe')
if (alias !== output) {
  copyFileSync(output, alias)
  writeFileSync(alias + '.sha256', `${checksum(alias)}  WinPalm-Setup.exe\n`)
}
console.log(`WinPalm ${version}: universal setup EXE built, version checked and checksummed.`)
