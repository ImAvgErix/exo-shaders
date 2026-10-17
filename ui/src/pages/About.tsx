// Exo Shaders, an unofficial fork based on SCSKiller. Copyright (C) the SCSKiller authors and Exo Shaders contributors. GPL-3.0-or-later.
// The toolbar's info button: one dialog with the shader cache, then About (the version and the license credit to
// SCSKiller, a GPL requirement), hidden games and added folders, since this is the only place to bring them back.
import { act, useStore } from '../store'
import { Btn, InfoBtn, Panel, Row, Value, confirm, openDialog } from '../ui'
import { CacheSection, readInventory } from './Cache'
import type { Inventory } from '../types'

/** The toolbar's "i": shader cache and about, in one dialog that opens with the caches already read. */
export function InfoButton() {
  const open = async () => {
    const inventory = await readInventory().catch(() => null)
    void openDialog(close => <InfoBody inventory={inventory} onClose={close} />, { wide: true })
  }
  return <InfoBtn label="Shader cache and about" onClick={() => void open()} />
}

function InfoBody({ inventory, onClose }: { inventory: Inventory | null; onClose: () => void }) {
  const app = useStore(s => s.snapshot!.app)
  const hidden = useStore(s => s.snapshot!.hidden)
  const manualFolders = useStore(s => s.snapshot!.manualFolders)

  return (
    <>
      <h2 className="s-dialog-title">Exo Shaders</h2>
      <CacheSection initial={inventory} />
      <Panel title="About" i={1}>
        <Row label="Version"><Value>{app.version ?? '—'}</Value></Row>
        <Row label="Based on"><Value>{app.engineVersion ? `SCSKiller ${app.engineVersion}` : 'SCSKiller'}</Value></Row>
        <Row label="An unofficial fork of SCSKiller by the SCSKiller authors"><Value>GPL-3.0</Value></Row>
      </Panel>

      {hidden.length > 0 ? (
        <Panel title="Hidden games" i={2}>
          {hidden.map(h => (
            <Row key={h.id} label={h.name}>
              <Btn compact onClick={() => act('games.show', { id: h.id })}>Show</Btn>
            </Row>
          ))}
        </Panel>
      ) : null}

      {manualFolders.length > 0 ? (
        <Panel title="Added folders" i={3}>
          {manualFolders.map(f => (
            <Row key={f} label={f}>
              <Btn compact onClick={async () => {
                if (await confirm('Stop looking for games here?', f, 'Stop looking here')) await act('games.removeFolder', { path: f })
              }}>Remove</Btn>
            </Row>
          ))}
        </Panel>
      ) : null}

      <div className="s-dialog-actions">
        <Btn kind="primary" onClick={onClose}>Done</Btn>
      </div>
    </>
  )
}
