// Exo Shaders, an unofficial fork based on SCSKiller. Copyright (C) the SCSKiller authors and Exo Shaders contributors. GPL-3.0-or-later.
// A game's page, laid out like Exo's: the cover left, the title, one state word and the actions right of it, then the
// sections in two columns of rows.
import { useEffect, useRef, useState } from 'react'
import { call, errorText } from '../engine'
import { act, coverFor, say, useStore } from '../store'
import { BackBtn, Bar, Btn, Panel, Row, Spinner, Value, ask, confirm, openDialog } from '../ui'
import { AesKeyBody, FolderBody, RenameBody } from './AddGame'
import { FrameGraph } from './FrameGraph'
import { BeforeAfterIntro, BeforeAfterReportBody } from './BeforeAfter'
import { toneClass } from './Library'
import type { Game, GameCaches, GameDetail } from '../types'

const CLEAR_RECORDING_CONFIRM = 'The recorded pipelines are removed. They’re compiled again only if the game creates them again while recording.\n\nThe recorder itself stays in, and the game’s files aren’t touched.'

/** What makes the detail worth re-reading: anything in the snapshot's game except live progress text. */
const detailKey = (g: Game) => JSON.stringify({ ...g, statusReason: undefined, percent: undefined, statusTitle: g.compiling ? undefined : g.statusTitle })

/**
 * The one line under the state word: an error, live progress while compiling, or the plain first sentence of why a
 * game can't be compiled. The line is always there (empty otherwise) so nothing below it moves.
 */
function stateLine(g: Game): { text: string; bad: boolean } | null {
  const error = g.error ?? g.cacheLimitWarning ?? (g.similar?.warning ? g.similar.text : undefined)
  if (error) return { text: error, bad: true }
  if (g.compiling) return g.statusReason ? { text: g.statusReason, bad: false } : null
  if (g.status !== 'unsupported' && g.status !== 'needsRecording') return null
  const first = g.statusReason.split(/\.\s|:\s/)[0]
  return first ? { text: first, bad: false } : null
}

export function GamePage({ game: g, onBack }: { game: Game; onBack: () => void }) {
  const [detail, setDetail] = useState<GameDetail | null>(null)
  const [detailError, setDetailError] = useState<string | null>(null)
  const beforeAfterOn = useStore(s => !!s.snapshot?.settings.beforeAfter)
  useStore(s => s.covers)
  const key = detailKey(g)
  const checked = useRef<string | null>(null)

  useEffect(() => {
    let live = true
    const t = window.setTimeout(() => {
      call<GameDetail>('game.detail', { id: g.id })
        .then(d => { if (live) { setDetail(d); setDetailError(null) } })
        .catch(e => { if (live) setDetailError(errorText(e)) })
    }, detail ? 300 : 0)
    return () => { live = false; window.clearTimeout(t) }
  }, [g.id, key])

  // The page checks a game that hasn't been checked yet when it opens.
  useEffect(() => {
    if (g.statusText === 'Not checked' && checked.current !== g.id) {
      checked.current = g.id
      void act('game.check', { id: g.id })
    }
  }, [g.id, g.statusText])

  const d = detail?.id === g.id ? detail : null
  const cover = coverFor(g.installDir, g.name)
  const a = g.actions
  const line = stateLine(g)
  const compileLabel = a.compileText === 'Compile' ? 'Compile now' : a.compileText
  const showLastPlay = !!d && !!(d.frames || d.framesSummary || d.session || d.hitchRows.length)

  return (
    <div className="s-sheet-scroll" role="dialog" aria-label={g.name}>
      <div className="s-game-head s-arrive">
        <span className={`s-game-cover${g.compiling ? ' is-compiling' : ''}`}>
          {cover ? <img src={cover} alt="" draggable={false} /> : <span className="s-card-cover is-blank">{g.name}</span>}
        </span>
        <div className="s-game-main">
          <div className="s-game-titlebar">
            <BackBtn label="Back to library" onClick={onBack} />
            <h2 className="s-game-name">{g.name}</h2>
          </div>
          <span className={`s-game-word ${g.compiling ? 'is-good' : g.queued ? 'is-ink' : toneClass(g.tone)}`}>{g.statusTitle}</span>
          <span className={`s-game-line${line?.bad ? ' is-bad' : ''}`}>{line?.text}</span>
          <span className="s-game-bar">{g.compiling ? <Bar percent={g.percent} tone="good" /> : null}</span>
          <div className="s-game-actions">
            {/* One button for both states, as wide as its widest label, so the row never shifts when it's pressed. */}
            <Btn kind="primary" disabled={!g.queued && !a.compile} reserve={[compileLabel, 'Remove from queue', 'Stop and remove']}
              onClick={() => (g.queued ? act('queue.remove', { id: g.id }) : act('game.compile', { id: g.id }))}>
              {g.queued ? (g.compiling ? 'Stop and remove' : 'Remove from queue') : compileLabel}
            </Btn>
            {a.add ? <Btn disabled={g.queued || !a.addEnabled} doneText="Added" onClick={() => act('queue.add', { id: g.id })}>Add to queue</Btn> : null}
            {a.setAesKey ? <Btn onClick={() => void openDialog(close => <AesKeyBody id={g.id} name={g.name} onClose={close} />)}>Set AES key</Btn> : null}
            {a.play ? <Btn disabled={!a.playEnabled} onClick={() => act('game.play', { id: g.id })}>{a.playText}</Btn> : null}
          </div>
        </div>
      </div>

      {!d ? (
        detailError ? <p className="s-error">{detailError}</p> : <div className="s-loading"><Spinner /></div>
      ) : (
        <div className="s-columns">
          <PlanPanel d={d} />
          <RecordingPanel g={g} />
          {g.beforeAfter.shown || beforeAfterOn ? <BeforeAfterPanel g={g} /> : null}
          {showLastPlay ? <LastPlayPanel d={d} /> : null}
          <CachePanel g={g} />
          <ManagePanel g={g} />
        </div>
      )}
    </div>
  )
}

