// Exo Shaders, an unofficial fork based on SCSKiller. Copyright (C) the SCSKiller authors and Exo Shaders contributors. GPL-3.0-or-later.
// Before and after: the intro dialog that starts the before run, and the report with both runs on one graph.
import { useEffect, useState } from 'react'
import { call, errorText } from '../engine'
import { say, useStore } from '../store'
import { Btn, Empty, FieldError, Row, Segmented, Spinner } from '../ui'
import { FrameGraph } from './FrameGraph'
import type { BeforeAfterReport, Game, RunStats } from '../types'

export function BeforeAfterIntro({ game, over, onClose }: { game: Game; over: boolean; onClose: () => void }) {
  const sound = useStore(s => s.snapshot?.settings.beforeAfterSound ?? true)
  const overlay = useStore(s => s.snapshot?.settings.beforeAfterOverlay ?? true)
  const [minutes, setMinutes] = useState<5 | 10>(5)
  const [error, setError] = useState<string | null>(null)
  async function startRun() {
    setError(null)
    try {
      const r = await call<{ ok: boolean; launchError?: string }>('ba.start', { id: game.id, minutes, sound, overlay, over })
      if (r?.launchError) say(r.launchError, 'bad')
      onClose()
    } catch (e) {
      setError(errorText(e))
    }
  }
  return (
    <>
      <h2 className="s-dialog-title">{over ? `Start over with ${game.name}?` : 'Before and after recording'}</h2>
      <p className="s-warn">This deletes the game&rsquo;s shader caches and its current compile. The game rebuilds its own cache, which can take minutes.</p>
      <ol className="s-steps">
        <li>Play {minutes} minutes from a save you can load again, then close the game.</li>
        <li>Exo Shaders compiles the game&rsquo;s shaders.</li>
        <li>Load the same save, play the same {minutes} minutes with the same settings.</li>
      </ol>
      <Row label="Play time">
        <Segmented label="Play time" value={minutes} onChange={setMinutes} options={[{ value: 5, label: '5 minutes' }, { value: 10, label: '10 minutes' }]} />
      </Row>
      <FieldError>{error}</FieldError>
      <div className="s-dialog-actions">
        <Btn onClick={onClose}>Cancel</Btn>
        <Btn kind="primary" onClick={startRun}>Start the before run</Btn>
      </div>
    </>
  )
}

const fps = (v?: number) => (v == null ? '—' : `${Math.round(v)}`)
const ms = (v?: number) => (v == null ? '—' : `${Math.round(v)} ms`)
const secs = (v?: number) => (v == null ? '—' : `${v.toFixed(1)} s`)

const STATS: { label: string; get: (r: RunStats) => string }[] = [
  { label: 'Shader stutters', get: r => String(r.shaderStutters) },
  { label: 'Frozen for', get: r => secs(r.frozenSeconds) },
  { label: 'Worst stutter', get: r => ms(r.worstStutterMs) },
  { label: 'Compiled while playing', get: r => r.playCompiles.toLocaleString('en-US') },
  { label: 'Other hitches', get: r => String(r.otherHitches) },
  { label: 'Average fps', get: r => fps(r.avgFps) },
  { label: '1% low fps', get: r => fps(r.low1PctFps) },
]

export function BeforeAfterReportBody({ game, onClose }: { game: Game; onClose: () => void }) {
  const [report, setReport] = useState<BeforeAfterReport | null | undefined>(undefined)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    call<{ report?: BeforeAfterReport }>('ba.report', { id: game.id })
      .then(r => setReport(r?.report ?? null))
      .catch(e => setError(errorText(e)))
  }, [game.id])
  return (
    <>
      <h2 className="s-dialog-title">Before and after: {game.name}</h2>
      {error ? <p className="s-error">{error}</p> : report === undefined ? <div className="s-loading"><Spinner /></div> : report === null ? (
        <Empty title="No report yet" />
      ) : (
        <>
          <FrameGraph series={[
            { label: 'Before', durationMs: report.before.durationMs, startupMs: 0, peaks: report.before.peaks, hitches: report.before.hitches, tone: 'faint' },
            { label: 'After', durationMs: report.after.durationMs, startupMs: 0, peaks: report.after.peaks, hitches: report.after.hitches, tone: 'ink' },
          ]} />
          <table className="s-table">
            <thead><tr><th />{['Before', 'After'].map(h => <th key={h}>{h}</th>)}</tr></thead>
            <tbody>
              {STATS.map(s => (
                <tr key={s.label}><td>{s.label}{report.compare.perTenMinutes && /stutters|hitches/i.test(s.label) ? ' per 10 min' : ''}</td><td>{s.get(report.compare.before)}</td><td>{s.get(report.compare.after)}</td></tr>
              ))}
            </tbody>
          </table>
        </>
      )}
      <div className="s-dialog-actions"><Btn onClick={onClose}>Close</Btn></div>
    </>
  )
}
