import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'
import tailwindcss from '@tailwindcss/vite'

// During development, run the PC app (or `cargo run --example dev-host`) and
// point the proxy at it so the client talks to a real host.
const host = process.env.WINPALM_HOST ?? 'http://127.0.0.1:8790'
const { version } = JSON.parse(readFileSync(new URL('./package.json', import.meta.url), 'utf8')) as { version: string }

export default defineConfig({
  // Public assets already use browser-root URLs. Keep them out of module
  // resolution, which otherwise creates drive-less file URLs on Windows.
  plugins: [vue({ template: { transformAssetUrls: { includeAbsolute: false } } }), tailwindcss()],
  define: { __APP_VERSION__: JSON.stringify(version) },
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  build: {
    outDir: process.env.WINPALM_WEB_OUT ?? '../../winpalm-dev/artifacts/web',
    emptyOutDir: true,
    target: 'safari16.4',
    assetsInlineLimit: 0,
    reportCompressedSize: false,
  },
  server: {
    proxy: {
      '/ws': { target: host.replace(/^http/, 'ws'), ws: true },
      '/api': host,
      '/winpalm.crt': host,
    },
  },
  test: {
    environment: 'happy-dom',
    include: ['test/**/*.test.ts'],
  },
})
