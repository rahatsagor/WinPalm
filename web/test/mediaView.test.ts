import { mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import { FakeSocket, lastSocket, welcome } from './fakeSocket'

let conn: typeof import('../src/lib/connection')

beforeEach(async () => {
  vi.useFakeTimers()
  vi.stubGlobal('WebSocket', FakeSocket)
  Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' })
  FakeSocket.all = []
  localStorage.clear()
  localStorage.setItem('winpalm.token', 'saved')
  vi.resetModules()
  conn = await import('../src/lib/connection')
})

afterEach(() => {
  conn.disconnect(false)
  document.body.innerHTML = ''
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

async function mediaView() {
  conn.connect()
  const socket = lastSocket()
  socket.open()
  socket.receive(welcome)
  const { default: MediaView } = await import('../src/views/MediaView.vue')
  const view = mount(MediaView, { attachTo: document.body })
  await nextTick()
  return { socket, view }
}

const pointer = (el: Element, type: string, pointerId: number) =>
  el.dispatchEvent(new PointerEvent(type, { pointerId, pointerType: 'touch', isPrimary: pointerId === 1, bubbles: true }))
const mediaSent = (socket: FakeSocket) => socket.sent.filter((m) => m.t === 'media').map((m) => `${m.key} ${m.op}`)

describe('MediaView', () => {
  it('releases seeking and repeating controls when their pointer capture is lost', async () => {
    const { socket, view } = await mediaView()
    const rewind = view.find('button[aria-label="Rewind"]').element
    pointer(rewind, 'pointerdown', 1)
    pointer(rewind, 'lostpointercapture', 2)
    expect(mediaSent(socket)).toEqual(['rewind down'])
    pointer(rewind, 'lostpointercapture', 1)
    expect(mediaSent(socket)).toEqual(['rewind down', 'rewind up'])
    pointer(rewind, 'pointerup', 1)
    expect(mediaSent(socket)).toEqual(['rewind down', 'rewind up'])
    const brighter = view.find('button[aria-label="Display brightness up"]').element
    pointer(brighter, 'pointerdown', 1)
    pointer(brighter, 'lostpointercapture', 1)
    const count = mediaSent(socket).length
    vi.advanceTimersByTime(1000)
    expect(mediaSent(socket)).toHaveLength(count)
    view.unmount()
  })

  it.each(['pause', 'hide', 'unmount'] as const)('cancels a delayed volume update on %s', async mode => {
    const { socket, view } = await mediaView()
    socket.receive({ t: 'volume', value: 0.5, muted: false })
    await nextTick()
    const slider = view.find('input[type="range"]').element as HTMLInputElement
    for (const value of ['0.6', '0.7']) {
      slider.value = value
      slider.dispatchEvent(new Event('input', { bubbles: true }))
    }
    const sent = socket.sent.filter(message => message.t === 'volume').length
    expect(sent).toBeLessThan(2)
    if (mode === 'pause') {
      socket.receive({ t: 'paused', paused: true })
      socket.receive({ t: 'paused', paused: false })
    } else if (mode === 'hide') {
      Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'hidden' })
      document.dispatchEvent(new Event('visibilitychange'))
    } else view.unmount()
    vi.advanceTimersByTime(100)
    expect(socket.sent.filter(message => message.t === 'volume')).toHaveLength(sent)
    if (mode !== 'unmount') view.unmount()
  })
  it('works from a keyboard or screen reader, without doubling a tap', async () => {
    const { socket, view } = await mediaView()
    const play = view.find('button[aria-label="Play or pause"]')
    await play.trigger('click')
    expect(mediaSent(socket)).toEqual(['playPause click'])

    pointer(play.element, 'pointerdown', 1)
    pointer(play.element, 'pointerup', 1)
    await play.trigger('click', { detail: 1 })
    expect(mediaSent(socket)).toEqual(['playPause click', 'playPause click'])

    await view.find('button[aria-label="Rewind"]').trigger('click')
    expect(mediaSent(socket).slice(2)).toEqual(['rewind down', 'rewind up'])
    view.unmount()
  })

  it('keeps a held key down when another finger lifts from it', async () => {
    const { socket, view } = await mediaView()
    const rewind = view.find('button[aria-label="Rewind"]').element
    pointer(rewind, 'pointerdown', 1)
    pointer(rewind, 'pointerdown', 2)
    pointer(rewind, 'pointerup', 2)
    expect(mediaSent(socket)).toEqual(['rewind down'])
    pointer(rewind, 'pointerup', 1)
    expect(mediaSent(socket)).toEqual(['rewind down', 'rewind up'])
    view.unmount()
  })

  it('stops repeating media keys and clears the held control when the PC pauses', async () => {
    const { socket, view } = await mediaView()
    const brighter = view.find('button[aria-label="Display brightness up"]').element
    brighter.setPointerCapture = () => {}
    pointer(brighter, 'pointerdown', 1)
    vi.advanceTimersByTime(550)
    expect(mediaSent(socket).length).toBeGreaterThanOrEqual(2)

    socket.receive({ t: 'paused', paused: true })
    await nextTick()
    const atPause = mediaSent(socket).length
    vi.advanceTimersByTime(500)
    expect(mediaSent(socket)).toHaveLength(atPause)
    expect(brighter.className).not.toContain('bg-surface-3')

    socket.receive({ t: 'paused', paused: false })
    vi.advanceTimersByTime(500)
    expect(mediaSent(socket)).toHaveLength(atPause)
    view.unmount()
  })

  it('cancels media repeats outside MediaView when the PC releases held input', async () => {
    conn.connect()
    const socket = lastSocket()
    socket.open()
    socket.receive(welcome)
    const { pressMedia } = await import('../src/lib/input')
    const stop = pressMedia('volumeUp')
    vi.advanceTimersByTime(550)
    expect(mediaSent(socket).length).toBeGreaterThanOrEqual(2)

    socket.receive({ t: 'paused', paused: true })
    const atPause = mediaSent(socket).length
    socket.receive({ t: 'paused', paused: false })
    vi.advanceTimersByTime(500)
    expect(mediaSent(socket)).toHaveLength(atPause)
    stop()
  })
})
