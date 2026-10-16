// Exo Shaders, an unofficial fork based on SCSKiller. Copyright (C) the SCSKiller authors and Exo Shaders contributors. GPL-3.0-or-later.
// "Add a game" (exe, then the game folder dialog), "Add a folder of games", and the manual game's folder/rename dialogs.
import { useEffect, useState } from 'react'
import { call, errorText } from '../engine'
import { act } from '../store'
import { Btn, FieldError } from '../ui'

/** Windows' own picker, through Exo: the page can't browse the disk itself. Null when cancelled. */
async function browse(kind: 'program' | 'folder'): Promise<string | null> {
  try { return (await call<string | null>(kind === 'program' ? 'exo.pickProgram' : 'exo.pickFolder')) ?? null } catch { return null }
}

type Preview = {
  game: { id: string; name: string; exe: string; installDir: string; store: string }
  existed: boolean; hidden: boolean; listed: boolean
  message?: string; folderProblem?: string; title: string; text: string
}

const unquote = (s: string) => s.trim().replace(/^"(.*)"$/, '$1')

export function AddGameBody({ onClose, onAdded }: { onClose: () => void; onAdded: (id: string) => void }) {
  const [exe, setExe] = useState('')
  const [preview, setPreview] = useState<Preview | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function next() {
    setError(null)
    try {
      const p = await call<Preview>('games.previewExe', { exe: unquote(exe) })
      if (p?.existed) { setError(p.message ?? 'Already in your library'); return }
      if (!p?.game) { setError('That program couldn’t be read.'); return }
      setPreview(p)
    } catch (e) {
      setError(errorText(e))
    }
  }

  if (preview) {
    return <FolderBody title={preview.title || 'Add a game'} exe={preview.game.exe} initial={preview.game.installDir} initialProblem={preview.folderProblem}
      confirmText="Add game" onClose={onClose}
      onConfirm={async folder => {
        const r = await call<{ id: string }>('games.addExe', { exe: preview.game.exe, folder })
        onClose()
        if (r?.id) onAdded(r.id)
      }} />
  }

  return (
    <>
      <h2 className="s-dialog-title">Add a game</h2>
      <label className="s-field">
        <span className="s-field-label">Game program</span>
        <span className="s-input-row">
          <input className="s-input" autoFocus spellCheck={false} placeholder="C:\Games\Game\Game.exe" value={exe}
            onChange={e => { setExe(e.target.value); setError(null) }} onKeyDown={e => { if (e.key === 'Enter' && exe.trim()) void next() }} />
          <Btn onClick={async () => { const p = await browse('program'); if (p) { setExe(p); setError(null) } }}>Browse</Btn>
        </span>
      </label>
      <FieldError>{error}</FieldError>
      <div className="s-dialog-actions">
        <Btn onClick={onClose}>Cancel</Btn>
        <Btn kind="primary" disabled={!exe.trim()} onClick={next}>Next</Btn>
      </div>
    </>
  )
}

/** The game folder dialog: every pick is checked with games.checkFolder; Add stays off while there's a problem. */
export function FolderBody({ title, exe, initial, initialProblem, confirmText, onConfirm, onClose }: {
  title: string; exe: string; initial: string; initialProblem?: string
  confirmText: string; onConfirm: (folder: string) => Promise<void>; onClose: () => void
}) {
  const [folder, setFolder] = useState(initial)
  const [problem, setProblem] = useState<string | null>(initialProblem ?? null)
  const [checking, setChecking] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let live = true
    setChecking(true)
    const t = window.setTimeout(() => {
      call<{ problem?: string }>('games.checkFolder', { exe, folder: unquote(folder) })
        .then(r => { if (live) setProblem(r?.problem ?? null) })
        .catch(e => { if (live) setProblem(errorText(e)) })
        .finally(() => { if (live) setChecking(false) })
    }, 250)
    return () => { live = false; window.clearTimeout(t) }
  }, [exe, folder])

  return (
    <>
      <h2 className="s-dialog-title">{title}</h2>
      <label className="s-field">
        <span className="s-field-label">Game folder</span>
        <span className="s-input-row">
          <input className="s-input" spellCheck={false} value={folder} onChange={e => setFolder(e.target.value)} />
          <Btn onClick={async () => { const p = await browse('folder'); if (p) setFolder(p) }}>Browse</Btn>
        </span>
      </label>
      <FieldError>{error ?? problem}</FieldError>
      <div className="s-dialog-actions">
        <Btn onClick={onClose}>Cancel</Btn>
        <Btn kind="primary" disabled={!!problem || checking || !folder.trim()} onClick={async () => {
          setError(null)
          try { await onConfirm(unquote(folder)) } catch (e) { setError(errorText(e)) }
        }}>{confirmText}</Btn>
      </div>
    </>
  )
}

