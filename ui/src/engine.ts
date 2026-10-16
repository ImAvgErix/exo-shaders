// Exo Shaders, an unofficial fork based on SCSKiller. Copyright (C) the SCSKiller authors and Exo Shaders contributors. GPL-3.0-or-later.
// The page's only line out: messages to Exo, which relays them to the engine service and back.

const EXO_ORIGIN = 'https://app.exo-launcher.local'

type Listener = (data: unknown) => void

export type Transport = {
  send: (id: number, method: string, params?: Record<string, unknown>) => void
}

const pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void }>()
const listeners = new Map<string, Set<Listener>>()
let nextId = 1

/** Handles one {exo:'shaders', ...} message, from Exo or from the dev mock. */
export function receive(m: unknown) {
  if (!m || typeof m !== 'object') return
  const msg = m as { exo?: unknown; id?: unknown; result?: unknown; error?: unknown; event?: unknown; data?: unknown }
  if (msg.exo !== 'shaders') return
  if (typeof msg.event === 'string') {
    listeners.get(msg.event)?.forEach(fn => fn(msg.data ?? null))
    return
  }
  if (typeof msg.id !== 'number') return
  const waiter = pending.get(msg.id)
  if (!waiter) return
  pending.delete(msg.id)
  if ('error' in msg && msg.error != null) waiter.reject(new Error(typeof msg.error === 'string' ? msg.error : 'The shader engine did not answer.'))
  else waiter.resolve(msg.result ?? null)
}

let transport: Transport = {
  send: (id, method, params) => window.parent.postMessage({ exo: 'shaders', id, method, params: params ?? {} }, EXO_ORIGIN),
}

export const embedded = window.parent !== window

if (embedded) {
  window.addEventListener('message', event => {
    if (event.origin !== EXO_ORIGIN || event.source !== window.parent) return
    receive(event.data)
  })
}

/** Dev only: swaps in the mock engine. */
export function setTransport(t: Transport) {
  transport = t
}

export function call<T = { ok: true }>(method: string, params?: Record<string, unknown>): Promise<T> {
  const id = nextId++
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve: v => resolve(v as T), reject })
    try {
      transport.send(id, method, params)
    } catch (e) {
      pending.delete(id)
      reject(e instanceof Error ? e : new Error('The shader engine did not answer.'))
    }
  })
}

export function on(event: string, fn: Listener): () => void {
  let set = listeners.get(event)
  if (!set) listeners.set(event, (set = new Set()))
  set.add(fn)
  return () => { set.delete(fn) }
}

/** Asks Exo to close the Shaders sheet. */
export function closeSheet() {
  if (embedded) window.parent.postMessage({ exo: 'shaders', close: true }, EXO_ORIGIN)
}

export const errorText = (e: unknown) => (e instanceof Error ? e.message : typeof e === 'string' ? e : 'Something went wrong.')