// ------------------------------------------------------------------ panels

function PlanPanel({ d }: { d: GameDetail }) {
  return (
    <Panel title="Compile plan" i={1}>
      <Row label="Coverage"><Value tone={d.coverage.good ? 'good' : d.coverage.low ? 'bad' : undefined}>{d.coverage.value}</Value></Row>
      {d.pipelines != null ? <Row label="Pipelines"><Value>{d.pipelines.toLocaleString('en-US')}</Value></Row> : null}
      {d.compileTime ? <Row label={d.compiledAt ? 'Last compile took' : 'Compile time'}><Value>{d.compileTime}</Value></Row> : null}
    </Panel>
  )
}

function RecordingPanel({ g }: { g: Game }) {
  const r = g.recorder
  return (
    <Panel title="Recording" i={2}>
      <Row label={r.effective ? 'Records while you play' : 'Not recorded'} error={r.refused} />
      {r.recordingBytes > 0 || r.recordingText ? (
        <Row label="Recorded" error={r.paused ? r.pausedText : undefined}>
          <span className="s-inline">
            <Value>{r.recordingText ?? ''}</Value>
            <Btn compact disabled={!r.canClear} doneText="Cleared" onClick={() => clearRecording(g)}>Clear</Btn>
          </span>
        </Row>
      ) : null}
    </Panel>
  )
}
async function clearRecording(g: Game) {
  if (await confirm(`Clear the recording of ${g.name}?`, CLEAR_RECORDING_CONFIRM, 'Clear recording', true)) return act('recorder.clear', { id: g.id })
}

function BeforeAfterPanel({ g }: { g: Game }) {
  const ba = g.beforeAfter
  const stage = ba.stage
  async function main() {
    if (!stage || ba.voided) {
      const b = await call<{ blocked?: string }>('ba.blocked', { id: g.id }).catch(() => ({} as { blocked?: string }))
      if (b.blocked) { say(b.blocked, 'bad'); return }
      await openDialog(close => <BeforeAfterIntro game={g} over={!!stage} onClose={close} />, { wide: true })
      return
    }
    if (ba.compileAgain || ba.clearPending || stage === 'compiling' || stage === 'afterReady') { await act('ba.continue', { id: g.id }); return }
    if (stage === 'beforeArmed' || stage === 'afterArmed') { await act('ba.launch', { id: g.id }); return }
    if (stage === 'done') await openDialog(close => <BeforeAfterReportBody game={g} onClose={close} />, { wide: true })
  }
  async function cancel() {
    const r = await ask({
      title: 'Cancel the before and after recording?',
      text: 'Its shader caches were cleared, so the game stutters until it’s compiled again.',
      cancel: 'Keep going',
      buttons: [{ label: 'Don’t compile', value: 'no' }, { label: 'Compile now', value: 'yes', kind: 'primary' }],
    })
    if (r.value) await act('ba.cancel', { id: g.id, compile: r.value === 'yes' })
  }
  async function again() {
    if (await confirm('Record the after run again?', 'The caches are cleared and the game compiled again first. Then play the same save again.', 'Record again')) {
      await act('ba.recordAfterAgain', { id: g.id })
    }
  }
  // The engine's note says where a run stands; a problem says why it can't start.
  const label = ba.problem ?? ba.note ?? (stage === 'done' ? 'Report ready' : stage ? 'In progress' : 'Not measured yet')
  return (
    <Panel title="Before and after" i={3}>
      <Row label={ba.problem ? <span className="s-bad">{label}</span> : label}>
        <Btn compact disabled={!ba.enabled} onClick={main}>{ba.text}</Btn>
      </Row>
      {ba.hasMenu ? (
        <>
          {stage === 'done'
            ? <Row label="After run"><Btn compact onClick={again}>Record again</Btn></Row>
            : <Row label="This measurement"><Btn compact onClick={cancel}>Cancel</Btn></Row>}
          <Row label="From the before run">
            <Btn compact onClick={() => void openDialog(close => <BeforeAfterIntro game={g} over onClose={close} />, { wide: true })}>Start over</Btn>
          </Row>
        </>
      ) : null}
    </Panel>
  )
}

