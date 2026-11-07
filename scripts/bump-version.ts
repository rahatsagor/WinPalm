import { resolve } from 'node:path'
import { bumpVersion } from './version'

if (Bun.argv.length !== 3) throw new Error('Usage: bun scripts/bump-version.ts VERSION')
const version = bumpVersion(resolve(import.meta.dir, '..'), Bun.argv[2])
console.log(`Version set to ${version} in Rust, Cargo.lock, web, Windows project and manifest.`)
console.log(`Add a ## [${version}] section to CHANGELOG.md, then run bun scripts/release-notes.ts check ${version}.`)
