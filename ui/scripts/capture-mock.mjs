// Exo Shaders, an unofficial fork based on SCSKiller. Copyright (C) the SCSKiller authors and Exo Shaders contributors. GPL-3.0-or-later.
// Dev only: runs the engine service in --safe mode against the copied test data and saves its replies as the dev mock.
// Never point this at the real data folder.
import { spawn } from 'node:child_process'
import { writeFileSync, mkdirSync } from 'node:fs'
import { resolve, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const exe = 'C:\\Users\\Player\\exo-shaders-work\\engine\\out\\service\\ExoShaders.Service.exe'
const dataDir = 'C:\\Users\\Player\\exo-shaders-work\\testdata\\SCSKiller'
const out = resolve(root, 'src/dev/mock')
mkdirSync(out, { recursive: true })

const child = spawn(exe, ['--safe', '--data-dir', dataDir], { stdio: ['pipe', 'pipe', 'pipe'] })
let buf = ''
let nextId = 1
const waiting = new Map()
const events = []
child.stderr.on('data', d => process.stderr.write(d))
child.stdout.on('data', d => {
  buf += d.toString('utf8')
  let i
  while ((i = buf.indexOf('\n')) >= 0) {
    const line = buf.slice(0, i).trim()
    buf = buf.slice(i + 1)
    if (!line) continue
    const m = JSON.parse(line)
    if (m.id != null && waiting.has(m.id)) { waiting.get(m.id)(m); waiting.delete(m.id) }
    else if (m.event) events.push(m)
  }
})
const call = (method, params) => new Promise(res => {
  const id = nextId++
  waiting.set(id, res)
  child.stdin.write(JSON.stringify({ id, method, params }) + '\n')
})
const save = (name, value) => writeFileSync(resolve(out, name + '.json'), JSON.stringify(value, null, 1))

const scan = await call('scan', {})
console.log('scan', JSON.stringify(scan).slice(0, 200))
const snap = await call('snapshot')
save('snapshot', snap.result)
console.log('games', snap.result?.games?.length)
const details = {}
for (const g of (snap.result?.games ?? []).slice(0, 40)) {
  const d = await call('game.detail', { id: g.id })
  if (d.result) details[g.id] = d.result
}
save('details', details)
save('inventory', (await call('cache.inventory')).result ?? null)
save('settings', (await call('settings.get')).result ?? null)
save('whatsnew', (await call('whatsNew', { all: true })).result ?? null)
const caches = {}
for (const g of (snap.result?.games ?? []).slice(0, 40)) {
  const c = await call('game.caches', { id: g.id })
  if (c.result) caches[g.id] = c.result
}
save('caches', caches)
save('events', events.filter(e => e.event !== 'state').slice(0, 20))
await call('shutdown', { waitSeconds: 5 })
setTimeout(() => process.exit(0), 3000)
