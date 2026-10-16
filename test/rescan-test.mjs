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
for (let i = 0; i < 60; i++) { const r = await call("snapshot").catch(() => null); if (r?.ok && r.result?.games?.length) break; await sleep(1000) }
const t = Date.now()
const r = await call("scan", { force: true })
out({ scan: r?.result ?? r, seconds: Math.round((Date.now() - t) / 1000) })
const s = (await call("snapshot")).result
for (const g of s.games.filter((g) => /Sekiro|Aniimo|Spider|Wuthering|League|Rainbow|NBA|Rocket/.test(g.name))) out({ name: g.name, status: g.status, text: g.statusText, reason: g.statusReason?.slice(0, 120), key: g.keyNote ?? null })
const by = {}; for (const g of s.games) by[g.status] = (by[g.status] ?? 0) + 1
out({ total: s.games.length, byStatus: by, launchers: s.app.launchers })
process.exit(0)
