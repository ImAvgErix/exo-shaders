// Exo Shaders, an unofficial fork based on SCSKiller. Copyright (C) the SCSKiller authors and Exo Shaders contributors. GPL-3.0-or-later.
import { useCallback, useEffect, useState } from 'react'
import { call, errorText } from '../engine'
import { act, say, useStore } from '../store'
import { Bar, Btn, Empty, Panel, Spinner, arriveAt, ask, confirm } from '../ui'
import type { Inventory } from '../types'

type DeleteResult = { ok: true; bytes: number; files: number; skipped: string[]; text: string; warning: boolean }

/** Every shader cache, read before the info dialog opens so it opens whole, never on a spinner. */
export const readInventory = () => call<Inventory>('cache.inventory')

/** The info dialog's shader cache: how full it is, every cache with what can be deleted, and Clear everything. */
export function CacheSection({ initial }: { initial: Inventory | null }) {
  const cache = useStore(s => s.snapshot!.app.cache)
  const compiling = useStore(s => s.snapshot!.queue.compiling)
  const [inv, setInv] = useState<Inventory | null>(initial)
  const [error, setError] = useState<string | null>(null)
  const [open, setOpen] = useState<Set<string>>(new Set())

  const read = useCallback(async () => {
    setError(null)
    try {
      setInv(await call<Inventory>('cache.inventory'))
    } catch (e) {
      setError(errorText(e))
    }
  }, [])

  useEffect(() => { if (!initial) void read() }, [read, initial])

  async function del(params: Record<string, unknown>, title: string, text: string) {
    const r = await ask({ title, text, buttons: [{ label: 'Delete', value: 'ok', kind: 'danger' }] })
    if (r.value !== 'ok') return
    try {
      const res = await call<DeleteResult>('cache.delete', params)
      // The row going away says it worked; only a delete that left files behind needs words.
      if (res.warning) say(res.text, 'bad')
    } catch (e) {
      say(errorText(e), 'bad')
    }
    await read()
  }

  async function everything(i: Inventory) {
    const hasOthers = i.groups.some(g => g.kind === 'unattributed' && g.bytes > 0)
    const r = await ask({
      title: 'Clear every shader cache?',
      text: checked => <p>{checked ? i.everythingWarning.withOthers : i.everythingWarning.withoutOthers}</p>,
      buttons: [{ label: 'Clear everything', value: 'ok', kind: 'danger' }],
      checkbox: hasOthers ? 'Include other apps and games' : undefined,
    })
    if (r.value !== 'ok') return
    try {
      const res = await call<DeleteResult>('cache.delete', { all: true, withOthers: r.checked })
      // The row going away says it worked; only a delete that left files behind needs words.
      if (res.warning) say(res.text, 'bad')
    } catch (e) {
      say(errorText(e), 'bad')
    }
    await read()
  }

  const toggle = (k: string) => setOpen(prev => { const n = new Set(prev); if (n.has(k)) n.delete(k); else n.add(k); return n })

  // limitText is "of 10 GB limit", "· DirectX 12 ...", "no limit" or "limit unknown".
  const limit = cache ? (/^(of|·)/.test(cache.limitText) ? ` ${cache.limitText}` : ` · ${cache.limitText}`) : ''
  return (
    <Panel title={cache ? `Shader cache · ${cache.usedText}${limit}` : 'Shader cache'}>
      <div>
        {cache ? <Budget compiling={compiling} oldDriverFree={inv?.oldDriver?.freeText} onFreed={read} /> : null}

        {error ? (
          <Empty title="The caches couldn’t be read" line={<span className="s-bad">{error}</span>}><Btn kind="primary" onClick={read}>Try again</Btn></Empty>
        ) : !inv ? (
          <Empty title="Reading the caches"><Spinner /></Empty>
        ) : inv.noFolders || inv.groups.length === 0 ? (
          <Empty title="No shader caches" />
        ) : inv.groups.map((g, gi) => (
          <section key={g.kind} className="s-cache-group s-arrive" style={arriveAt(gi + 1)}>
            <div className="s-list-row">
              <span className="s-list-main">{g.title}</span>
              <span className="s-list-meta">{g.size}</span>
              <span className="s-list-actions">
                {g.items.length ? <Btn compact reserve={['Show', 'Hide']} onClick={() => toggle(g.kind)}>{open.has(g.kind) ? 'Hide' : 'Show'}</Btn> : null}
                {g.bytes > 0 && g.kind !== 'unattributed' ? <Btn compact kind="danger" disabled={compiling} onClick={() => del({ group: g.kind }, `Delete ${g.title.toLowerCase()} (${g.size})?`, g.consequence)}>Delete</Btn> : null}
              </span>
            </div>
            {open.has(g.kind) ? (
              <div className="s-list is-nested">
                {g.items.map((it, i) => (
                  <div key={it.key} className="s-list-row s-arrive" style={arriveAt(i)}>
                    <span className="s-list-main">{it.name}</span>
                    <span className="s-list-meta">{it.size}</span>
                    <span className="s-list-actions">
                      {g.deletable ? <Btn compact disabled={compiling} onClick={() => del({ keys: [it.key] }, `Delete the cache of ${it.name} (${it.size})?`, it.consequence)}>Delete</Btn> : null}
                    </span>
                  </div>
                ))}
              </div>
            ) : null}
          </section>
        ))}
      </div>
      <div className="s-dialog-actions">
        <Btn kind="danger" disabled={compiling || !inv || inv.totalBytes === 0} onClick={() => (inv ? everything(inv) : undefined)}>Clear everything</Btn>
      </div>
    </Panel>
  )
}