export function AddFolderBody({ onClose }: { onClose: () => void }) {
  const [path, setPath] = useState('')
  const [error, setError] = useState<string | null>(null)
  async function add() {
    setError(null)
    try {
      await call('games.addFolder', { path: unquote(path) })
      onClose()
    } catch (e) {
      setError(errorText(e))
    }
  }
  return (
    <>
      <h2 className="s-dialog-title">Add a folder of games</h2>
      <label className="s-field">
        <span className="s-field-label">Folder</span>
        <span className="s-input-row">
          <input className="s-input" autoFocus spellCheck={false} placeholder="D:\Games" value={path}
            onChange={e => { setPath(e.target.value); setError(null) }} onKeyDown={e => { if (e.key === 'Enter' && path.trim()) void add() }} />
          <Btn onClick={async () => { const p = await browse('folder'); if (p) { setPath(p); setError(null) } }}>Browse</Btn>
        </span>
      </label>
      <FieldError>{error}</FieldError>
      <div className="s-dialog-actions">
        <Btn onClick={onClose}>Cancel</Btn>
        <Btn kind="primary" disabled={!path.trim()} onClick={add}>Add folder</Btn>
      </div>
    </>
  )
}

export function RenameBody({ id, name, onClose }: { id: string; name: string; onClose: () => void }) {
  const [value, setValue] = useState(name)
  const save = async () => {
    if (await act('games.rename', { id, name: value.trim() })) onClose()
  }
  return (
    <>
      <h2 className="s-dialog-title">Rename {name}</h2>
      <input className="s-input" aria-label="Name" autoFocus value={value} onChange={e => setValue(e.target.value)} onKeyDown={e => { if (e.key === 'Enter' && value.trim()) void save() }} />
      <div className="s-dialog-actions">
        <Btn onClick={onClose}>Cancel</Btn>
        <Btn kind="primary" disabled={!value.trim() || value.trim() === name} onClick={save}>Rename</Btn>
      </div>
    </>
  )
}

export function AesKeyBody({ id, name, onClose }: { id: string; name: string; onClose: () => void }) {
  const [key, setKey] = useState('')
  const [error, setError] = useState<string | null>(null)
  const valid = /^0x[0-9a-fA-F]{64}$/.test(key.trim())
  async function save() {
    setError(null)
    try {
      await call('game.setAesKey', { id, key: key.trim() })
      onClose()
    } catch (e) {
      setError(errorText(e))
    }
  }
  return (
    <>
      <h2 className="s-dialog-title">Set the AES key of {name}</h2>
      <input className="s-input" autoFocus spellCheck={false} aria-label="AES key" placeholder="0x and 64 hex digits" value={key} onChange={e => { setKey(e.target.value); setError(null) }}
        onKeyDown={e => { if (e.key === 'Enter' && valid) void save() }} />
      <FieldError>{error}</FieldError>
      <div className="s-dialog-actions">
        <Btn onClick={onClose}>Cancel</Btn>
        <Btn kind="primary" disabled={!valid} onClick={save}>Set key</Btn>
      </div>
    </>
  )
}
