import { mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import { FakeSocket, lastSocket, welcome } from './fakeSocket'

let connection: typeof import('../src/lib/connection')
let display: typeof import('../src/lib/screen')
let settings: typeof import('../src/lib/settings').settings

function connect() {
  connection.connect()
  lastSocket().open()
  lastSocket().receive({ ...welcome, features: { ...welcome.features, display: true } })
  return lastSocket()
}

beforeEach(async () => {
  vi.useFakeTimers()
  vi.stubGlobal('WebSocket', FakeSocket)
  vi.stubGlobal('RTCPeerConnection', class {})
  FakeSocket.all = []
  localStorage.clear()
  localStorage.setItem('winpalm.token', 'saved')
  vi.resetModules()
  connection = await import('../src/lib/connection')
  display = await import('../src/lib/screen')
  ;({ settings } = await import('../src/lib/settings'))
})

afterEach(() => {
  display.deactivateScreen()
  connection.disconnect(false)
  document.body.innerHTML = ''
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

describe('DisplayView', () => {
  it('switches controls, updates preview space, and deactivates its viewer on unmount', async () => {
    const socket = connect()
    const { default: DisplayView } = await import('../src/views/DisplayView.vue')
    const view = mount(DisplayView, { attachTo: document.body })
    const listRequest = socket.sent.find(message => message.t === 'displays')!
    socket.receive({ t: 'displays', id: listRequest.id, displays: [{ id: 'main', name: 'Main display', width: 1920, height: 1080, main: true }] })
    await nextTick()

    const controls = view.find('[aria-label="Controls below the display"]')
    await controls.findAll('[role="radio"]').find(button => button.text() === 'Keyboard')!.trigger('click')
    expect(settings.displayControls).toBe('keyboard')
    expect(view.findComponent({ name: 'KeyboardView' }).exists()).toBe(true)

    await view.find('button[aria-label="Display options"]').trigger('click')
    await nextTick()
    const range = view.findComponent({ name: 'RangeRow' }).find('input[type="range"]')
    await range.setValue('0.62')
    expect(settings.displaySplit).toBe(0.62)

    view.unmount()
    expect(display.screen.active).toBe(false)
  })

  it('enters fullscreen and exits it from Escape', async () => {
    connect()
    const { default: DisplayView } = await import('../src/views/DisplayView.vue')
    const view = mount(DisplayView, { attachTo: document.body })
    display.screen.status = 'live'
    await nextTick()

    await view.find('button[aria-label="Fullscreen display"]').trigger('click')
    expect(view.findAll('button').some(button => button.text().includes('Exit'))).toBe(true)
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
    await nextTick()
    expect(view.findAll('button').some(button => button.text().includes('Exit'))).toBe(false)
    view.unmount()
  })
})