function LastPlayPanel({ d }: { d: GameDetail }) {
  const f = d.frames
  return (
    <Panel title="Last play" i={4}>
      {f ? (
        <>
          <Row label={f.verdict}><Value>{Math.round(f.avgFps)} fps · 1% low {Math.round(f.low1PctFps)}</Value></Row>
          <FrameGraph series={[{ label: 'Last play', durationMs: f.durationMs, startupMs: f.startupMs, peaks: f.peaks, hitches: f.hitches, tone: 'ink' }]} />
        </>
      ) : d.framesSummary ? <Row label={d.framesSummary.verdict} /> : null}
      {d.hitchRows.map(r => <Row key={r.label} label={r.label}><Value>{r.value}</Value></Row>)}
      {d.session ? <Row label="Compiled while playing"><Value>{d.session.compiles.toLocaleString('en-US')}</Value></Row> : null}
    </Panel>
  )
}

const fmtDate = (iso?: string) => (iso ? new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' }) : '')

function CachePanel({ g }: { g: Game }) {
  const a = g.actions
  async function clear() {
    let info: GameCaches
    try {
      info = await call<GameCaches>('game.caches', { id: g.id })
    } catch (e) {
      say(errorText(e), 'bad')
      return
    }
    const r = await ask({
      title: info.title || `Clear the shader cache of ${g.name}?`,
      text: (
        <>
          <p>{info.text}</p>
          {info.parts.length ? (
            <div className="s-list">
              {info.parts.map(p => <div key={p.name} className="s-list-row"><span className="s-list-main">{p.name}</span><span className="s-list-meta">{p.size}</span></div>)}
            </div>
          ) : null}
        </>
      ),
      checkbox: info.ownText,
      buttons: [{ label: 'Clear cache', value: 'ok', kind: 'danger' }],
    })
    if (r.value === 'ok') return act('game.clearCache', { id: g.id, includeGameCache: r.checked })
  }
  return (
    <Panel title="Cache" i={5}>
      <Row label="Shader cache">
        <span className="s-inline">
          <Value>{g.cacheText}</Value>
          {a.clearCache ? <Btn compact kind="danger" disabled={!a.clearCacheEnabled} doneText="Cleared" onClick={clear}>Clear</Btn> : null}
        </span>
      </Row>
      {g.compiledAt ? <Row label="Compiled"><Value>{fmtDate(g.compiledAt)}</Value></Row> : null}
      {g.kept ? (
        <Row label="Kept aside">
          <span className="s-inline">
            <Value>{g.kept.sizeText}</Value>
            <Btn compact onClick={() => act('game.putBackKept', { id: g.id })}>Put back</Btn>
            <Btn compact kind="danger" onClick={async () => { if (await confirm(`Drop the kept cache of ${g.name}?`, 'The cache kept aside is deleted. The game compiles again to get it back.', 'Drop', true)) await act('game.dropKept', { id: g.id }) }}>Drop</Btn>
          </span>
        </Row>
      ) : null}
    </Panel>
  )
}

function ManagePanel({ g }: { g: Game }) {
  const a = g.actions
  return (
    <Panel title="Game" i={6}>
      {a.gameFolder ? (
        <Row label="Game folder">
          <Btn compact onClick={() => void openDialog(close => (
            <FolderBody title={`Game folder of ${g.name}`} exe={g.exe} initial={g.installDir} confirmText="Save" onClose={close}
              onConfirm={async folder => { await call('games.setFolder', { id: g.id, folder }); close() }} />
          ))}>Change</Btn>
        </Row>
      ) : null}
      {a.rename ? <Row label="Name"><Btn compact onClick={() => void openDialog(close => <RenameBody id={g.id} name={g.name} onClose={close} />)}>Rename</Btn></Row> : null}
      <Row label="Remove from Shaders">
        <Btn compact kind="danger" onClick={async () => {
          if (await confirm(a.remove.confirmTitle, a.remove.confirmText, a.remove.text, true)) await act('games.remove', { id: g.id })
        }}>{a.remove.text}</Btn>
      </Row>
    </Panel>
  )
}
