// Exo Shaders, an unofficial fork based on SCSKiller. Copyright (C) the SCSKiller authors and Exo Shaders contributors. GPL-3.0-or-later.
// One store for what the engine last said: the snapshot, live progress, the status line and Exo's add-on state.
import { useSyncExternalStore } from 'react'
import { call, on, errorText } from './engine'
import type { ExoGame, Notice, Progress, Snapshot } from './types'

export type Status = { id: number; text: string; tone: 'muted' | 'good' | 'bad'; action?: { label: string; run: () => void } }
export type Addon = { state: string; message?: string | null }

type State = {
  snapshot: Snapshot | null
  loading: boolean
  loadError: string | null
  addon: Addon | null
  exiting: string | null
  status: Status | null
  covers: Map<string, string>
}

let state: State = { snapshot: null, loading: true, loadError: null, addon: null, exiting: null, status: null, covers: new Map() }
const subs = new Set<() => void>()

function set(patch: Partial<State>) {
  state = { ...state, ...patch }
  subs.forEach(fn => fn())
}

export function useStore<T>(pick: (s: State) => T): T {
  return useSyncExternalStore(fn => { subs.add(fn); return () => { subs.delete(fn) } }, () => pick(state))
}

export const getState = () => state

// ------------------------------------------------------------------ status line

let statusSeq = 0
let statusTimer: number | undefined

/** One quiet line at the bottom; it fades on its own. */
export function say(text: string, tone: Status['tone'] = 'muted', action?: Status['action']) {
  const id = ++statusSeq
  set({ status: { id, text, tone, action } })
  window.clearTimeout(statusTimer)
  statusTimer = window.setTimeout(() => { if (state.status?.id === id) set({ status: null }) }, action ? 9000 : tone === 'bad' ? 7000 : 4500)
}

/**
 * Runs an engine method; a failure reaches the user as the engine's own sentence on the status line.
 * Returns undefined when it failed.
 */
export async function act<T = { ok: true }>(method: string, params?: Record<string, unknown>, done?: string): Promise<T | undefined> {
  try {
    const result = await call<T>(method, params)
    if (done) say(done)
    return result
  } catch (e) {
    say(errorText(e), 'bad')
    return undefined
  }
}

// ------------------------------------------------------------------ snapshot and events

export async function loadSnapshot() {
  set({ loading: true, loadError: null })
  try {
    const snap = await call<Snapshot>('snapshot')
    if (!state.snapshot || snap.rev >= state.snapshot.rev) set({ snapshot: snap })
    set({ loading: false, addon: state.addon?.state === 'blocked' ? null : state.addon })
  } catch (e) {
    set({ loading: false, loadError: errorText(e) })
  }
}

function applyProgress(p: Progress) {
  const snap = state.snapshot
  if (!snap || p.rev <= snap.rev) return
  const games = p.game
    ? snap.games.map(g => (g.id === p.game!.id ? { ...g, statusTitle: p.game!.statusTitle, statusReason: p.game!.statusReason, percent: p.game!.percent } : g))
    : snap.games
  set({ snapshot: { ...snap, rev: p.rev, queue: p.queue, games } })
}

function noticeText(n: Notice): { text: string; tone: Status['tone'] } {
  switch (n.kind) {
    case 'driverStale': return { text: n.text, tone: 'muted' }
    case 'newShaders': return { text: n.text, tone: 'muted' }
    case 'beforeAfterTimeUp': return { text: `Time's up in ${n.name}: close the game`, tone: 'muted' }
    case 'beforeAfterDone': return { text: `The before and after report of ${n.name} is ready`, tone: 'good' }
    case 'error': return { text: n.text, tone: 'bad' }
    case 'conflict': return { text: n.text, tone: 'bad' }
  }
}

/** Set by the app so a "report ready" notice can open the game. */
export let openGameFromNotice: (id: string) => void = () => {}
export function setOpenGameFromNotice(fn: (id: string) => void) { openGameFromNotice = fn }

let started = false
export function start() {
  if (started) return
  started = true
  on('state', data => {
    const snap = data as Snapshot
    if (!state.snapshot || snap.rev >= state.snapshot.rev) set({ snapshot: snap, loading: false, loadError: null })
  })
  on('progress', data => applyProgress(data as Progress))
  on('notice', data => {
    const n = data as Notice
    const { text, tone } = noticeText(n)
    const action = n.kind === 'beforeAfterDone' ? { label: 'View', run: () => openGameFromNotice(n.id) } : undefined
    say(text, tone, action)
  })
  on('addon', data => {
    const a = data as Addon
    set({ addon: a })
    if (a.state !== 'blocked' && !state.snapshot) void loadSnapshot()
  })
  on('ready', () => { set({ exiting: null }); void loadSnapshot() })
  on('exiting', data => set({ exiting: (data as { reason?: string } | null)?.reason ?? 'shutdown' }))
  void loadSnapshot()
  void loadCovers()
}

// ------------------------------------------------------------------ covers

const norm = (p: string) => p.toLowerCase().replace(/\//g, '\\').replace(/\\+$/, '')
const normTitle = (t: string) => t.toLowerCase().replace(/[™®©’']/g, '').replace(/[^a-z0-9]+/g, ' ').trim()

let exoGames: ExoGame[] = []

async function loadCovers() {
  try {
    exoGames = (await call<ExoGame[]>('exo.games')) ?? []
  } catch {
    exoGames = []
  }
  set({ covers: new Map() })
}

/** Exo's cover for a game: by install folder, else by title. */
export function coverFor(installDir: string, name: string): string | null {
  const cached = state.covers.get(installDir + '|' + name)
  if (cached !== undefined) return cached || null
  const dir = norm(installDir)
  const title = normTitle(name)
  const hit =
    exoGames.find(g => g.cover && g.path && (dir === norm(g.path) || dir.startsWith(norm(g.path) + '\\') || norm(g.path).startsWith(dir + '\\'))) ??
    exoGames.find(g => g.cover && normTitle(g.title) === title)
  const url = hit?.cover && (hit.cover.startsWith('https://covers.exo-launcher.local/') || hit.cover.startsWith('https://thumbs.exo-launcher.local/') || hit.cover.startsWith('data:')) ? hit.cover : ''
  state.covers.set(installDir + '|' + name, url)
  return url || null
}
