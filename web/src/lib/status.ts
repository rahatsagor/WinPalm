// Connection status shown in the header (portrait) or side rail (landscape).

import { computed, ref } from 'vue'
import { notify, state } from './connection'
import { releaseHardwareKeys } from './hardware'
import { modifiers, releaseAll } from './input'
import { modsLabel } from './keys'

export const online = computed(() => state.status === 'connected')
/** Modifiers left on (latched or locked), as symbols. */
export const stuck = computed(() => modsLabel(modifiers.state.locked | modifiers.state.latched))

/** True for a moment after `panic`, to confirm it on the button. */
export const released = ref(false)
let releasedTimer: ReturnType<typeof setTimeout> | undefined

export function explainPermission() {
  notify(
    'Windows is blocking remote input. An administrator app, a UAC prompt or the lock screen is in front on the PC. Switch to a regular app there; to control administrator apps, run WinPalm as administrator.',
    'warning',
    'permission')
}

/** Release every key and button this device holds on the PC. */
export function panic() {
  releaseHardwareKeys()
  releaseAll()
  notify('Released all keys and buttons on the PC.', 'info', 'release')
  released.value = true
  clearTimeout(releasedTimer)
  releasedTimer = setTimeout(() => (released.value = false), 1200)
}
