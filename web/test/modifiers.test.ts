import { describe, expect, it } from 'vitest'
import { Key } from '../src/lib/keys'
import { Modifiers } from '../src/lib/modifiers'
import { Mod } from '../src/lib/protocol'

function setup() {
  const events: string[] = []
  let now = 0
  const mods = new Modifiers(
    {
      keyDown: (code) => events.push(`down:${code}`),
      keyUp: (code) => events.push(`up:${code}`),
    },
    () => now)
  const advance = (ms: number) => (now += ms)
  return { mods, events, advance }
}

describe('modifier keys', () => {
  it('tap latches for exactly one key without pressing anything on the PC', () => {
    const { mods, events, advance } = setup()
    mods.press(1, Mod.Command)
    advance(80)
    mods.release(1)
    expect(mods.status(Mod.Command)).toBe('latched')
    expect(events).toEqual([])
    expect(mods.use()).toBe(Mod.Command)
    expect(mods.use()).toBe(0)
    expect(mods.status(Mod.Command)).toBe('off')
  })

  it('forgets fingers lost while the page was hidden without sending key-ups', () => {
    const { mods, events } = setup()
    mods.press(1, Mod.Shift)
    mods.use()
    expect(events).toEqual([`down:${Key.Shift}`])
    mods.dropTouches()
    expect(mods.status(Mod.Shift)).toBe('off')
    mods.use()
    mods.release(1)
    expect(events).toEqual([`down:${Key.Shift}`])
  })

  it('double tap locks by holding the key on the PC; a third tap unlocks', () => {
    const { mods, events, advance } = setup()
    for (let i = 0; i < 2; i++) {
      mods.press(1, Mod.Shift)
      advance(60)
      mods.release(1)
      advance(100)
    }
    expect(mods.status(Mod.Shift)).toBe('locked')
    expect(events).toEqual([`down:${Key.Shift}`])
    expect(mods.use()).toBe(0)
    expect(mods.status(Mod.Shift)).toBe('locked')
    mods.press(1, Mod.Shift)
    advance(60)
    mods.release(1)
    expect(mods.status(Mod.Shift)).toBe('off')
    expect(events).toEqual([`down:${Key.Shift}`, `up:${Key.Shift}`])
  })

  it('holding presses the modifier only when something uses it', () => {
    const { mods, events, advance } = setup()
    mods.press(7, Mod.Command, Key.RightCommand)
    advance(500)
    expect(events).toEqual([])
    expect(mods.use()).toBe(0)
    expect(mods.use()).toBe(0)
    expect(events).toEqual([`down:${Key.RightCommand}`])
    mods.release(7)
    expect(events).toEqual([`down:${Key.RightCommand}`, `up:${Key.RightCommand}`])
    expect(mods.status(Mod.Command)).toBe('off')
  })

  it('a held modifier that was used does not latch on release', () => {
    const { mods, advance } = setup()
    mods.press(1, Mod.Option)
    mods.use()
    advance(50)
    mods.release(1)
    expect(mods.status(Mod.Option)).toBe('off')
  })

  it('a long unused press and release does nothing (no lone fn press)', () => {
    const { mods, events, advance } = setup()
    mods.press(1, Mod.Fn)
    advance(900)
    mods.release(1)
    expect(events).toEqual([])
    expect(mods.status(Mod.Fn)).toBe('off')
  })

  it('locked fn is applied per key instead of being held', () => {
    const { mods, events } = setup()
    mods.toggleLock(Mod.Fn)
    expect(events).toEqual([])
    expect(mods.use()).toBe(Mod.Fn)
    expect(mods.use()).toBe(Mod.Fn)
  })

  it('reset can release everything it pressed', () => {
    const { mods, events } = setup()
    mods.toggleLock(Mod.Control)
    mods.press(2, Mod.Command)
    mods.use()
    mods.reset(true)
    expect(events).toEqual([`down:${Key.Control}`, `down:${Key.Command}`, `up:${Key.Command}`, `up:${Key.Control}`])
  })

  it('two fingers on the same modifier hold one key until the last lifts', () => {
    const { mods, events } = setup()
    mods.press(1, Mod.Shift)
    mods.press(2, Mod.Shift)
    mods.use()
    expect(events).toEqual([`down:${Key.Shift}`])
    mods.release(1)
    expect(events).toEqual([`down:${Key.Shift}`])
    mods.release(2)
    expect(events).toEqual([`down:${Key.Shift}`, `up:${Key.Shift}`])
  })

  it('presses a held modifier again after the PC released everything', () => {
    const { mods, events } = setup()
    mods.press(1, Mod.Command)
    mods.use()
    mods.restoreLocks()
    mods.use()
    mods.release(1)
    expect(events).toEqual([`down:${Key.Command}`, `down:${Key.Command}`, `up:${Key.Command}`])
  })
  it('tells the left and right keys apart', () => {
    const { mods, events, advance } = setup()
    mods.press(1, Mod.RightCommand, Key.RightCommand)
    advance(80)
    mods.release(1)
    expect(mods.status(Mod.RightCommand)).toBe('latched')
    expect(mods.status(Mod.Command)).toBe('off')
    // Labels treat both sides alike.
    expect(mods.active).toBe(Mod.Command)
    expect(mods.use()).toBe(Mod.RightCommand)
    expect(events).toEqual([])
  })

  it('holds and locks the right-side key itself', () => {
    const { mods, events, advance } = setup()
    mods.press(1, Mod.RightShift, Key.RightShift)
    mods.use()
    mods.release(1)
    expect(events).toEqual([`down:${Key.RightShift}`, `up:${Key.RightShift}`])
    events.length = 0
    for (let i = 0; i < 2; i++) {
      mods.press(1, Mod.RightOption, Key.RightOption)
      advance(60)
      mods.release(1)
      advance(100)
    }
    expect(mods.status(Mod.RightOption)).toBe('locked')
    expect(mods.status(Mod.Option)).toBe('off')
    expect(events).toEqual([`down:${Key.RightOption}`])
    mods.reset(true)
    expect(events).toEqual([`down:${Key.RightOption}`, `up:${Key.RightOption}`])
  })})

