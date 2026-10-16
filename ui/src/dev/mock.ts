// Exo Shaders, an unofficial fork based on SCSKiller. Copyright (C) the SCSKiller authors and Exo Shaders contributors. GPL-3.0-or-later.
// Dev only (the page opened on its own, not in Exo's frame): a stand-in engine built from real --safe service output
// (scripts/capture-mock.mjs), with a running compile, a short queue, compiled games and a last play added so every state shows.
import snapshotJson from './mock/snapshot.json'
import detailsJson from './mock/details.json'
import inventoryJson from './mock/inventory.json'
import whatsNewJson from './mock/whatsnew.json'
import cachesJson from './mock/caches.json'
import { receive, setTransport } from '../engine'
import type { BeforeAfterReport, Game, GameDetail, Hitch, QueueItem, Snapshot } from '../types'

const clone = <T,>(v: T): T => JSON.parse(JSON.stringify(v)) as T

const snap = clone(snapshotJson) as unknown as Snapshot
const details = clone(detailsJson) as unknown as Record<string, GameDetail>

const byId = (id: string) => snap.games.find(g => g.id === id)

function peaks(seconds: number, stutters: number, seed: number) {
  let s = seed
  const rnd = () => ((s = (s * 16807) % 2147483647) / 2147483647)
  const n = seconds * 2
  const out: number[] = []
  const hitches: Hitch[] = []
  for (let i = 0; i < n; i++) out.push(7 + rnd() * 4 + (i < 40 ? rnd() * 30 : 0))
  for (let k = 0; k < stutters; k++) {
    const i = 40 + Math.floor(rnd() * (n - 41))
    const ms = 35 + rnd() * 120
    out[i] = ms
    hitches.push({ atMs: (i / n) * seconds * 1000, ms, cause: rnd() < 0.75 ? 'shader' : 'other' })
  }
  return { durationMs: seconds * 1000, startupMs: 20000, peaks: out, hitches }
}

// ------------------------------------------------------------------ make the captured library show every state

function warm(id: string, cacheText: string, bytes: number) {
  const g = byId(id)
  if (!g) return
  Object.assign(g, {
    status: 'warmed', statusText: 'Warmed', tone: 'accent', statusTitle: 'Warmed', statusReason: 'Compiled for driver 617.42',
    compiledAt: '2026-10-14T21:12:00-05:00', compiledDriver: '617.42', cacheBytes: bytes, cacheText, cacheEstimated: false, inAddAllReady: false,
    rowNote: 'Compiled for driver 617.42',
  } satisfies Partial<Game>)
  g.actions.compileText = 'Compile again'
  g.actions.add = false
  if (details[id]) Object.assign(details[id], g)
}

warm('steam:1593500', '3.4 GB', 3.4e9)
warm('steam:1110910', '1.1 GB', 1.1e9)

const played = byId('steam:1593500')
if (played && details[played.id]) {
  const f = peaks(600, 9, 42)
  played.frames = { verdict: 'Smooth after the compile', summary: '9 short stutters in 10 minutes, none over 160 ms', stuttersLabel: 'Shader stutters', stutters: '9' }
  Object.assign(details[played.id], {
    frames: { ...f, verdict: played.frames.verdict, summary: played.frames.summary, low1PctFps: 71, avgFps: 118 },
    framesSummary: played.frames,
    hitchRows: [{ label: 'Shader stutters', value: '9' }, { label: 'Worst stutter', value: '154 ms' }, { label: 'Other hitches', value: '2' }],
    earlierSessions: [{ label: 'Oct 12, 41 min', value: '23 stutters' }, { label: 'Oct 9, 1 h 12 min', value: '61 stutters' }],
  })
  played.beforeAfter = { ...played.beforeAfter, shown: true, enabled: true, stage: 'done', text: 'View before and after', note: '', chip: 'Before and after', hasMenu: true }
  details[played.id].beforeAfter = played.beforeAfter
}

const risky = byId('steam:3513350')
if (risky) {
  risky.recorder.offline = { risk: true, on: true, running: false, canStart: true, buttonText: 'Record a session', riskText: 'Anti-cheat may treat the recorder as tampering. A ban is possible.' }
  risky.recorder.canToggle = true
  if (details[risky.id]) details[risky.id].recorder = risky.recorder
}

