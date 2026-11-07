import { enableAutoUnmount, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import { FakeSocket, lastSocket, welcome } from './fakeSocket'

let conn: typeof import('../src/lib/connection')
enableAutoUnmount(afterEach)
beforeEach(async () => {
  vi.stubGlobal('WebSocket', FakeSocket)
  vi.stubGlobal('fetch', () => Promise.reject(new Error('offline')))
  FakeSocket.all = []
  localStorage.clear()
  localStorage.setItem('winpalm.token', 'saved')
  vi.resetModules()
  conn = await import('../src/lib/connection')
  conn.connect()
  lastSocket().open()
  lastSocket().receive(welcome)
})
afterEach(() => { conn.disconnect(false); vi.restoreAllMocks(); vi.unstubAllGlobals(); document.body.innerHTML = '' })

function finger(el: Element, type: string, pointerId = 1) {
  vi.spyOn(document, 'elementFromPoint').mockReturnValue(el)
  el.dispatchEvent(new PointerEvent(type, { pointerId, pointerType: 'touch', bubbles: true, cancelable: true }))
}

describe('Windows keyboard input', () => {
  it('taps Win by itself, and Ctrl is released before the next key', async () => {
    const { default: Keyboard } = await import('../src/components/VirtualKeyboard.vue')
    const { Key } = await import('../src/lib/keys')
    const { modifiers } = await import('../src/lib/input')
    const view = mount(Keyboard, { attachTo: document.body })
    const key = (code: number) => view.find(`[data-key="${code}"]`).element
    finger(key(Key.Command), 'pointerdown'); finger(key(Key.Command), 'pointerup')
    finger(key(Key.Control), 'pointerdown'); finger(key(Key.Control), 'pointerup')
    finger(key(Key.A), 'pointerdown'); finger(key(Key.A), 'pointerup')
    expect(lastSocket().sent.filter((message) => message.t === 'key')).toMatchObject([
      { code: Key.Command, down: true, repeat: false }, { code: Key.Command, down: false },
      { code: Key.Control, down: true, repeat: false }, { code: Key.Control, down: false },
      { code: Key.A, down: true, mods: 0 }, { code: Key.A, down: false },
    ])
    expect(modifiers.active).toBe(0)
    expect(view.find(`[data-key="${Key.Command}"] svg`).exists()).toBe(true)
  })

  it('keeps Ctrl down for a multi-touch combo and releases it when leaving the keyboard', async () => {
    const { default: Keyboard } = await import('../src/components/VirtualKeyboard.vue')
    const { Key } = await import('../src/lib/keys')
    const { modifiers } = await import('../src/lib/input')
    const view = mount(Keyboard, { attachTo: document.body })
    const ctrl = view.find(`[data-key="${Key.Control}"]`).element
    const a = view.find(`[data-key="${Key.A}"]`).element
    finger(ctrl, 'pointerdown', 1)
    finger(a, 'pointerdown', 2); finger(a, 'pointerup', 2)
    view.unmount()
    expect(lastSocket().sent.filter((message) => message.t === 'key')).toMatchObject([
      { code: Key.Control, down: true }, { code: Key.A, down: true },
      { code: Key.A, down: false }, { code: Key.Control, down: false },
    ])
    expect(modifiers.active).toBe(0)
  })

  it('presses shortcut-strip modifiers normally by default, including a screen-reader tap', async () => {
    const { default: ModKey } = await import('../src/components/ModKey.vue')
    const { Key } = await import('../src/lib/keys')
    const { Mod } = await import('../src/lib/protocol')
    const { modifiers } = await import('../src/lib/input')
    const view = mount(ModKey, { props: { code: Key.Command, mod: Mod.Command, symbol: 'Win' }, attachTo: document.body })
    view.find('[role="button"]').element.dispatchEvent(new MouseEvent('click', { detail: 0, bubbles: true }))
    await nextTick()
    expect(lastSocket().sent.filter((message) => message.t === 'key')).toMatchObject([{ code: Key.Command, down: true }, { code: Key.Command, down: false }])
    expect(modifiers.active).toBe(0)
  })

  it('keeps custom modifiers momentary even when shortcut latches are enabled', async () => {
    const { default: Keyboard } = await import('../src/components/CustomKeyboard.vue')
    const { Key } = await import('../src/lib/keys')
    const { Mod } = await import('../src/lib/protocol')
    const { settings } = await import('../src/lib/settings')
    const { modifiers } = await import('../src/lib/input')
    settings.stickyModifiers = true; await nextTick()
    const board = { id: 'b', name: 'Test', cols: 1, rows: 1, trackpad: false, keys: [{ id: 'ctrl', x: 0, y: 0, w: 1, h: 1, action: { kind: 'mod' as const, mod: Mod.Control } }] }
    const view = mount(Keyboard, { props: { board, editing: false }, attachTo: document.body })
    finger(view.find('.key').element, 'pointerdown'); finger(view.find('.key').element, 'pointerup')
    expect(lastSocket().sent.filter((message) => message.t === 'key')).toMatchObject([{ code: Key.Control, down: true }, { code: Key.Control, down: false }])
    expect(modifiers.active).toBe(0)
  })
})
