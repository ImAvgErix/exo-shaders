// Exo Shaders, an unofficial fork based on SCSKiller. Copyright (C) the SCSKiller authors and Exo Shaders contributors. GPL-3.0-or-later.
// About: the version and the licence credit to SCSKiller (a GPL requirement), plus hidden games and added folders
// when there are any, since this is the only place to bring them back.
import { useId, type ReactNode } from 'react'
import { act, useStore } from '../store'
import { Btn, Row, Value, arriveAt, confirm } from '../ui'

function Card({ title, i, children }: { title: string; i: number; children: ReactNode }) {
  const id = useId()
  return (
    <section className="s-info-card s-arrive" style={arriveAt(i)} aria-labelledby={id}>
      <h3 id={id} className="s-info-title">{title}</h3>
      {children}
    </section>
  )
}

export function AboutPage() {
  const app = useStore(s => s.snapshot!.app)
  const hidden = useStore(s => s.snapshot!.hidden)
  const manualFolders = useStore(s => s.snapshot!.manualFolders)

  return (
    <div className="s-scroll s-about-page">
      <Card title="Exo Shaders" i={0}>
        <Row label="Version"><Value>{app.version ?? '—'}</Value></Row>
        <Row label="Based on"><Value>{app.engineVersion ? `SCSKiller ${app.engineVersion}` : 'SCSKiller'}</Value></Row>
        <Row label="An unofficial fork of SCSKiller by the SCSKiller authors"><Value>GPL-3.0</Value></Row>
      </Card>

      {hidden.length > 0 ? (
        <Card title="Hidden games" i={1}>
          {hidden.map(h => (
            <Row key={h.id} label={h.name}>
              <Btn compact onClick={() => act('games.show', { id: h.id })}>Show</Btn>
            </Row>
          ))}
        </Card>
      ) : null}

      {manualFolders.length > 0 ? (
        <Card title="Added folders" i={2}>
          {manualFolders.map(f => (
            <Row key={f} label={f}>
              <Btn compact onClick={async () => {
                if (await confirm('Stop looking for games here?', f, 'Stop looking here')) await act('games.removeFolder', { path: f })
              }}>Remove</Btn>
            </Row>
          ))}
        </Card>
      ) : null}
    </div>
  )
}
