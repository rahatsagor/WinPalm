import { existsSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { runInNewContext } from 'node:vm'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const ORIGIN = 'https://pc.local:8790'
const swPath = existsSync(resolve(process.cwd(), 'public/sw.js'))
  ? resolve(process.cwd(), 'public/sw.js')
  : resolve(process.cwd(), 'web/public/sw.js')
const SW_SOURCE = readFileSync(swPath, 'utf8')

interface FakeRequest {
  url: string
  method: string
  mode: string
}

class MemoryCache {
  readonly entries = new Map<string, Response>()

  private key(input: string | FakeRequest) {
    const raw = typeof input === 'string' ? input : input.url
    return new URL(raw, ORIGIN).href
  }

  async add(url: string | FakeRequest) {
    const res = await this.fetcher(url)
    if (!res.ok) throw new Error(`add failed for ${typeof url === 'string' ? url : url.url}`)
    await this.put(url, res)
  }

  async addAll(urls: string[]) {
    for (const url of urls) await this.add(url)
  }

  constructor(private readonly fetcher: (input: string | FakeRequest) => Promise<Response>) {}

  async match(input: string | FakeRequest) {
    const found = this.entries.get(this.key(input))
    return found ? found.clone() : undefined
  }

  async put(input: string | FakeRequest, response: Response) {
    this.entries.set(this.key(input), response.clone())
  }

  async delete(input: string | FakeRequest) {
    return this.entries.delete(this.key(input))
  }

  async keys() {
    return Array.from(this.entries.keys(), (url) => ({ url }))
  }
}

function createHarness(fetcher: (input: string | FakeRequest) => Promise<Response>) {
  const listeners = new Map<string, (event: unknown) => void>()
  const cache = new MemoryCache(fetcher)
  const caches = {
    open: async () => cache,
    match: (input: string | FakeRequest) => cache.match(input),
  }

  const sandbox = {
    URL,
    Response,
    Headers,
    Set,
    Promise,
    Symbol,
    setTimeout,
    clearTimeout,
    location: { origin: ORIGIN },
    caches,
    fetch: fetcher,
    self: {} as Record<string, unknown>,
  }
  sandbox.self = {
    addEventListener: (type: string, listener: (event: unknown) => void) => {
      listeners.set(type, listener)
    },
    skipWaiting: () => Promise.resolve(),
    clients: { claim: () => Promise.resolve() },
  }

  runInNewContext(SW_SOURCE, sandbox)

  async function install() {
    const waits: Promise<unknown>[] = []
    listeners.get('install')?.({ waitUntil: (p: Promise<unknown>) => waits.push(p) })
    await Promise.all(waits)
  }

  async function dispatchFetch(path: string, mode: string) {
    const request: FakeRequest = { url: new URL(path, ORIGIN).href, method: 'GET', mode }
    let handled: Promise<Response> | undefined
    const waits: Promise<unknown>[] = []
    listeners.get('fetch')?.({
      request,
      respondWith: (p: Promise<Response>) => { handled = p },
      waitUntil: (p: Promise<unknown>) => waits.push(p),
    })
    return {
      intercepted: handled !== undefined,
      response: handled ? await handled : undefined,
      flush: () => Promise.all(waits),
    }
  }

  return { cache, install, dispatchFetch }
}

describe('service worker downloads and cache pruning', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('bypasses a slow /source.zip request without timing out to the app shell or caching it', async () => {
    const shellHtml = '<!doctype html><script type="module" src="/assets/app-1.js"></script>'
    const fetchSpy = vi.fn(async (input: string | FakeRequest) => {
      const path = new URL(typeof input === 'string' ? input : input.url, ORIGIN).pathname
      if (path === '/') return new Response(shellHtml, { status: 200, headers: { 'Content-Type': 'text/html' } })
      if (path === '/offline.html') return new Response('offline', { status: 200 })
      if (path === '/icon.svg') return new Response('<svg/>', { status: 200 })
      if (path === '/assets/app-1.js') return new Response('console.log(1)', { status: 200 })
      if (path === '/source.zip') {
        return await new Promise<Response>((resolve) => {
          setTimeout(() => resolve(new Response('PK-source-archive', { status: 200 })), 5000)
        })
      }
      return new Response('missing', { status: 404 })
    })

    const sw = createHarness(fetchSpy)
    await sw.install()
    expect(sw.cache.entries.has(`${ORIGIN}/assets/app-1.js`)).toBe(true)

    const result = await sw.dispatchFetch('/source.zip', 'navigate')
    expect(result.intercepted).toBe(false)
    await result.flush()

    const directPromise = fetchSpy(`${ORIGIN}/source.zip`)
    await vi.advanceTimersByTimeAsync(5000)
    const direct = await directPromise
    expect(await direct.text()).toBe('PK-source-archive')
    expect(sw.cache.entries.has(`${ORIGIN}/source.zip`)).toBe(false)
    expect(sw.cache.entries.has(`${ORIGIN}/assets/app-1.js`)).toBe(true)
  })

  it('bypasses /LICENSE.txt even when it changes and preserves cached assets', async () => {
    let licenseText = 'AGPL-3.0-only v1'
    const shellHtml = '<!doctype html><script type="module" src="/assets/app-1.js"></script>'
    const fetchSpy = vi.fn(async (input: string | FakeRequest) => {
      const path = new URL(typeof input === 'string' ? input : input.url, ORIGIN).pathname
      if (path === '/') return new Response(shellHtml, { status: 200 })
      if (path === '/offline.html') return new Response('offline', { status: 200 })
      if (path === '/icon.svg') return new Response('<svg/>', { status: 200 })
      if (path === '/assets/app-1.js') return new Response('console.log(1)', { status: 200 })
      if (path === '/LICENSE.txt') return new Response(licenseText, { status: 200 })
      return new Response('missing', { status: 404 })
    })

    const sw = createHarness(fetchSpy)
    await sw.install()
    await sw.cache.put('/assets/chunk-1.js', new Response('chunk1', { status: 200 }))

    const first = await sw.dispatchFetch('/LICENSE.txt', 'navigate')
    expect(first.intercepted).toBe(false)
    licenseText = 'AGPL-3.0-only v2 (updated)'
    const second = await sw.dispatchFetch('/LICENSE.txt', 'navigate')
    expect(second.intercepted).toBe(false)
    expect(await (await fetchSpy(`${ORIGIN}/LICENSE.txt`)).text()).toBe('AGPL-3.0-only v2 (updated)')

    expect(sw.cache.entries.has(`${ORIGIN}/LICENSE.txt`)).toBe(false)
    expect(sw.cache.entries.has(`${ORIGIN}/assets/app-1.js`)).toBe(true)
    expect(sw.cache.entries.has(`${ORIGIN}/assets/chunk-1.js`)).toBe(true)
  })

  it('preserves cached /assets/* on arbitrary navigations and prunes obsolete assets only on shell updates', async () => {
    let shellHtml = '<!doctype html><script type="module" src="/assets/app-1.js"></script>'
    let customPage = '<!doctype html><p>Page A</p>'
    const sw = createHarness(async (input) => {
      const path = new URL(typeof input === 'string' ? input : input.url, ORIGIN).pathname
      if (path === '/' || path === '/index.html') return new Response(shellHtml, { status: 200 })
      if (path === '/offline.html') return new Response('offline', { status: 200 })
      if (path === '/icon.svg') return new Response('<svg/>', { status: 200 })
      if (path === '/assets/app-1.js') return new Response('console.log(1)', { status: 200 })
      if (path === '/custom') return new Response(customPage, { status: 200 })
      return new Response('missing', { status: 404 })
    })

    await sw.install()
    await sw.cache.put('/assets/chunk-1.js', new Response('chunk1', { status: 200 }))

    // Arbitrary navigations changing content must not prune /assets/*.
    const nav1 = await sw.dispatchFetch('/custom', 'navigate')
    expect(nav1.intercepted).toBe(true)
    await nav1.flush()
    customPage = '<!doctype html><p>Page B</p>'
    const nav2 = await sw.dispatchFetch('/custom', 'navigate')
    expect(nav2.intercepted).toBe(true)
    await nav2.flush()
    expect(sw.cache.entries.has(`${ORIGIN}/assets/app-1.js`)).toBe(true)
    expect(sw.cache.entries.has(`${ORIGIN}/assets/chunk-1.js`)).toBe(true)

    // Updating the HTML app shell at / prunes obsolete assets while keeping referenced ones.
    shellHtml = '<!doctype html><script type="module" src="/assets/app-1.js"></script><script type="module" src="/assets/app-2.js"></script>'
    const shellUpdate = await sw.dispatchFetch('/', 'navigate')
    expect(shellUpdate.intercepted).toBe(true)
    await shellUpdate.flush()
    expect(sw.cache.entries.has(`${ORIGIN}/assets/app-1.js`)).toBe(true)
    expect(sw.cache.entries.has(`${ORIGIN}/assets/chunk-1.js`)).toBe(false)

    // Updating via /index.html also prunes obsolete assets.
    shellHtml = '<!doctype html><script type="module" src="/assets/app-3.js"></script>'
    const indexUpdate = await sw.dispatchFetch('/index.html', 'navigate')
    expect(indexUpdate.intercepted).toBe(true)
    await indexUpdate.flush()
    expect(sw.cache.entries.has(`${ORIGIN}/assets/app-1.js`)).toBe(false)
  })
})