snap.hidden.push({ id: 'steam:000000', name: 'Old benchmark', store: 'Steam' })

// Captured before the settings policy: what the service now adds to every snapshot.
snap.policy ??= {
  on: true, summary: 'Balanced settings sized to this PC. Pauses while you play or run on battery.', threads: 9, processorCount: 12, memoryGB: 4, ramGB: 15,
  lines: [
    'Compiles with 9 of 12 CPU threads at below-normal priority, and up to 2 GB of memory', 'Pauses while you play',
    'Games with both DirectX 11 and 12 are compiled for both', 'Ray tracing shaders are included', 'Rebuilds after a driver update when the PC is idle',
    'Tells you when a game update brings new shaders', 'Records DirectX 12 games while you play, never anti-cheat games',
  ],
}
if (snap.app.cache) snap.app.cache.largest ??= snap.app.cache.limitCurrent === 'Unlimited'
snap.app.launchers ??= [...new Set(snap.games.map(g => g.store))].map(name => ({ name, games: snap.games.filter(g => g.store === name).length }))
snap.app.version ??= '0.1.0'
snap.app.engineVersion ??= '1.3.0-beta.1'
for (const g of snap.games) delete (g as Partial<Game> & { options?: unknown }).options

const queueGames = ['steam:1620730', 'steam:2001760', 'steam:2215430', 'steam:3489700']
let percent = 41.2
function item(id: string, n: number, stage: QueueItem['stage']): QueueItem {
  const g = byId(id)!
  return { id, name: g.name, number: n, stage, stageText: stage === 'warming' ? 'Compiling' : 'Waiting', time: g.compileTime ?? '—', note: '', cache: g.cacheText, whenIdle: false }
}
function current(): QueueItem {
  const total = 286_140
  const done = Math.round((total * percent) / 100)
  const left = Math.round(((100 - percent) / 100) * 52 * 60)
  return {
    ...item(queueGames[0], 1, snap.queue.paused ? 'paused' : 'warming'),
    stageText: snap.queue.paused ? 'Paused' : 'Compiling',
    percent, done, total, perSecond: 122, failed: 3,
    progressText: `${percent.toFixed(1)}% · ${done.toLocaleString('en-US')} of ${total.toLocaleString('en-US')} pipelines · 122/s · 3 failed`,
    time: `${Math.round(left / 60)} min left`, timeLeftSeconds: left,
  }
}
snap.queue = {
  ...snap.queue, running: true, compiling: true, paused: false, canStart: false, canClear: true, pauseText: 'Pause',
  summary: '4 games · about 3 h 10 min in total',
  current: current(),
  waiting: queueGames.slice(1).map((id, i) => item(id, i + 2, 'waiting')),
  finished: [{ ...item('steam:730', 5, 'failed'), stageText: 'Failed', note: 'Failed: the game’s files changed while reading them', time: '4 min' }],
}
function syncQueueFlags() {
  const ids = [snap.queue.current?.id, ...snap.queue.waiting.map(w => w.id)].filter(Boolean) as string[]
  for (const g of snap.games) {
    const pos = ids.indexOf(g.id)
    g.queued = pos >= 0
    g.compiling = pos === 0 && !!snap.queue.current && g.id === snap.queue.current.id
    g.queuePosition = pos >= 0 ? pos + 1 : undefined
    if (g.compiling) { g.statusTitle = snap.queue.paused ? 'Paused' : 'Compiling'; g.statusReason = snap.queue.current!.progressText ?? ''; g.percent = percent }
    else if (pos >= 0) { g.statusTitle = `In queue (position ${pos + 1})`; g.statusReason = 'Waits for the games before it' }
    else if (g.statusTitle.startsWith('In queue') || g.statusTitle === 'Compiling' || g.statusTitle === 'Paused') { g.statusTitle = g.statusText; g.statusReason = 'Not compiled yet'; g.percent = undefined }
  }
  snap.app.waitingCount = snap.queue.waiting.length
  snap.app.status = snap.queue.current ? (snap.queue.paused ? 'paused' : 'compiling') : 'idle'
  snap.app.statusLine = snap.queue.current ? `${snap.queue.paused ? 'Paused' : 'Compiling'} ${snap.queue.current.name}, ${Math.round(percent)}%` : 'Idle'
}
syncQueueFlags()

