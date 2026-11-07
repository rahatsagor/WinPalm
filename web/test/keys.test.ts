import { describe, expect, it } from 'vitest'
import { Key, LabelMap, SPECIAL_KEYS, comboLabel, keyName, keyboardRows, modsLabel, plainMods } from '../src/lib/keys'
import { Mod } from '../src/lib/protocol'
import { migrate } from '../src/lib/settings'

describe('layout labels', () => {
  it('labels right-side modifiers like the left ones', () => {
    expect(plainMods(Mod.RightShift | Mod.Command)).toBe(Mod.Shift | Mod.Command)
    expect(new LabelMap().label(Key.Two, Mod.RightShift)).toBe('@')
    expect(modsLabel(Mod.RightCommand | Mod.Shift)).toBe('⇧Win')
    const bottom = keyboardRows(false).at(-1)!
    const right = (code: number) => bottom.find((k) => k.kind === 'mod' && k.code === code)
    expect(right(Key.RightOption)).toMatchObject({ mod: Mod.RightOption, symbol: 'Alt' })
    expect(right(Key.RightControl)).toMatchObject({ mod: Mod.RightControl, symbol: 'Ctrl' })
  })

  it('defaults to US and follows the PC layout when known', () => {
    const us = new LabelMap()
    expect(us.label(Key.Q, 0)).toBe('Q')
    expect(us.label(Key.Two, Mod.Shift)).toBe('@')
    const azerty = new LabelMap({
      id: 'fr',
      name: 'French',
      iso: true,
      keys: [
        { code: Key.Q, base: 'a', shift: 'A' },
        { code: Key.A, base: 'q', shift: 'Q', option: '‡' },
        { code: Key.E, base: 'e', shift: 'E', option: '´' },
      ],
    })
    expect(azerty.label(Key.Q, 0)).toBe('A')
    expect(azerty.forChar('q')).toEqual({ code: Key.A, mods: 0 })
    expect(azerty.forChar('Q')).toEqual({ code: Key.A, mods: Mod.Shift })
    expect(azerty.forChar('‡')).toEqual({ code: Key.A, mods: Mod.Option | Mod.Control })
  })

  it('keeps PC ISO scan positions and AltGr labels distinct from Alt shortcuts', () => {
    const rows = keyboardRows(true)
    expect(rows[1]?.[0]).toMatchObject({ code: Key.Grave })
    expect(rows[4]?.[1]).toMatchObject({ code: Key.ISOSection })
    const labels = new LabelMap({ id: 'de', name: 'German', iso: true, keys: [{ code: Key.Q, base: 'q', shift: 'Q', option: '@' }] })
    expect(labels.label(Key.Q, Mod.Option)).toBe('Q')
    expect(labels.label(Key.Q, Mod.Option | Mod.Control)).toBe('@')
  })

  it('formats combos in PC modifier order', () => {
    expect(comboLabel(Key.Four, Mod.Command | Mod.Shift, new LabelMap())).toBe('⇧Win4')
    expect(comboLabel(Key.Left, Mod.Control, new LabelMap())).toBe('Ctrl←')
    expect(comboLabel(Key.A, Mod.Option | Mod.Control, new LabelMap())).toBe('CtrlAltA')
  })

  it('uses key symbols that phones draw as text, not emoji', () => {
    const labels = new LabelMap()
    const symbols = [
      ...[false, true].flatMap((iso) => keyboardRows(iso).flat()).flatMap((spec) => ('symbol' in spec ? [spec.symbol] : [])),
      ...[...SPECIAL_KEYS.map((k) => k.code), Key.CapsLock].map((code) => keyName(code, labels)),
    ]
    for (const symbol of symbols) expect(symbol, symbol).not.toMatch(/\p{Extended_Pictographic}/u)
  })
})

describe('settings', () => {
  it('drops malformed values and clamps ranges', () => {
    const s = migrate({ pointerSpeed: 99, naturalScroll: 'yes', threeFinger: { up: null, sideways: 1 }, bogus: true })
    expect(s.pointerSpeed).toBe(4)
    expect(s.naturalScroll).toBe(true)
    expect(s.threeFinger.up).toBeNull()
    expect(s.threeFinger.down).toEqual({ kind: 'system', name: 'showDesktop' })
    expect('bogus' in s).toBe(false)
  })

})
