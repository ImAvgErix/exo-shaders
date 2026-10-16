// Exo Shaders, an unofficial fork based on SCSKiller. Copyright (C) the SCSKiller authors and Exo Shaders contributors. GPL-3.0-or-later.
import { useCallback, useEffect, useState } from 'react'
import { call, closeSheet } from './engine'
import { act, loadSnapshot, setOpenGameFromNotice, start, useStore } from './store'
import { BackBtn, Boundary, Btn, CloseBtn, DialogHost, Empty, LinkBtn, Segmented, Spinner, openDialog } from './ui'
import { LibraryPage } from './pages/Library'
import { GamePage } from './pages/Game'
import { QueuePage } from './pages/Queue'
import { CachePage } from './pages/Cache'
import { AboutPage } from './pages/About'
import { WhatsNewBody } from './pages/WhatsNew'
import type { Span } from './types'

type Tab = 'library' | 'queue' | 'cache' | 'about'

const reducedMotion = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches

export function App() {
  const [tab, setTab] = useState<Tab>('library')
  const [gameId, setGameId] = useState<string | null>(null)
  // The game page fades out (200ms) before it unmounts; the library under it is already there.
  const [leaving, setLeaving] = useState(false)
  const snapshot = useStore(s => s.snapshot)
  const loading = useStore(s => s.loading)
  const loadError = useStore(s => s.loadError)
  const addon = useStore(s => s.addon)
  const exiting = useStore(s => s.exiting)
  const status = useStore(s => s.status)

  const openGame = useCallback((id: string) => { setLeaving(false); setTab('library'); setGameId(id) }, [])
  const back = useCallback(() => {
    if (reducedMotion()) setGameId(null)
    else setLeaving(true)
  }, [])

  useEffect(() => {
    start()
    setOpenGameFromNotice(openGame)
    const visible = () => void call('ui.visible', { visible: document.visibilityState === 'visible' }).catch(() => {})
    visible()
    document.addEventListener('visibilitychange', visible)
    window.addEventListener('pagehide', () => void call('ui.visible', { visible: false }).catch(() => {}))
    // The "What's new in X" dialog, once after an update.
    void call<{ section?: { version: string; bullets: Span[][] } }>('whatsNew.atStart').then(r => {
      const section = r?.section
      if (section) void openDialog(close => <WhatsNewBody version={section.version} bullets={section.bullets} onClose={close} />, { wide: true })
    }).catch(() => {})
    return () => document.removeEventListener('visibilitychange', visible)
  }, [openGame])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || e.defaultPrevented) return
      if (gameId) back()
      else closeSheet()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [gameId, back])

  const blocked = addon?.state === 'blocked'
  const game = gameId ? snapshot?.games.find(g => g.id === gameId) ?? null : null
  const waiting = snapshot?.app.waitingCount ?? 0

  let body
  if (blocked) {
    body = (
      <Empty title="SCSKiller is open" line={(addon?.message ?? '').replace(/^SCSKiller is open\.\s*/, '') || 'Quit it from its tray icon, then try again.'}>
        <Btn kind="primary" onClick={() => loadSnapshot()}>Retry</Btn>
      </Empty>
    )
  } else if (exiting && !snapshot) {
    body = <Empty title="The shader engine stopped" line={exiting === 'conflict' ? 'SCSKiller started on the same games.' : undefined}><Btn kind="primary" onClick={() => loadSnapshot()}>Retry</Btn></Empty>
  } else if (!snapshot) {
    body = loading
      ? <Empty title="Starting the shader engine"><Spinner /></Empty>
      : <Empty title="The shader engine didn’t answer" line={loadError ?? undefined}><Btn kind="primary" onClick={() => loadSnapshot()}>Retry</Btn></Empty>
  } else {
    body = (
      <>
        <Banners />
        {/* The library stays mounted so its search, filter and scroll survive a tab switch. Each .s-page fades in when shown. */}
        <div className="s-page" hidden={tab !== 'library'}><LibraryPage onOpen={openGame} /></div>
        {tab === 'queue' ? <div className="s-page"><QueuePage onOpen={openGame} /></div> : null}
        {tab === 'cache' ? <div className="s-page"><CachePage /></div> : null}
        {tab === 'about' ? <div className="s-page"><AboutPage /></div> : null}
      </>
    )
  }

  return (
    <div className="s-app">
      <header className="s-head">
        <h1 className="s-title">Shaders</h1>
        <CloseBtn label="Close Shaders" onClick={closeSheet} />
      </header>
      {snapshot && !blocked ? (
        <nav className="s-tabs" aria-label="Shaders">
          <Segmented<Tab>
            label="Page"
            value={tab}
            onChange={t => { setGameId(null); setLeaving(false); setTab(t) }}
            onSame={gameId ? back : undefined}
            options={[
              { value: 'library', label: 'Library' },
              // The count shows only while games wait.
              { value: 'queue', label: <>Queue<span className="s-count" data-on={waiting > 0}>{waiting}</span></> },
              { value: 'cache', label: 'Cache' },
              { value: 'about', label: 'About' },
            ]}
          />
          {snapshot.app.status === 'compiling' ? <span className="s-app-state">{snapshot.app.statusLine}</span> : null}
        </nav>
      ) : null}
      <main className="s-body">
        <Boundary>{body}</Boundary>
        {gameId && snapshot && !blocked ? (
          <div className={`s-sheet${leaving ? ' is-leaving' : ''}`}
            onAnimationEnd={e => { if (leaving && e.target === e.currentTarget) { setGameId(null); setLeaving(false) } }}>
            {game
              ? <Boundary key={game.id} onReset={back}><GamePage game={game} onBack={back} /></Boundary>
              : <GoneGame onBack={back} />}
          </div>
        ) : null}
      </main>
      <footer className={`s-status${status ? ' is-shown' : ''}${status?.tone === 'bad' ? ' is-bad' : status?.tone === 'good' ? ' is-good' : ''}`} role="status" aria-live="polite">
        {status ? <>
          <span className="s-status-text">{status.text}</span>
          {status.action ? <LinkBtn onClick={status.action.run}>{status.action.label}</LinkBtn> : null}
        </> : null}
      </footer>
      <Boundary><DialogHost /></Boundary>
    </div>
  )
}

function GoneGame({ onBack }: { onBack: () => void }) {
  return (
    <div className="s-sheet-scroll">
      <div className="s-game-titlebar"><BackBtn label="Back to library" onClick={onBack} /></div>
      <Empty title="This game isn’t in the library any more" />
    </div>
  )
}

/** The app's info bars: the GPU notice and the "driver updated" offer. */
function Banners() {
  const app = useStore(s => s.snapshot?.app)
  if (!app) return null
  const stale = app.driverStale?.notify ? app.driverStale : null
  if (!app.gpu.notice && !stale) return null
  return (
    <div className="s-banners">
      {app.gpu.notice ? (
        <div className="s-banner">
          <span className="s-banner-text">{app.gpu.notice}</span>
          <Btn compact onClick={() => act('app.dismissGpuNotice')}>Dismiss</Btn>
        </div>
      ) : null}
      {stale ? (
        <div className="s-banner">
          <span className="s-banner-text">{stale.text}</span>
          <span className="s-banner-actions">
            <Btn compact onClick={() => act('driverStale.skip')}>Skip</Btn>
            <Btn compact kind="primary" onClick={() => act('driverStale.compileNow')}>Compile now</Btn>
          </span>
        </div>
      ) : null}
    </div>
  )
}
