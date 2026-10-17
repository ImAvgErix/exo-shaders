// Exo Shaders, an unofficial fork based on SCSKiller. Copyright (C) the SCSKiller authors and Exo Shaders contributors. GPL-3.0-or-later.
import { useEffect, useMemo, useRef, useState } from 'react'
import { act, coverFor, useStore } from '../store'
import { Bar, Btn, Empty, Select, Spinner, arriveAt, openDialog } from '../ui'
import { AddFolderBody, AddGameBody } from './AddGame'
import { Activity } from './Queue'
import { InfoButton } from './About'
import type { Game, LibrarySort, Tone } from '../types'

type Show = 'all' | 'notSupported' | 'ready' | 'compiled'

const SHOW: { value: Show; label: string }[] = [
  { value: 'all', label: 'All games' },
  { value: 'ready', label: 'Ready to compile' },
  { value: 'compiled', label: 'Compiled' },
  { value: 'notSupported', label: 'Not supported' },
]

const SORTS: { value: LibrarySort; label: string }[] = [
  { value: 'status', label: 'By status' },
  { value: 'nameAscending', label: 'By name' },
  { value: 'cacheDescending', label: 'Largest cache' },
]

/** The beta's status filter (Library.Shows), on the snapshot's fields. */
function shows(g: Game, show: Show) {
  switch (show) {
    case 'notSupported': return g.status === 'unsupported' && g.statusText !== 'Not checked'
    case 'ready': return g.status === 'ready' || g.status === 'stale'
    case 'compiled': return !!g.compiledAt || (!g.cacheEstimated && (g.cacheBytes ?? 0) > 0)
    default: return true
  }
}

export const toneClass = (t: Tone) => (t === 'accent' ? 'is-good' : t === 'warn' ? 'is-bad' : t === 'primary' ? 'is-ink' : 'is-faint')

/** Arrive (360ms) plus the last capped delay (15 x 28ms): after this the cards have landed. */
const ARRIVAL_MS = 360 + 15 * 28

export function LibraryPage({ onOpen }: { onOpen: (id: string) => void }) {
  const snap = useStore(s => s.snapshot)!
  useStore(s => s.covers)
  const [show, setShow] = useState<Show>('all')
  const [query, setQuery] = useState('')
  const { app, games } = snap
  const finished = useJustFinished(games)

  // Cards arrive once, the first time there are games to show. After that the arrival is switched off, so a card
  // that changes shelf, a filter, or the page coming back from another tab doesn't play it again.
  const [settled, setSettled] = useState(false)
  const hasGames = games.length > 0
  useEffect(() => {
    if (settled || !hasGames) return
    const t = window.setTimeout(() => setSettled(true), ARRIVAL_MS)
    return () => window.clearTimeout(t)
  }, [settled, hasGames])

  const groups = useMemo(() => {
    const q = query.trim().toLowerCase()
    const list = games.filter(g => shows(g, show) && (!q || g.name.toLowerCase().includes(q) || g.store.toLowerCase().includes(q)))
    const out: { title: string; games: Game[] }[] = []
    const recommended = list.filter(g => g.recommended)
    if (recommended.length) out.push({ title: 'Recommended to compile', games: recommended })
    const byStore = new Map<string, Game[]>()
    for (const g of list) if (!g.recommended) byStore.set(g.store, [...(byStore.get(g.store) ?? []), g])
    for (const [store, gs] of byStore) out.push({ title: store, games: gs })
    return { out, count: list.length }
  }, [games, show, query])

  return (
    <div className={`s-library${settled ? ' is-settled' : ''}`}>
      <div className="s-toolbar">
        <div className="s-toolbar-group">
          <input className="s-search" type="search" placeholder="Search" aria-label="Search games" value={query} onChange={e => setQuery(e.target.value)} />
          <Select label="Show" value={show} options={SHOW} onChange={setShow} />
          <Select label="Sort" value={app.librarySort} options={SORTS} onChange={sort => act('library.setSort', { sort })} />
        </div>
        <div className="s-toolbar-group">
          <Btn onClick={() => void openDialog(close => <AddGameBody onClose={close} onAdded={onOpen} />)}>Add a game</Btn>
          <Btn onClick={() => void openDialog(close => <AddFolderBody onClose={close} />)}>Add a folder</Btn>
          {/* Shift-click scans every game again, not only new and changed ones. */}
          <Btn busy={app.scanning} busyText="Scanning" doneText="Scanned" onClick={e => act('scan', e.shiftKey ? { force: true } : {})}>Scan</Btn>
          <Btn kind="primary" disabled={app.readyCount === 0} onClick={() => act('library.addAllReady')}>
            Compile all ready<span className="s-btn-count">{app.readyCount}</span>
          </Btn>
          <InfoButton />
        </div>
      </div>

      <Activity onOpen={onOpen} />

      <div className="s-scroll">
        {!hasGames ? (
          app.scanning
            ? <Empty title="Looking for your games"><Spinner /></Empty>
            : <Empty title="No games found" line="Add a game or a folder, or scan again." />
        ) : groups.count === 0 ? (
          <Empty title={query ? `No games match “${query}”` : 'No games here'}>
            <Btn onClick={() => { setQuery(''); setShow('all') }}>Show all games</Btn>
          </Empty>
        ) : (
          groups.out.map(group => (
            <section key={group.title} className="s-shelf">
              <h3 className="s-shelf-title">{group.title}</h3>
              <div className="s-grid">
                {group.games.map((g, index) => <Card key={g.id} game={g} index={index} finished={finished.has(g.id)} onOpen={onOpen} />)}
              </div>
            </section>
          ))
        )}
      </div>
    </div>
  )
}

