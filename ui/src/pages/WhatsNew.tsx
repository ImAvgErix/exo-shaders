// Exo Shaders, an unofficial fork based on SCSKiller. Copyright (C) the SCSKiller authors and Exo Shaders contributors. GPL-3.0-or-later.
// The "What's new in X" dialog, opened once after an update with the engine's own changelog section.
import { Btn, arriveAt } from '../ui'
import type { Span } from '../types'

function Spans({ spans }: { spans: Span[] }) {
  return (
    <>
      {spans.map((s, i) =>
        s.kind === 'bold' ? <strong key={i}>{s.text}</strong>
          : s.kind === 'code' ? <code key={i}>{s.text}</code>
            : <span key={i}>{s.text}</span>,
      )}
    </>
  )
}

export function WhatsNewBody({ version, bullets, onClose }: { version: string; bullets: Span[][]; onClose: () => void }) {
  return (
    <>
      <h2 className="s-dialog-title s-arrive">What’s new in {version}</h2>
      <ul className="s-changelog">
        {bullets.map((b, i) => <li key={i} className="s-arrive" style={arriveAt(i + 1)}><Spans spans={b} /></li>)}
      </ul>
      <div className="s-dialog-actions">
        <Btn kind="primary" onClick={onClose}>Close</Btn>
      </div>
    </>
  )
}
