import { mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import { Op, ScrollPhase } from '../src/lib/protocol'
import { FakeSocket, lastSocket, welcome } from './fakeSocket'

let conn: typeof import('../src/lib/connection')

beforeEach(async () => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date', 'performance'] })
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
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

function connected() {
  conn.connect()
  lastSocket().open()
  lastSocket().receive(welcome)
  return lastSocket()
}

function scrolls(socket: FakeSocket) {
  return socket.binary
    .map((buf) => new DataView(buf))
    .filter((view) => view.getUint8(0) === Op.Scroll)
    .map((view) => ({ phase: view.getUint8(1), dy: view.getFloat32(6, true) }))
}

/** Lets batched pointer messages go out. */
const settle = (ms = 20) => vi.advanceTimersByTimeAsync(ms)

// The track is 400px and the handle 112px, so the handle travels 144px each way.
async function rail() {
  const { default: ScrollRail } = await import('../src/components/ScrollRail.vue')
  const view = mount(ScrollRail, { attachTo: document.body })
  const [track, handle] = [view.element as HTMLElement, view.find('[aria-label="Scroll strip"]').element as HTMLElement]
  Object.defineProperty(track, 'clientHeight', { value: 400 })
  Object.defineProperty(handle, 'clientHeight', { value: 112 })
  handle.setPointerCapture = () => {}
  const fire = (type: string, clientY: number) => handle.dispatchEvent(new PointerEvent(type, { pointerId: 1, clientY, bubbles: true }))
  return { view, fire }
}

describe('scroll strip', () => {
  it.each([
    ['short', 'normal', 36], ['short', 'wide', 52], ['short', 'wider', 68],
    ['long', 'normal', 36], ['long', 'wide', 52], ['long', 'wider', 68],
  ] as const)('keeps both chevrons and placement for %s at %s width', async (mode, width, pixels) => {
    const { settings } = await import('../src/lib/settings')
    settings.scrollStrip = mode
    settings.scrollStripWidth = width
    settings.scrollStripEdge = true
    settings.scrollStripSide = 'left'

    const { default: ScrollRail } = await import('../src/components/ScrollRail.vue')
    const view = mount(ScrollRail, { attachTo: document.body })
    const placement = view.element as HTMLElement
    const chevrons = view.findAll('[aria-label="Scroll strip"] svg')

    expect(placement.style.width).toBe(`${pixels}px`)
    expect(placement.style.left).toBe('0px')
    expect(chevrons).toHaveLength(2)
    expect(chevrons.every(icon => icon.classes().includes('shrink-0'))).toBe(true)
    if (mode === 'short') expect(view.find('[aria-label="Scroll strip"]').classes()).toContain('h-28')
    else expect(placement.className).toContain('bg-text/[0.05]')

    view.unmount()
  })

  it('scrolls with the handle, and keeps scrolling while it is held at an end', async () => {
    const socket = connected()
    const { view, fire } = await rail()
    fire('pointerdown', 300)
    fire('pointermove', 340)
    await settle()
    expect(scrolls(socket)).toEqual([{ phase: ScrollPhase.Began, dy: 64 }])

    // Short of the end, nothing happens while the finger rests.
    await settle(300)
    expect(scrolls(socket)).toHaveLength(1)

    fire('pointermove', 300 + 144 + 20)
    await settle()
    const before = scrolls(socket).length
    await settle(500)
    const held = scrolls(socket).slice(before)
    expect(held.length).toBeGreaterThan(5)
    expect(held.every((s) => s.phase === ScrollPhase.Changed && s.dy > 0)).toBe(true)
    // About (360 + 20 * 14) px/s for half a second, at 1.6× scroll scale.
    expect(held.reduce((sum, s) => sum + s.dy, 0)).toBeCloseTo(512, -2)

    fire('pointerup', 464)
    await settle()
    const done = scrolls(socket).length
    expect(scrolls(socket).at(-1)!.phase).toBe(ScrollPhase.Ended)
    await settle(500)
    expect(scrolls(socket)).toHaveLength(done)
    view.unmount()
  })

  it('scrolls the other way at the top', async () => {
    const socket = connected()
    const { view, fire } = await rail()
    fire('pointerdown', 300)
    fire('pointermove', 300 - 160)
    await settle()
    const before = scrolls(socket).length
    await settle(200)
    expect(scrolls(socket).slice(before).every((s) => s.dy < 0)).toBe(true)
    fire('pointerup', 140)
    view.unmount()
  })

  it('stops when the page hides', async () => {
    const socket = connected()
    const { view, fire } = await rail()
    fire('pointerdown', 300)
    fire('pointermove', 480)
    await settle(100)
    vi.spyOn(document, 'hidden', 'get').mockReturnValue(true)
    document.dispatchEvent(new Event('visibilitychange'))
    await settle()
    const stopped = scrolls(socket).length
    expect(scrolls(socket).at(-1)!.phase).toBe(ScrollPhase.Cancelled)
    await settle(500)
    expect(scrolls(socket)).toHaveLength(stopped)
    view.unmount()
  })

  it('stops when the connection drops', async () => {
    const first = connected()
    const { view, fire } = await rail()
    fire('pointerdown', 300)
    fire('pointermove', 480)
    await settle(100)
    first.drop()
    await nextTick()
    await settle(1000)
    const second = lastSocket()
    second.open()
    second.receive(welcome)
    await settle(500)
    expect(scrolls(second)).toEqual([])
    view.unmount()
  })

  it('abandons end-hold on pause and does not resume an old gesture', async () => {
    const socket = connected()
    const { view, fire } = await rail()
    fire('pointerdown', 300)
    fire('pointermove', 480)
    await settle(100)
    expect(scrolls(socket).length).toBeGreaterThan(1)

    socket.receive({ t: 'paused', paused: true })
    await nextTick()
    await settle()
    const stopped = scrolls(socket).length
    await settle(500)
    expect(scrolls(socket)).toHaveLength(stopped)

    socket.receive({ t: 'paused', paused: false })
    await nextTick()
    fire('pointermove', 500)
    await settle(500)
    expect(scrolls(socket)).toHaveLength(stopped)
    view.unmount()
  })

  it('clears trackpad drag lock when the PC pauses control', async () => {
    const { settings } = await import('../src/lib/settings')
    settings.tapAndDrag = true
    settings.dragLock = true
    connected()
    const { default: TrackpadSurface } = await import('../src/components/TrackpadSurface.vue')
    const view = mount(TrackpadSurface, { attachTo: document.body })
    const surface = view.find('[role="application"]').element as HTMLElement
    surface.setPointerCapture = () => {}
    const touch = (type: string, x: number, timeStamp: number) => {
      const event = new PointerEvent(type, { pointerId: 2, pointerType: 'touch', clientX: x, clientY: 50, bubbles: true })
      Object.defineProperty(event, 'timeStamp', { value: timeStamp })
      surface.dispatchEvent(event)
    }
    touch('pointerdown', 50, 10)
    touch('pointerup', 50, 30)
    touch('pointerdown', 50, 120)
    touch('pointermove', 80, 150)
    touch('pointerup', 80, 180)
    await nextTick()
    expect(surface.className).toContain('ring-2')

    lastSocket().receive({ t: 'paused', paused: true })
    await nextTick()
    expect(surface.className).not.toContain('ring-2')
    view.unmount()
  })

  it('keeps the long strip to scrolling with the finger', async () => {
    const { settings } = await import('../src/lib/settings')
    settings.scrollStrip = 'long'
    const socket = connected()
    const { default: ScrollRail } = await import('../src/components/ScrollRail.vue')
    const view = mount(ScrollRail, { attachTo: document.body })
    const strip = view.element as HTMLElement
    expect(strip.getAttribute('aria-label')).toBe('Scroll strip')
    expect(view.findAll('[aria-label="Scroll strip"]')).toHaveLength(1)
    strip.setPointerCapture = () => {}
    const fire = (type: string, clientY: number) => strip.dispatchEvent(new PointerEvent(type, { pointerId: 1, clientY, bubbles: true }))
    fire('pointerdown', 300)
    fire('pointermove', 900)
    await settle(500)
    expect(scrolls(socket)).toEqual([{ phase: ScrollPhase.Began, dy: 960 }])
    fire('pointerup', 900)
    await settle()
    expect(scrolls(socket).at(-1)!.phase).toBe(ScrollPhase.Ended)
    view.unmount()
  })

  it('only follows the finger when holding is turned off', async () => {
    const { settings } = await import('../src/lib/settings')
    settings.scrollHold = false
    const socket = connected()
    const { view, fire } = await rail()
    fire('pointerdown', 300)
    fire('pointermove', 480)
    await settle()
    await settle(500)
    expect(scrolls(socket)).toEqual([{ phase: ScrollPhase.Began, dy: 288 }])
    fire('pointerup', 480)
    view.unmount()
  })
})
