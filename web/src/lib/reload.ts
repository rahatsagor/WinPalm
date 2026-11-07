// After a WinPalm update on the PC, an open page still refers to the previous
// build's lazily loaded files, which the PC no longer serves. Reloading picks
// up the new build; the guards keep a broken setup from reloading in a loop.

import { defineAsyncComponent, type Component } from 'vue'

const KEY = 'winpalm.reloadedAt'
const VERSION_KEY = 'winpalm.reloadedFor'
const MIN_GAP_MS = 10_000

export function reloadForUpdate(): boolean {
  try {
    if (Date.now() - Number(sessionStorage.getItem(KEY)) < MIN_GAP_MS) return false
    sessionStorage.setItem(KEY, String(Date.now()))
  } catch {
    return false
  }
  location.reload()
  return true
}

/** Reload once per host version when the PC runs a different WinPalm than this page. */
export function checkHostVersion(version: string) {
  if (!version || version === __APP_VERSION__) return
  try {
    if (sessionStorage.getItem(VERSION_KEY) === version) return
    if (reloadForUpdate()) sessionStorage.setItem(VERSION_KEY, version)
  } catch {
    // No session storage: reloadForUpdate refuses too, so there is no loop.
  }
}

/** A lazily loaded component that reloads the page if its file is gone. */
export function lazy(loader: () => Promise<Component | { default: Component }>) {
  return defineAsyncComponent({
    loader,
    onError(_error, _retry, fail) {
      reloadForUpdate()
      fail()
    },
  })
}

export function installReloadOnStaleBuild() {
  addEventListener('vite:preloadError', (event) => {
    if (reloadForUpdate()) event.preventDefault()
  })
}