/** The driver's cache: how full it is, raising its limit when it can go higher, and freeing an old driver's files. */
function Budget({ compiling, oldDriverFree, onFreed }: { compiling: boolean; oldDriverFree?: string; onFreed: () => Promise<void> }) {
  const snap = useStore(s => s.snapshot!)
  const cache = snap.app.cache!
  const sizes = snap.settings.meta.cacheSizes
  const largest = sizes[sizes.length - 1]
  const canRaise = cache.configurable && !cache.largest && !!largest

  async function raiseLimit() {
    const text = `This ${snap.app.gpu.vendorName} driver setting applies to every game on this PC. Raising it deletes nothing. Windows will ask for administrator permission.`
    if (!(await confirm(`Set the shader cache limit to ${largest.label.toLowerCase()}?`, text, 'Set limit'))) return false
    try {
      const r = await call<{ ok: boolean; text: string }>('driver.setCacheLimit', { index: largest.index })
      if (!r.ok) say(r.text, 'bad')
      return r.ok
    } catch (e) {
      say(errorText(e), 'bad')
      return false
    }
  }

  async function setCleanupGuard(on: boolean) {
    const text = on
      ? 'Windows’ automatic cleanup will skip the DirectX Shader Cache, where the driver keeps compiled shaders. Disk Cleanup and Settings can still delete it when you choose. Windows will ask for administrator permission.'
      : 'Windows’ automatic cleanup can delete compiled shaders again, and games then compile them while you play. Windows will ask for administrator permission.'
    if (!(await confirm(on ? 'Keep compiled shaders from Windows’ cleanup?' : 'Let Windows’ cleanup delete compiled shaders?', text, on ? 'Keep' : 'Allow'))) return false
    try {
      const r = await call<{ ok: boolean; text: string }>('driver.setCleanupGuard', { on })
      if (!r.ok) say(r.text, 'bad')
      return r.ok
    } catch (e) {
      say(errorText(e), 'bad')
      return false
    }
  }

  const cleanup = snap.app.gpu.windowsCleanup
  return (
    <section className="s-budget s-arrive">
      <div className="s-budget-line">
        <span className="s-budget-used">{cache.usedText}</span>
        <span className="s-muted">{cache.limitText}</span>
        {canRaise ? <span className="s-budget-end"><Btn compact doneText="Raised" onClick={raiseLimit}>Raise limit to {largest.label.toLowerCase()}</Btn></span> : null}
      </div>
      <Bar percent={cache.percent} tone={cache.warn ? 'bad' : undefined} />
      {cache.warn ? <p className="s-bad">{cache.warn}</p> : null}
      {snap.app.gpu.shaderDelivery ? (
        <div className="s-budget-old">
          <span className="s-muted">Advanced Shader Delivery</span>
          <span className={snap.app.gpu.shaderDelivery.works ? 's-good' : 's-muted'}>{snap.app.gpu.shaderDelivery.text}</span>
        </div>
      ) : null}
      {cleanup ? (
        <div className="s-budget-old">
          <span className="s-muted">Windows automatic cleanup</span>
          <span className={cleanup.kept ? 's-good' : 's-muted'}>{cleanup.text}</span>
          {!cleanup.kept ? <Btn compact disabled={compiling} doneText="Kept" onClick={() => setCleanupGuard(true)}>Keep</Btn>
            : cleanup.canUndo ? <Btn compact disabled={compiling} onClick={() => setCleanupGuard(false)}>Undo</Btn> : null}
        </div>
      ) : null}
      {cache.oldDriverNote ? (
        <div className="s-budget-old">
          <span className="s-muted">{cache.oldDriverNote}</span>
          <Btn compact disabled={compiling} onClick={async () => {
            if (await confirm('Free the old driver caches?', 'The current driver doesn’t read these files. Nothing needs to compile again.', oldDriverFree ?? 'Free', true)) {
              await act('cache.freeOldDriver')
              await onFreed()
            }
          }}>{oldDriverFree ?? 'Free'}</Btn>
        </div>
      ) : null}
    </section>
  )
}