/**
 * One game: its cover, name and one word of state; detail lives on its page. While it compiles a diagonal band of
 * light crosses the cover; the moment it finishes the cover dims under a check that draws itself, then lets go.
 */
function Card({ game: g, index, finished, onOpen }: { game: Game; index: number; finished: boolean; onOpen: (id: string) => void }) {
  const cover = coverFor(g.installDir, g.name)
  const word = g.compiling || g.queued ? g.statusTitle : g.statusText
  const tone = g.compiling ? 'is-good' : g.queued ? 'is-ink' : toneClass(g.tone)
  return (
    <button
      type="button"
      className={`s-card s-arrive${g.playing ? ' is-playing' : ''}${g.compiling ? ' is-compiling' : ''}${finished ? ' is-finished' : ''}`}
      style={arriveAt(index)}
      onClick={() => onOpen(g.id)}
    >
      <span className="s-card-art">
        {cover ? <img className="s-card-cover" src={cover} alt="" loading="lazy" draggable={false} /> : <span className="s-card-cover is-blank">{g.name}</span>}
        {g.compiling ? <span className="s-card-progress"><Bar percent={g.percent} /></span> : null}
        {finished ? <svg className="s-card-check" viewBox="0 0 24 24" aria-hidden="true"><path d="M6 12.5 l4 4 l8 -9" /></svg> : null}
      </span>
      <span className="s-card-name">{g.name}</span>
      <span className={`s-card-status ${tone}`}>{word}</span>
    </button>
  )
}

/** How long a card shows it just finished: the dim, check and ring hold for 2.4s and let go over 0.4s (shaders.css s-done). */
const FINISHED_MS = 3000

/**
 * The games that went from compiling to warmed in the last few seconds. Tracked here rather than in each card, so
 * a card that moves shelf when it finishes (out of "Recommended") still shows it.
 */
function useJustFinished(games: Game[]): ReadonlySet<string> {
  const compiling = useRef(new Set<string>())
  const timers = useRef(new Map<string, number>())
  const [finished, setFinished] = useState<ReadonlySet<string>>(() => new Set())

  useEffect(() => {
    const now = new Set<string>()
    const done: string[] = []
    for (const g of games) {
      if (g.compiling) now.add(g.id)
      else if (compiling.current.has(g.id) && g.status === 'warmed') done.push(g.id)
    }
    compiling.current = now
    if (!done.length) return
    setFinished(prev => new Set([...prev, ...done]))
    for (const id of done) {
      window.clearTimeout(timers.current.get(id))
      timers.current.set(id, window.setTimeout(() => {
        timers.current.delete(id)
        setFinished(prev => { const next = new Set(prev); next.delete(id); return next })
      }, FINISHED_MS))
    }
  }, [games])

  useEffect(() => () => timers.current.forEach(t => window.clearTimeout(t)), [])
  return finished
}
