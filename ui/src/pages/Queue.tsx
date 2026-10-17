// Exo Shaders, an unofficial fork based on SCSKiller. Copyright (C) the SCSKiller authors and Exo Shaders contributors. GPL-3.0-or-later.
// The queue on the library page: one strip for what is compiling now, and a dialog for the order of what waits.
import type { ReactNode } from 'react'
import { act, useStore } from '../store'
import { Bar, Btn, confirm, openDialog } from '../ui'
import type { QueueItem } from '../types'

/** The strip over the grid: the running compile with its controls, or what waits, or how the last compiles ended. */
export function Activity({ onOpen }: { onOpen: (id: string) => void }) {
  const q = useStore(s => s.snapshot!.queue)
  const c = q.current
  if (!c && !q.waiting.length && !q.finished.length) return null
  const openQueue = () => void openDialog(close => <QueueBody onOpen={id => { close(); onOpen(id) }} onClose={close} />, { wide: true })
  const failed = q.finished.filter(f => f.stage === 'failed' || f.error).length

  let main: ReactNode
  if (c) {
    main = <>
      <button type="button" className="s-list-name" onClick={() => onOpen(c.id)}>{c.name}</button>
      {/* An error takes the stage line's place rather than adding a line. */}
      {c.error ? <span className="s-activity-line s-bad">{c.error}</span> : (
        <span className="s-activity-line">
          <span className={c.stage === 'paused' ? 's-muted' : 's-good'}>{c.stageText}</span>
          {c.progressText ? <span className="s-muted s-num">{c.progressText}</span> : null}
        </span>
      )}
    </>
  } else if (q.waiting.length) {
    main = <span>{q.waiting.length === 1 ? '1 game waits to compile' : `${q.waiting.length} games wait to compile`}</span>
  } else {
    const one = q.finished.length === 1 ? q.finished[0] : null
    main = one
      ? <><button type="button" className="s-list-name" onClick={() => onOpen(one.id)}>{one.name}</button>
        <span className={`s-activity-line ${one.error || one.stage === 'failed' ? 's-bad' : 's-muted'}`}>{one.error || one.note || one.stageText}</span></>
      : <span>{q.finished.length} compiles finished{failed ? <span className="s-bad">, {failed} failed</span> : null}</span>
  }

  return (
    <section className="s-activity" aria-label="Compiling">
      <div className="s-activity-main">{main}</div>
      <div className="s-activity-end">
        {c ? <>
          <span className="s-muted s-num">{c.time}</span>
          <Btn compact reserve={['Pause', 'Resume']} onClick={() => act(q.paused ? 'queue.resume' : 'queue.pause')}>{q.paused ? 'Resume' : 'Pause'}</Btn>
          <Btn compact kind="danger" onClick={() => act('queue.stop')}>Stop</Btn>
        </> : null}
        {!c && q.canStart ? <Btn compact onClick={() => act('queue.start')}>Compile queue</Btn> : null}
        {!c && !q.waiting.length ? <Btn compact onClick={() => act('queue.clearFinished')}>Clear</Btn> : null}
        <Btn compact onClick={openQueue}>Queue{q.waiting.length ? <span className="s-btn-count">{q.waiting.length}</span> : null}</Btn>
      </div>
      {c ? <span className="s-activity-bar"><Bar percent={c.percent} tone={c.stage === 'paused' ? undefined : 'good'} /></span> : null}
      {q.cacheWarning ? <p className="s-activity-warn s-bad">{q.cacheWarning}</p> : null}
    </section>
  )
}

/** The whole queue: the running game, the order of the waiting ones, and how the finished ones ended. */
function QueueBody({ onOpen, onClose }: { onOpen: (id: string) => void; onClose: () => void }) {
  const q = useStore(s => s.snapshot!.queue)
  const c = q.current
  const empty = !c && !q.waiting.length && !q.finished.length
  return (
    <>
      <h2 className="s-dialog-title">Queue</h2>
      {empty ? <p className="s-muted">Nothing queued</p> : (
        <div className="s-list">
          {c ? (
            <QueueRow item={c} line={c.error ?? c.stageText} onOpen={onOpen}>
              <Btn compact onClick={() => act('queue.remove', { id: c.id })}>Remove</Btn>
            </QueueRow>
          ) : null}
          {q.waiting.map((w, i) => (
            <QueueRow key={w.id} item={w} line={w.error} onOpen={onOpen}>
              <Btn compact label={`Move ${w.name} up`} disabled={i === 0} onClick={() => act('queue.move', { id: w.id, index: i - 1 })}><Arrow up /></Btn>
              <Btn compact label={`Move ${w.name} down`} disabled={i === q.waiting.length - 1} onClick={() => act('queue.move', { id: w.id, index: i + 1 })}><Arrow /></Btn>
              <Btn compact onClick={() => act('queue.remove', { id: w.id })}>Remove</Btn>
            </QueueRow>
          ))}
          {q.finished.map(f => <QueueRow key={f.id} item={f} line={f.error ?? f.stageText} onOpen={onOpen} />)}
        </div>
      )}
      <div className="s-dialog-actions">
        {q.finished.length ? <Btn onClick={() => act('queue.clearFinished')}>Clear finished</Btn> : null}
        {q.canClear ? <Btn onClick={async () => {
          if (await confirm('Clear the queue?', 'Every waiting game leaves the queue. The running compile keeps going.', 'Clear queue', true)) await act('queue.removeMany', {})
        }}>Clear queue</Btn> : null}
        <Btn kind="primary" onClick={onClose}>Done</Btn>
      </div>
    </>
  )
}

function QueueRow({ item, line, onOpen, children }: { item: QueueItem; line?: string; onOpen: (id: string) => void; children?: ReactNode }) {
  const tone = item.error || item.stage === 'failed' ? 's-bad' : item.stage === 'done' ? 's-good' : 's-muted'
  return (
    <div className="s-list-row">
      <span className="s-list-main">
        <button type="button" className="s-list-name" onClick={() => onOpen(item.id)}>{item.name}</button>
        {line ? <span className={`s-list-sub ${tone}`}>{line}</span> : null}
      </span>
      <span className="s-list-meta">{item.time}</span>
      <span className="s-list-actions">{children}</span>
    </div>
  )
}

function Arrow({ up = false }: { up?: boolean }) {
  return (
    <svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true" style={up ? { transform: 'rotate(180deg)' } : undefined}>
      <path d="M2.5 4.5 6 8l3.5-3.5" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  )
}