describe('Windows momentary modifiers', () => {
  it.each([
    [Mod.Command, Key.Command], [Mod.Control, Key.Control],
    [Mod.Option, Key.Option], [Mod.Shift, Key.Shift],
    [Mod.RightCommand, Key.RightCommand], [Mod.RightControl, Key.RightControl],
    [Mod.RightOption, Key.RightOption], [Mod.RightShift, Key.RightShift],
  ])('presses and releases standalone modifier %s without a latch', (mod, code) => {
    const { mods, events, advance } = setup()
    mods.press(1, mod, code, 'momentary')
    expect(events).toEqual([`down:${code}`])
    advance(80)
    mods.release(1)
    expect(events).toEqual([`down:${code}`, `up:${code}`])
    expect(mods.status(mod)).toBe('off')
    expect(mods.use()).toBe(0)
  })

  it('does not lock after repeated taps and holds a combo only while touched', () => {
    const { mods, events, advance } = setup()
    for (let i = 0; i < 2; i++) {
      mods.press(1, Mod.Control, Key.Control, 'momentary')
      advance(60); mods.release(1); advance(100)
    }
    expect(mods.status(Mod.Control)).toBe('off')
    mods.press(2, Mod.Control, Key.Control, 'momentary')
    expect(mods.use()).toBe(0)
    mods.release(2)
    expect(events).toEqual(Array.from({ length: 3 }, () => [`down:${Key.Control}`, `up:${Key.Control}`]).flat())
  })

  it('releases cancelled presses and handles two fingers on one key', () => {
    const { mods, events } = setup()
    mods.press(1, Mod.Shift, Key.Shift, 'momentary')
    mods.press(2, Mod.Shift, Key.Shift, 'momentary')
    mods.cancel(1)
    expect(events).toEqual([`down:${Key.Shift}`])
    mods.cancel(2)
    expect(events).toEqual([`down:${Key.Shift}`, `up:${Key.Shift}`])
    expect(mods.active).toBe(0)
  })

  it('keeps Fn as a client-side media layer without sending a Windows key', () => {
    const { mods, events } = setup()
    mods.press(1, Mod.Fn, Key.Fn, 'momentary')
    mods.release(1)
    expect(mods.active & Mod.Fn).toBe(Mod.Fn)
    expect(events).toEqual([])
  })

  it('does not release a momentary key when its shortcut-strip lock is removed', () => {
    const { mods, events } = setup()
    mods.toggleLock(Mod.Control)
    mods.press(1, Mod.Control, Key.Control, 'momentary')
    mods.toggleLock(Mod.Control)
    expect(events).toEqual([`down:${Key.Control}`])
    mods.release(1)
    expect(events).toEqual([`down:${Key.Control}`, `up:${Key.Control}`])
  })
})
