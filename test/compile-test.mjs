// Exo Shaders live compile test: through Exo's shell, compiles the smallest
// game that is ready, and watches the engine until it is compiled or fails.
import { openShell, sleep } from '../../ExoLauncher/tools/e2e/cdp.mjs'

const port = Number(process.argv[2] ?? 9341)
const sh = await openShell(port, 60000)
const call = (method, params = {}) => sh.eval(`new Promise((resolve, reject) => {
  const wv = window.chrome.webview, id = crypto.randomUUID()
  const timer = setTimeout(() => { wv.removeEventListener('message', on); reject(new Error('timeout')) }, 120000)
  function on(e) {
    let d = e.data
    try { if (typeof d === 'string') d = JSON.parse(d) } catch { return }
    if (!d || d.id !== id) return
    clearTimeout(timer); wv.removeEventListener('message', on)
    d.ok ? resolve(d.result) : reject(new Error(d.error || 'host error'))
  }
  wv.addEventListener('message', on)
  wv.postMessage(JSON.stringify({ version: 1, id, method: 'shaders.call', params: { method: ${JSON.stringify(method)}, params: ${JSON.stringify(params)} } }))
})`)

const out = (o) => console.log(JSON.stringify(o))
let snap
for (let i = 0; i < 60; i++) {
  const r = await call('snapshot').catch((e) => ({ ok: false, message: e.message }))
  if (r?.ok && r.result?.games?.length) { snap = r.result; break }
  await sleep(1000)
}
if (!snap) { out({ fail: 'no snapshot from the engine' }); process.exit(1) }
const ready = snap.games.filter((g) => g.status === 'ready' && !g.queued && g.pipelines > 0).sort((a, b) => a.pipelines - b.pipelines)
out({ games: snap.games.length, ready: ready.map((g) => `${g.name} (${g.pipelines})`).slice(0, 6) })
const target = ready[0]
if (!target) { out({ fail: 'no ready game' }); process.exit(1) }
out({ compiling: target.name, id: target.id, pipelines: target.pipelines })
const start = await call('game.compile', { id: target.id })
out({ start })
const began = Date.now()
let last = ''
while (Date.now() - began < 40 * 60000) {
  await sleep(5000)
  const r = await call('snapshot').catch(() => null)
  const g = r?.result?.games?.find((x) => x.id === target.id)
  if (!g) continue
  const line = `${g.statusTitle} | ${g.statusReason}`
  if (line !== last) { out({ t: Math.round((Date.now() - began) / 1000), status: g.status, line, percent: g.percent ?? null }); last = line }
  if (!g.queued && !g.compiling && (g.status === 'warmed' || g.compiledAt)) { out({ done: true, status: g.status, statusText: g.statusText, compiledAt: g.compiledAt, cache: g.cacheText, time: g.compileTime }); process.exit(0) }
  if (!g.queued && !g.compiling && Date.now() - began > 30000) { out({ done: false, status: g.status, statusText: g.statusText, reason: g.statusReason }); process.exit(1) }
}
out({ fail: 'still compiling after 40 minutes' })
process.exit(1)
