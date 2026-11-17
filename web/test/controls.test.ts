import { enableAutoUnmount, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
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
})

afterEach(() => {
  conn.disconnect(false)
  document.body.innerHTML = ''
  vi.unstubAllGlobals()
})

function connect() {
  conn.connect()
  const socket = lastSocket()
  socket.open()
  socket.receive(welcome)
  return socket
}

/** A screen reader's click: no press before it, so a detail of 0. */
const readerClick = (el: Element) => el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 0 }))
const enter = (el: Element) => el.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', bubbles: true, cancelable: true }))

describe('controls used without touch', () => {
  it('releases only the click bar owner when pointer capture is lost', async () => {
    const socket = connect()
    const input = await import('../src/lib/input')
    const { default: ClickBar } = await import('../src/components/ClickBar.vue')
    const view = mount(ClickBar, { attachTo: document.body })
    const left = view.find('[aria-label="left mouse button"]').element
    const other = input.buttonDown('left')
    const pointer = (type: string) => left.dispatchEvent(new PointerEvent(type, { pointerId: 1, pointerType: 'touch', bubbles: true }))
    pointer('pointerdown')
    pointer('lostpointercapture')
    expect(socket.sent.filter(message => message.t === 'button')).toMatchObject([{ op: 'down' }])
    other()
    expect(socket.sent.filter(message => message.t === 'button')).toMatchObject([{ op: 'down' }, { op: 'up' }])
    pointer('lostpointercapture'); pointer('pointerup')
    expect(socket.sent.filter(message => message.t === 'button')).toHaveLength(2)
  })
  it('ignores old button and media cleanup after release, pause and reconnect', async () => {
    const socket = connect()
    const input = await import('../src/lib/input')
    for (const reset of [() => input.releaseAll(), () => { socket.receive({ t: 'paused', paused: true }); socket.receive({ t: 'paused', paused: false }) }]) {
      const oldButton = input.buttonDown('left')
      const oldMedia = input.pressMedia('rewind')
      reset()
      const newButton = input.buttonDown('left')
      const newMedia = input.pressMedia('rewind')
      const before = socket.sent.length
      oldButton(); oldButton(); oldMedia(); oldMedia()
      expect(socket.sent).toHaveLength(before)
      newButton(); newMedia()
      expect(socket.sent.slice(-2)).toMatchObject([{ t: 'button', op: 'up' }, { t: 'media', op: 'up' }])
    }
    const old = input.pressMedia('rewind')
    socket.drop()
    conn.retryNow(); lastSocket().open(); lastSocket().receive(welcome)
    const current = input.pressMedia('rewind')
    const before = lastSocket().sent.length
    old()
    expect(lastSocket().sent).toHaveLength(before)
    current()
    expect(lastSocket().sent.at(-1)).toMatchObject({ t: 'media', op: 'up' })
  })
  it('keeps shared mouse and seeking keys held until the last finger lifts', async () => {
    const socket = connect()
    const input = await import('../src/lib/input')
    input.buttonDown('left')
    input.buttonDown('left')
    input.buttonUp('left')
    expect(socket.sent.filter((m) => m.t === 'button')).toMatchObject([{ button: 'left', op: 'down' }])
    input.buttonUp('left')
    expect(socket.sent.filter((m) => m.t === 'button')).toMatchObject([{ op: 'down' }, { op: 'up' }])

    const first = input.pressMedia('rewind')
    const second = input.pressMedia('rewind')
    first()
    first()
    expect(socket.sent.filter((m) => m.t === 'media')).toMatchObject([{ key: 'rewind', op: 'down' }])
    second()
    expect(socket.sent.filter((m) => m.t === 'media')).toMatchObject([{ op: 'down' }, { op: 'up' }])

    input.buttonDown('left')
    input.pressMedia('rewind')
    input.releaseAll()
    const before = socket.sent.length
    input.buttonUp('left')
    input.media('rewind', 'up')
    expect(socket.sent).toHaveLength(before)
    input.buttonDown('left')
    expect(socket.sent.at(-1)).toMatchObject({ t: 'button', op: 'down' })
  })

  it('reorders local tabs with Alt + Left without sending a lone Alt to Windows', async () => {
    const socket = connect()
    const { installHardwareKeyboard } = await import('../src/lib/hardware')
    const { default: GroupTabs } = await import('../src/components/GroupTabs.vue')
    installHardwareKeyboard()
    const view = mount(GroupTabs, { props: { items: [{ id: 'a', name: 'One' }, { id: 'b', name: 'Two' }], active: 'a', editing: true, label: 'Groups' }, attachTo: document.body })
    const tab = view.find('[data-group-id="b"]').element
    tab.dispatchEvent(new KeyboardEvent('keydown', { key: 'Alt', code: 'AltLeft', altKey: true, bubbles: true, cancelable: true }))
    tab.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', code: 'ArrowLeft', altKey: true, bubbles: true, cancelable: true }))
    tab.dispatchEvent(new KeyboardEvent('keyup', { key: 'ArrowLeft', code: 'ArrowLeft', altKey: true, bubbles: true, cancelable: true }))
    tab.dispatchEvent(new KeyboardEvent('keyup', { key: 'Alt', code: 'AltLeft', bubbles: true, cancelable: true }))
    expect(view.emitted('reorder')).toEqual([[['b', 'a']]])
    expect(socket.sent.filter((message) => message.t === 'key')).toEqual([])
  })
  it('click the PC from the click bar', async () => {
    const socket = connect()
    const { default: ClickBar } = await import('../src/components/ClickBar.vue')
    const view = mount(ClickBar, { attachTo: document.body })
    const left = view.find('[aria-label="left mouse button"]')
    expect(left.attributes('tabindex')).toBe('0')
    readerClick(left.element)
    enter(left.element)
    expect(socket.sent.filter((m) => m.t === 'button')).toMatchObject([
      { button: 'left', op: 'click' },
      { button: 'left', op: 'click' },
    ])
  })

  it('latch a modifier key like a tap', async () => {
    connect()
    const { default: ModKey } = await import('../src/components/ModKey.vue')
    const { modifiers } = await import('../src/lib/input')
    const { Key } = await import('../src/lib/keys')
    const { Mod } = await import('../src/lib/protocol')
    const { settings } = await import('../src/lib/settings')
    settings.stickyModifiers = true
    await import('vue').then(({ nextTick }) => nextTick())
    const view = mount(ModKey, { props: { mod: Mod.Command, code: Key.Command, symbol: '⌘', label: 'Command' }, attachTo: document.body })
    view.find('[role="button"]').element.dispatchEvent(new KeyboardEvent('keydown', { key: ' ', bubbles: true, cancelable: true }))
    expect(modifiers.status(Mod.Command)).toBe('latched')
  })

  it('tap the composer’s keys, without a hardware keyboard also sending Return', async () => {
    const socket = connect()
    const { installHardwareKeyboard } = await import('../src/lib/hardware')
    installHardwareKeyboard()
    const { Key } = await import('../src/lib/keys')
    const { default: Composer } = await import('../src/components/Composer.vue')
    const view = mount(Composer, { attachTo: document.body })
    enter(view.find('[aria-label="Left"]').element)
    readerClick(view.find('[aria-label="Return"]').element)
    expect(socket.sent.filter((m) => m.t === 'tap')).toMatchObject([{ code: Key.Left }, { code: Key.Return }])
    expect(socket.sent.filter((m) => m.t === 'key')).toEqual([])
  })
})

describe('forgetting a PC', () => {
  it('offers token reconnection after a retryable refusal from the PC', async () => {
    conn.connect()
    lastSocket().open()
    lastSocket().receive({ t: 'denied', reason: 'busy', retryAfter: 5 })
    const { default: ConnectView } = await import('../src/views/ConnectView.vue')
    const view = mount(ConnectView, { attachTo: document.body })
    expect(view.text()).toContain('quit and reopen WinPalm')
    await view.findAll('button').find((button) => button.text() === 'Reconnect')!.trigger('click')
    lastSocket().open()
    expect(lastSocket().sent[0]).toMatchObject({ t: 'hello', auth: { method: 'token', token: 'saved' } })
  })

  it('drops its app list', () => {
    const socket = connect()
    socket.receive({ t: 'apps', apps: [{ id: 'a', name: 'A', running: false, active: false, hidden: false }] })
    expect(conn.state.apps).toHaveLength(1)
    conn.disconnect(true)
    expect(conn.state.apps).toEqual([])
    expect(conn.state.appsLoaded).toBe(false)
  })
})
