import { enableAutoUnmount, mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import SettingsSheet from '../src/components/SettingsSheet.vue'
import { state } from '../src/lib/connection'
import { resetSettings, settings } from '../src/lib/settings'
import { buildPayload, settingsBackup } from '../src/lib/settingsSync'

enableAutoUnmount(afterEach)

const sheet = () => document.querySelector<HTMLElement>('[role="dialog"][aria-label="Settings"]')!
const panel = () => sheet().querySelector<HTMLElement>('[role="tabpanel"]')!
const button = (root: HTMLElement, text: string) =>
  Array.from(root.querySelectorAll('button')).find((element) => element.textContent?.trim() === text)!

async function select(label: string) {
  button(sheet(), label).click()
  await nextTick()
}

beforeEach(() => {
  resetSettings()
  settings.tab = 'trackpad'
  state.status = 'connected'
  state.hostName = 'Studio PC'
  state.hostVersion = '0.3.2'
  state.latency = 7
  state.features = { voice: false, permission: true, secure: false, shell: false, display: true, displayOriginal: false }
  settingsBackup.error = ''
  mount(SettingsSheet, { attachTo: document.body })
})

describe('phone settings', () => {
  it('shows connection facts and opens every feature tab with legacy preferences', async () => {
    expect(sheet().querySelector('section.card')?.textContent).toContain('Studio PC')
    expect(sheet().querySelector('section.card')?.textContent).toContain('WinPalm 0.3.2 · 7 ms')
    expect(sheet().querySelector('section.card')?.textContent).toContain('Connected')
    expect(sheet().querySelector('section.card')?.textContent).not.toMatch(/HTTPS|encrypted/i)
    expect(sheet().querySelectorAll('[role="tab"]')).toHaveLength(5)
    expect(sheet().textContent).not.toContain('Show advanced settings')
    expect(buildPayload().settings.showAdvanced).toBe(false)
    expect(panel().querySelector('[aria-label="Acceleration"]')).not.toBeNull()
    expect(panel().querySelector('[aria-label="Hold to keep scrolling"]')).not.toBeNull()
    await select('Keyboard')
    expect(panel().textContent).toContain('Dictation setup')
    expect(panel().textContent).toContain('Enable local voice typing')
    expect(panel().querySelector('[aria-label="Sticky shortcut modifiers"]')).not.toBeNull()
    expect(panel().querySelector('[aria-label="Send text as"]')).not.toBeNull()
    await select('Display')
    const hd = Array.from(panel().querySelectorAll('button')).find((element) => element.textContent?.includes('HD text'))
    expect(hd?.disabled).toBe(true)
    state.features.displayOriginal = true
    await nextTick()
    hd?.click()
    expect(settings.displayQuality).toBe('original')
    await select('Deck')
    expect(panel().querySelector('[aria-label="Button size"]')).not.toBeNull()
    expect(settings.tab).toBe('trackpad')
  })

  it('lets the keyboard select tabs and Escape close only the gesture editor', async () => {
    const first = button(sheet(), 'Trackpad')
    first.focus()
    first.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true }))
    await nextTick()
    expect(document.activeElement).toBe(button(sheet(), 'Gestures'))
    expect(document.activeElement?.getAttribute('aria-selected')).toBe('true')
    const swipe = Array.from(panel().querySelectorAll('button')).find((element) => element.textContent?.includes('Swipe up'))!
    swipe.focus()
    swipe.click()
    await nextTick()
    const editor = document.querySelector<HTMLElement>('[role="dialog"][aria-label="Three-finger swipe up"]')!
    for (const kind of ['Run automation', 'Mouse click', 'Sequence', 'Wait']) expect(button(editor, kind)).toBeDefined()
    expect(button(editor, 'PowerShell command')).toBeUndefined()
    state.features.shell = true
    await nextTick()
    expect(button(editor, 'PowerShell command')).toBeDefined()
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', cancelable: true }))
    await nextTick()
    expect(editor.isConnected).toBe(false)
    expect(document.activeElement).toBe(swipe)
    expect(sheet().isConnected).toBe(true)
  })

  it('keeps device backup retry below the feature panel during a reconnect', async () => {
    settingsBackup.error = 'Could not save.'
    state.status = 'reconnecting'
    await nextTick()
    const retry = button(sheet(), 'Retry saving to the PC')
    expect(retry.disabled).toBe(true)
    expect(panel().contains(retry)).toBe(false)
    expect(panel().compareDocumentPosition(retry) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(sheet().querySelector('section.card')?.textContent).toContain('Reconnecting…')
  })
})
