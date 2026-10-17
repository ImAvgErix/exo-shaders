// Exo Shaders, an unofficial fork based on SCSKiller. Copyright (C) the SCSKiller authors and Exo Shaders contributors. GPL-3.0-or-later.
import { useCallback, useEffect, useState } from 'react'
import { call, closeSheet, embedded } from './engine'
import { act, loadSnapshot, setOpenGameFromNotice, start, useStore } from './store'
import { BackBtn, Boundary, Btn, CloseBtn, DialogHost, Empty, LinkBtn, Spinner, openDialog } from './ui'
import { LibraryPage } from './pages/Library'
import { GamePage } from './pages/Game'
import { WhatsNewBody } from './pages/WhatsNew'
import type { Span } from './types'

const reducedMotion = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches

export function App() {
  const [gameId, setGameId] = useState<string | null>(null)
  // The game page fades out (200ms) before it unmounts; the library under it is already there.
  const [leaving, setLeaving] = useState(false)
  const snapshot = useStore(s => s.snapshot)
  const loading = useStore(s => s.loading)
  const loadError = useStore(s => s.loadError)
  const addon = useStore(s => s.addon)
  const exiting = useStore(s => s.exiting)
  const status = useStore(s => s.status)

  const openGame = useCallback((id: string) => { setLeaving(false); setGameId(id) }, [])
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
        <LibraryPage onOpen={openGame} />
      </>
    )
  }

  return (
    <div className="s-app">
      {/* Inside Exo the GPU room has the title and the close button. */}
      {!embedded && (
        <header className="s-head">
          <h1 className="s-title">Shaders</h1>
          <CloseBtn label="Close Shaders" onClick={closeSheet} />
        </header>
      )}
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
      {/* The passing status message floats over the bottom edge; nothing sits there otherwise. */}
      <div className={`s-status${status ? ' is-shown' : ''}${status?.tone === 'bad' ? ' is-bad' : status?.tone === 'good' ? ' is-good' : ''}`} role="status" aria-live="polite">
        {status ? <>
          <span className="s-status-text">{status.text}</span>
          {status.action ? <LinkBtn onClick={status.action.run}>{status.action.label}</LinkBtn> : null}
        </> : null}
      </div>
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
  const cacheWarn = app.cache?.warn
  if (!app.gpu.notice && !stale && !cacheWarn) return null
  return (
    <div className="s-banners">
      {app.gpu.notice ? (
        <div className="s-banner">
          <span className="s-banner-text">{app.gpu.notice}</span>
          <Btn compact onClick={() => act('app.dismissGpuNotice')}>Dismiss</Btn>
        </div>
      ) : null}
      {cacheWarn ? (
        <div className="s-banner">
          <span className="s-banner-text s-bad">{cacheWarn}</span>
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
