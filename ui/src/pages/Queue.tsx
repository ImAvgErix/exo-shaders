// Exo Shaders, an unofficial fork based on SCSKiller. Copyright (C) the SCSKiller authors and Exo Shaders contributors. GPL-3.0-or-later.
import type { ReactNode } from 'react'
import { act, useStore } from '../store'
import { Bar, Btn, Empty, arriveAt, confirm } from '../ui'
import type { QueueItem } from '../types'

export function QueuePage({ onOpen }: { onOpen: (id: string) => void }) {
  const q = useStore(s => s.snapshot!.queue)
  const c = q.current
  const empty = !c && q.waiting.length === 0 && q.finished.length === 0
  // One running order for the arrival: the current compile, then waiting, then finished.
  const waitingFrom = c ? 1 : 0
  const finishedFrom = waitingFrom + q.waiting.length + (q.waiting.length ? 1 : 0)

  return (
    <div className="s-queue">
      <div className="s-toolbar is-end">
        <div className="s-toolbar-group">
          <Btn disabled={!q.canClear} onClick={async () => {
            if (await confirm('Clear the queue?', 'Every waiting game leaves the queue. The running compile keeps going.', 'Clear queue', true)) await act('queue.removeMany', {})
          }}>Clear queue</Btn>
          <Btn kind="primary" disabled={!q.canStart} onClick={() => act('queue.start')}>Compile queue</Btn>
        </div>
      </div>

      <div className="s-scroll">
        {empty ? <Empty title="Nothing queued" /> : (
          <>
            {c ? (
              <section className="s-current s-arrive">
                <div className="s-current-head">
                  <button type="button" className="s-current-name" onClick={() => onOpen(c.id)}>{c.name}</button>
                  <span className="s-current-time">{c.time}</span>
                </div>
                <Bar percent={c.percent} tone={c.stage === 'paused' ? undefined : 'good'} />
                {/* One line for where it stands; an error takes its place rather than adding a line. */}
                <div className="s-current-line">
                  {c.error ? <span className="s-bad">{c.error}</span> : <>
                    <span className={c.stage === 'paused' ? 's-muted' : 's-good'}>{c.stageText}</span>
                    {c.progressText ? <span className="s-muted s-num">{c.progressText}</span> : null}
                  </>}
                </div>
                <div className="s-current-actions">
                  <Btn reserve={['Pause', 'Resume']} onClick={() => act(q.paused ? 'queue.resume' : 'queue.pause')}>{q.paused ? 'Resume' : 'Pause'}</Btn>
                  <Btn kind="danger" onClick={() => act('queue.stop')}>Stop</Btn>
                  <Btn onClick={() => act('queue.remove', { id: c.id })}>Remove</Btn>
                </div>
              </section>
            ) : null}
            {q.cacheWarning ? <p className="s-error">{q.cacheWarning}</p> : null}

            {q.waiting.length ? (
              <section className="s-shelf">
                <div className="s-shelf-head s-arrive" style={arriveAt(waitingFrom)}><h3 className="s-shelf-title">Waiting</h3></div>
                <div className="s-list">
                  {q.waiting.map((w, i) => (
                    <QueueRow key={w.id} item={w} i={waitingFrom + 1 + i} onOpen={onOpen}>
                      <Btn compact label={`Move ${w.name} up`} disabled={i === 0} onClick={() => act('queue.move', { id: w.id, index: i - 1 })}><Arrow up /></Btn>
                      <Btn compact label={`Move ${w.name} down`} disabled={i === q.waiting.length - 1} onClick={() => act('queue.move', { id: w.id, index: i + 1 })}><Arrow /></Btn>
                      <Btn compact onClick={() => act('queue.remove', { id: w.id })}>Remove</Btn>
                    </QueueRow>
                  ))}
                </div>
              </section>
            ) : null}

            {q.finished.length ? (
              <section className="s-shelf">
                <div className="s-shelf-head s-arrive" style={arriveAt(finishedFrom)}>
                  <h3 className="s-shelf-title">Finished</h3>
                  <Btn compact onClick={() => act('queue.clearFinished')}>Clear</Btn>
                </div>
                <div className="s-list">
                  {q.finished.map((f, i) => <QueueRow key={f.id} item={f} i={finishedFrom + 1 + i} onOpen={onOpen} />)}
                </div>
              </section>
            ) : null}
          </>
        )}
      </div>
    </div>
  )
}

/** A waiting row says nothing under its name unless it failed; a finished one says how it ended. */
function QueueRow({ item, i, onOpen, children }: { item: QueueItem; i: number; onOpen: (id: string) => void; children?: ReactNode }) {
  const line = item.error ?? (item.stage === 'waiting' ? null : item.stageText)
  const tone = item.error || item.stage === 'failed' ? 's-bad' : item.stage === 'done' ? 's-good' : 's-muted'
  return (
    <div className="s-list-row s-arrive" style={arriveAt(i)}>
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