// ------------------------------------------------------------------ transport

const post = (m: object) => window.setTimeout(() => receive({ exo: 'shaders', ...m }), 30)
const emitState = () => { snap.rev++; syncQueueFlags(); post({ event: 'state', data: clone(snap) }) }

function report(): BeforeAfterReport {
  const b = peaks(300, 31, 7)
  const a = peaks(300, 4, 9)
  return {
    before: b, after: a,
    compare: {
      before: { playSeconds: 300, shaderStutters: 31, frozenSeconds: 3.8, worstStutterMs: 412, playCompiles: 1840, low1PctFps: 38, avgFps: 109, otherHitches: 3 },
      after: { playSeconds: 300, shaderStutters: 4, frozenSeconds: 0.3, worstStutterMs: 96, playCompiles: 61, low1PctFps: 74, avgFps: 116, otherHitches: 2 },
      perTenMinutes: false, alreadySmooth: false, notes: ['Same save, same graphics settings, driver 617.42.'],
    },
  }
}

function handle(method: string, p: Record<string, unknown>): unknown {
  const id = p.id as string | undefined
  switch (method) {
    case 'snapshot': return clone(snap)
    case 'game.detail': {
      const g = byId(id!)
      if (!g) throw new Error(`no game ${id}`)
      return { ...(details[id!] ?? {}), ...clone(g), frames: details[id!]?.frames, framesSummary: g.frames, coverage: details[id!]?.coverage ?? { value: '—', label: '', headline: 'No plan yet', low: false, good: false }, sources: details[id!]?.sources ?? [], details: details[id!]?.details ?? [], compile: details[id!]?.compile ?? { title: 'Compile', text: '' }, hitchRows: details[id!]?.hitchRows ?? [], earlierSessions: details[id!]?.earlierSessions ?? [] }
    }
    case 'cache.inventory': return new Promise(r => setTimeout(() => r(clone(inventoryJson)), 900))
    case 'settings.get': return clone(snap.settings)
    case 'whatsNew': return p.all ? clone(whatsNewJson) : { ...whatsNewJson, sections: whatsNewJson.sections.slice(0, 1) }
    case 'whatsNew.atStart': return {}
    case 'game.caches': return (cachesJson as Record<string, unknown>)[id!] ?? { parts: [], title: 'Clear the shader cache?', text: '', note: '' }
    case 'exo.games': return []
    case 'ui.visible': return { ok: true }
    case 'scan': return new Promise(r => { snap.app.scanning = true; snap.app.scanProgress = 'Checking each game… 3 of 27'; emitState(); setTimeout(() => { snap.app.scanning = false; snap.app.scanProgress = undefined; emitState(); r({ games: snap.games.length }) }, 1800) })
    case 'library.setSort': {
      snap.app.librarySort = p.sort as Snapshot['app']['librarySort']
      const s = snap.app.librarySort
      const by = (f: (g: Game) => number) => (a: Game, b: Game) => f(a) - f(b)
      if (s === 'nameAscending') snap.games.sort((a, b) => a.name.localeCompare(b.name))
      if (s === 'nameDescending') snap.games.sort((a, b) => b.name.localeCompare(a.name))
      if (s === 'pipelinesDescending') snap.games.sort(by(g => -(g.pipelines ?? 0)))
      if (s === 'pipelinesAscending') snap.games.sort(by(g => g.pipelines ?? 0))
      if (s === 'cacheDescending') snap.games.sort(by(g => -(g.cacheBytes ?? 0)))
      if (s === 'cacheAscending') snap.games.sort(by(g => g.cacheBytes ?? 0))
      emitState(); return { ok: true }
    }
    case 'ba.report': return { report: report() }
    case 'ba.blocked': return {}
    case 'games.previewExe': {
      const exe = String(p.exe)
      const dir = exe.replace(/\\[^\\]*$/, '')
      const name = dir.split('\\').pop() || 'Game'
      return { game: { id: 'manual:' + name.toLowerCase(), name, exe, installDir: dir, store: 'Added by you' }, existed: false, hidden: false, listed: false, title: 'Add ' + name, text: '' }
    }
    case 'games.checkFolder': return String(p.folder).split('\\').filter(Boolean).length < 2 ? { problem: 'This folder holds other games: pick the game’s own folder.' } : {}
    case 'queue.pause': snap.queue.paused = true; snap.queue.pauseText = 'Resume'; emitState(); return { ok: true }
    case 'queue.resume': snap.queue.paused = false; snap.queue.pauseText = 'Pause'; emitState(); return { ok: true }
    case 'queue.stop': throw new Error('not available in safe mode')
    case 'queue.move': {
      const w = snap.queue.waiting
      const i = w.findIndex(x => x.id === id)
      if (i >= 0) { const [x] = w.splice(i, 1); w.splice(Math.max(0, Math.min(w.length, p.index as number)), 0, x); w.forEach((x, k) => (x.number = k + 2)) }
      emitState(); return { ok: true }
    }
    case 'queue.remove':
      if (snap.queue.current?.id === id) snap.queue.current = undefined
      snap.queue.waiting = snap.queue.waiting.filter(w => w.id !== id)
      snap.queue.finished = snap.queue.finished.filter(w => w.id !== id)
      emitState(); return { ok: true }
    case 'queue.removeMany': {
      const ids = p.ids as string[] | undefined
      snap.queue.waiting = ids ? snap.queue.waiting.filter(w => !ids.includes(w.id)) : []
      if (!ids) snap.queue.finished = []
      emitState(); return { ok: true }
    }
    case 'queue.clearFinished': snap.queue.finished = []; emitState(); return { ok: true }
    case 'app.dismissGpuNotice': snap.app.gpu.notice = undefined; emitState(); return { ok: true }
    case 'settings.set': case 'game.setApi': case 'game.setRayTracing': case 'game.setNewShaders': case 'game.setLeftOutOfAddAll': case 'game.setMaximum': case 'game.setCareful':
    case 'game.compileMax': case 'queue.addWhenIdle': case 'driverStale.whenIdle': case 'library.addAllRecommended': case 'recorder.set': case 'recorder.install':
    case 'recorder.uninstall': case 'recorder.alongside': case 'recorder.offline': case 'recorder.startOffline': case 'recorder.checkRisk': case 'recorder.startRisk': case 'driver.setAutoShader':
      throw new Error('Exo Shaders sets this itself: there is no option for it')
    case 'game.compile': case 'queue.add': case 'library.addAllReady': case 'recorder.clear': case 'game.clearCache':
    case 'cache.delete': case 'cache.freeOldDriver': case 'driver.setCacheLimit':
      throw new Error('not available in safe mode')
    default:
      return { ok: true }
  }
}

export function installMock() {
  setTransport({
    send: (rid, method, params) => {
      Promise.resolve()
        .then(() => handle(method, params ?? {}))
        .then(result => post({ id: rid, result }), (e: unknown) => post({ id: rid, error: e instanceof Error ? e.message : String(e) }))
    },
  })
  // A running compile moves along like the engine's progress events.
  window.setInterval(() => {
    if (!snap.queue.current || snap.queue.paused) return
    percent = Math.min(99.9, percent + 0.3)
    snap.queue.current = current()
    snap.rev++
    const g = byId(snap.queue.current.id)!
    post({ event: 'progress', data: { rev: snap.rev, queue: clone(snap.queue), game: { id: g.id, statusTitle: 'Compiling', statusReason: snap.queue.current.progressText, percent } } })
  }, 1000)
  window.setTimeout(() => post({ event: 'notice', data: { kind: 'newShaders', compiling: true, games: [{ id: 'steam:2909400', name: 'God of War', newPipelines: 1200 }], title: 'New shaders', text: 'God of War has 1,200 new shaders after its update: they compile when the PC is idle.' } }), 2500)
}
