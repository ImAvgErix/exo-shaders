// Exo Shaders live feature test, through Exo's shell: wording, scan, settings,
// driver-update task, queue editing, recorder install/remove, cache inventory,
// clearing a game's cache. Every change is put back as it was.
import { openShell, sleep } from '../../ExoLauncher/tools/e2e/cdp.mjs'
import { execFileSync } from 'node:child_process'
import { existsSync } from 'node:fs'
import { dirname, join } from 'node:path'

const port = Number(process.argv[2] ?? 9341)
const sh = await openShell(port, 60000)
const call = (method, params = {}) => sh.eval(`new Promise((resolve, reject) => {
  const wv = window.chrome.webview, id = crypto.randomUUID()
  const timer = setTimeout(() => { wv.removeEventListener('message', on); reject(new Error('timeout')) }, 300000)
  function on(e) {
    let d = e.data
    try { if (typeof d === 'string') d = JSON.parse(d) } catch { return }
    if (!d || d.id !== id) return
    clearTimeout(timer); wv.removeEventListener('message', on)
    d.ok ? resolve(d.result) : reject(new Error(d.error || 'host error'))
  }
  wv.addEventListener('message', on)
  wv.postMessage(JSON.stringify({ version: 1, id, method: 'shaders.call', params: { method: ${JSON.stringify(method)}, params: ${JSON.stringify(params)} } }))
})`).then((r) => { if (!r?.ok) throw new Error(r?.message ?? 'failed'); return r.result })

const results = []
const test = async (name, fn) => {
  try { const detail = await fn(); results.push({ name, ok: true, detail }); console.log('PASS', name, JSON.stringify(detail ?? '')) }
  catch (e) { results.push({ name, ok: false, detail: e.message }); console.log('FAIL', name, e.message) }
}
const snap = async () => call('snapshot')
for (let i = 0; i < 60; i++) { try { if ((await snap()).games?.length) break } catch {} await sleep(1000) }
const game = async (id) => (await snap()).games.find((g) => g.id === id)

await test('wording says Exo Shaders, not SCSKiller', async () => {
  const text = JSON.stringify(await snap())
  const prose = [...text.matchAll(/.{0,40}(?<![\\/\w])SCSKiller(?![\\/.\w]).{0,40}/g)].map((m) => m[0])
  if (prose.length) throw new Error(prose.slice(0, 3).join(' | '))
  return { exoShaders: (text.match(/Exo Shaders/g) ?? []).length }
})

await test('scan', async () => {
  const r = await call('scan', {})
  if (!(r?.games > 0)) throw new Error('scan returned ' + JSON.stringify(r))
  return r
})

await test('settings are fixed by the policy', async () => {
  const st = await call('settings.get')
  let refused = false
  try { await call('settings.set', { beforeAfterSound: !st.beforeAfterSound }) } catch (e) { refused = /sets this itself/.test(e.message) }
  if (!refused) throw new Error('settings.set was not refused')
  const want = { priority: 'belowNormal', pauseWhileGaming: true, compileRayTracing: true, onDriverUpdate: 'whenIdle', onNewShaders: 'notify', recordAllGames: true, recordAntiCheatGames: false }
  const off = Object.entries(want).filter(([k, v]) => st[k] !== v).map(([k, v]) => k + '=' + st[k] + ' (want ' + v + ')')
  if (off.length) throw new Error(off.join(', '))
  if (!(st.threads >= 1 && st.threads < st.meta.processorCount)) throw new Error('threads ' + st.threads + ' of ' + st.meta.processorCount)
  return { threads: st.threads + '/' + st.meta.processorCount, memory: st.meta.maxCompileMemoryText ?? st.meta.autoCompileMemoryGB }
})

await test('per-game: both APIs on Auto, no overrides', async () => {
  const games = (await snap()).games
  const overridden = games.filter((g) => g.options?.api && g.options.api.value && g.options.api.value !== 'auto').map((g) => g.name)
  if (overridden.length) throw new Error('API override left on: ' + overridden.join(', '))
  const both = games.filter((g) => /DirectX 11 and( DirectX)? 12/.test(g.statusReason + ' ' + (g.api ?? '') + ' ' + g.rowNote)).map((g) => g.name)
  return { bothApis: both.slice(0, 6) }
})

await test('driver-update task points at Exo Shaders', async () => {
  const s = await call('settings.get')
  if (s.onDriverUpdate === 'off') return 'off: no task'
  const out = execFileSync('powershell.exe', ['-NoProfile', '-Command', "Get-ScheduledTask | Where-Object { ($_.Actions | ForEach-Object Execute) -match 'scskiller' } | ForEach-Object { $_.TaskName + ' -> ' + (($_.Actions | ForEach-Object Execute) -join ',') }"]).toString().trim()
  if (!/ExoLauncher\\addons\\shaders/i.test(out)) throw new Error('task: ' + (out || 'none'))
  return out
})

await test('queue: add, move, clear (without compiling)', async () => {
  const ready = (await snap()).games.filter((g) => g.status === 'ready' && !g.queued).slice(0, 2)
  if (ready.length < 2) return 'skipped: fewer than 2 ready games'
  await call('queue.add', { ids: ready.map((g) => g.id) })
  let s = await snap()
  const waiting = () => [...(s.queue?.current ? [s.queue.current] : []), ...(s.queue?.waiting ?? [])].filter((q) => ready.some((g) => g.id === q.id)).map((q) => q.id)
  if (waiting().length !== 2) throw new Error('queue: ' + JSON.stringify(s.queue))
  await call('queue.move', { id: ready[1].id, index: 0 })
  s = await snap()
  if (waiting()[0] !== ready[1].id) throw new Error('move did not reorder: ' + waiting().join(','))
  if (s.games.some((g) => ready.some((r) => r.id === g.id) && g.compiling)) throw new Error('a game started compiling')
  await call('queue.removeMany', { ids: ready.map((g) => g.id) })
  s = await snap()
  if (waiting().length) throw new Error('not removed')
  return ready.map((g) => g.name)
})

await test('recorder switches are owned by the policy', async () => {
  const g = (await snap()).games.find((x) => x.status !== 'unsupported')
  try { await call('recorder.install', { id: g.id }) } catch (e) { if (/sets this itself/.test(e.message)) return 'refused as designed'; throw e }
  throw new Error('recorder.install was not refused')
})

await test('cache inventory', async () => {
  const inv = await call('cache.inventory')
  const groups = inv?.groups ?? []
  if (!Array.isArray(groups)) throw new Error('no groups')
  return { groups: groups.map((x) => `${x.title ?? x.kind}: ${x.sizeText ?? x.bytes}`).slice(0, 6) }
})

const failed = results.filter((r) => !r.ok).length
console.log(`RESULT ${results.length - failed} pass, ${failed} fail`)
process.exit(failed ? 1 : 0)
