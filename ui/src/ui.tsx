// Exo Shaders, an unofficial fork based on SCSKiller. Copyright (C) the SCSKiller authors and Exo Shaders contributors. GPL-3.0-or-later.
// Exo's vocabulary: rows, pills, hairlines, one primary per area, and the dialogs over the page.
import { Component, useEffect, useId, useLayoutEffect, useRef, useState, useSyncExternalStore, type CSSProperties, type KeyboardEvent as ReactKeyboardEvent, type MouseEvent, type ReactNode } from 'react'

/** Arrival order for `.s-arrive`: the CSS staggers by --i and caps it, so pass the plain index. */
export const arriveAt = (i: number) => ({ '--i': i }) as CSSProperties

export function Panel({ title, i = 0, children }: { title: string; i?: number; children: ReactNode }) {
  const id = useId()
  return (
    <section className="s-panel s-arrive" style={arriveAt(i)} aria-labelledby={id}>
      <h3 id={id} className="s-panel-title">{title}</h3>
      {children}
    </section>
  )
}

/** Label left, value or control on the shared right edge; a #161616 hairline between rows. `error` is a red second line. */
export function Row({ label, error, children }: { label: ReactNode; error?: ReactNode; children?: ReactNode }) {
  return (
    <div className="s-row">
      <span className="s-row-label">
        <span>{label}</span>
        {error ? <span className="s-row-error">{error}</span> : null}
      </span>
      {children !== undefined ? <span className="s-row-control">{children}</span> : null}
    </div>
  )
}

/** A row's plain value: 12px, muted, tabular figures. */
export function Value({ children, tone }: { children: ReactNode; tone?: 'good' | 'bad' }) {
  return <span className={`s-value${tone ? ` is-${tone}` : ''}`}>{children}</span>
}

/**
 * Exo's glide (Exo's lib/glide.ts): the chosen item's fill slides to it on
 * the 200 ms switch timing instead of jumping. The first placement does not
 * animate; a group mounted hidden arms on its first visible placement.
 */
function useGlide<E extends HTMLElement>() {
  const ref = useRef<E>(null)
  useLayoutEffect(() => {
    const box = ref.current
    if (!box) return
    let ready = 0
    const place = () => {
      const on = box.querySelector<HTMLElement>('[aria-checked="true"]')
      if (!on || on.offsetWidth === 0) { box.style.setProperty('--glide-o', '0'); return }
      box.style.setProperty('--glide-x', `${on.offsetLeft}px`)
      box.style.setProperty('--glide-y', `${on.offsetTop}px`)
      box.style.setProperty('--glide-w', `${on.offsetWidth}px`)
      box.style.setProperty('--glide-h', `${on.offsetHeight}px`)
      box.style.setProperty('--glide-o', '1')
      if (!ready) ready = requestAnimationFrame(() => box.classList.add('is-glide-ready'))
    }
    place()
    const attributes = new MutationObserver(place)
    attributes.observe(box, { subtree: true, childList: true, attributes: true, attributeFilter: ['aria-checked'] })
    const sizes = new ResizeObserver(place)
    sizes.observe(box)
    for (const child of box.querySelectorAll('button')) sizes.observe(child)
    return () => { cancelAnimationFrame(ready); attributes.disconnect(); sizes.disconnect() }
  }, [])
  return ref
}

export function Segmented<T extends string | number>({ label, options, value, onChange, disabled = false }: {
  label: string
  options: readonly { value: T; label: ReactNode }[]
  value: T | null | undefined
  onChange: (value: T) => void
  disabled?: boolean
}) {
  const glide = useGlide<HTMLSpanElement>()
  return (
    <span ref={glide} className="s-seg" role="radiogroup" aria-label={label}>
      {options.map(o => (
        <button key={String(o.value)} type="button" role="radio" aria-checked={o.value === value}
          className={`s-seg-item${o.value === value ? ' is-on' : ''}`} disabled={disabled}
          onClick={() => { if (o.value !== value) onChange(o.value) }}>
          {o.label}
        </button>
      ))}
    </span>
  )
}

/**
 * Exo's menu (LibraryShelf.tsx Menu): a pill trigger over a native popover in the top layer, anchored under it,
 * light dismiss, arrow keys, a check on the chosen item. No native select.
 */
export function Select<T extends string | number>({ label, options, value, onChange, disabled = false }: {
  label: string
  options: readonly { value: T; label: string }[]
  value: T
  onChange: (v: T) => void
  disabled?: boolean
}) {
  const id = useId()
  const anchor = `--s-menu-${id.replace(/[^a-zA-Z0-9]/g, '')}`
  const [open, setOpen] = useState(false)
  const triggerRef = useRef<HTMLButtonElement>(null)
  const menuRef = useRef<HTMLDivElement>(null)
  const entries = () => Array.from(menuRef.current?.querySelectorAll<HTMLElement>('[role="menuitemradio"]') ?? [])
  const current = options.find(o => o.value === value)
  function hide(returnFocus: boolean) {
    if (menuRef.current?.matches(':popover-open')) menuRef.current.hidePopover()
    if (returnFocus) triggerRef.current?.focus()
  }
  function choose(v: T) {
    hide(true)
    if (v !== value) onChange(v)
  }
  function onKeyDown(e: ReactKeyboardEvent<HTMLDivElement>) {
    const list = entries()
    const at = list.indexOf(document.activeElement as HTMLElement)
    const to = ({ ArrowDown: at + 1, ArrowUp: at - 1, Home: 0, End: list.length - 1 } as Record<string, number | undefined>)[e.key]
    if (to !== undefined) {
      e.preventDefault()
      list[(to + list.length) % list.length]?.focus()
    } else if (e.key === 'Escape') {
      e.preventDefault()
      e.stopPropagation()
      hide(true)
    } else if (e.key === 'Tab') hide(false)
  }
  return (
    <>
      <button ref={triggerRef} type="button" className="s-select" style={{ anchorName: anchor } as CSSProperties}
        aria-label={label} aria-haspopup="menu" aria-expanded={open} aria-controls={id} popoverTarget={id} disabled={disabled}>
        <span className="s-select-text">{current?.label ?? label}</span>
        <svg className="s-select-chevron" width="12" height="12" viewBox="0 0 12 12" aria-hidden="true">
          <path d="M2.5 4.5 6 8l3.5-3.5" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </button>
      <div ref={menuRef} id={id} popover="auto" role="menu" aria-label={label} className="s-menu"
        style={{ positionAnchor: anchor } as CSSProperties}
        onToggle={e => {
          const opened = (e as unknown as ToggleEvent).newState === 'open'
          setOpen(opened)
          if (opened) (entries().find(x => x.ariaChecked === 'true') ?? entries()[0])?.focus()
        }}
        onKeyDown={onKeyDown}>
        {options.map(o => (
          <div key={String(o.value)} role="menuitemradio" aria-checked={o.value === value} tabIndex={-1} className="s-menu-item"
            onClick={() => choose(o.value)}
            onKeyDown={e => {
              if (e.key !== 'Enter' && e.key !== ' ') return
              e.preventDefault()
              choose(o.value)
            }}>
            <span className="s-menu-label">{o.label}</span>
            <svg className="s-menu-check" width="14" height="14" viewBox="0 0 14 14" aria-hidden="true">
              <path d="M3 7.2 5.8 10 11 4.2" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          </div>
        ))}
      </div>
    </>
  )
}

/** A checkbox in Exo's look: a hairline square with a check, the label beside it. No native checkbox. */
export function Check({ checked, onChange, children }: { checked: boolean; onChange: (on: boolean) => void; children: ReactNode }) {
  return (
    <button type="button" role="checkbox" aria-checked={checked} className={`s-check${checked ? ' is-on' : ''}`} onClick={() => onChange(!checked)}>
      <span className="s-check-box" aria-hidden="true">
        <svg width="12" height="12" viewBox="0 0 14 14">
          <path d="M3 7.2 5.8 10 11 4.2" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </span>
      <span className="s-check-label">{children}</span>
    </button>
  )
}

type BtnKind = 'primary' | 'danger' | 'plain'

/** How long a button says it's done ("Cleared" with a check) before it goes back to its label. */
const DONE_MS = 1600

/**
 * Every label a button can show sits in one grid cell, so the button is always as wide as its widest label: it never
 * changes width or goes blank. `faces[on]` is the one shown; `reserve` adds labels that only hold width (the other
 * state of a button whose label follows the data, like Pause and Resume).
 */
function Faces({ faces, on, reserve }: { faces: ReactNode[]; on: number; reserve?: ReactNode[] }) {
  return (
    <span className="s-faces">
      {faces.map((f, i) => <span key={i} className="s-face" data-on={i === on}>{f}</span>)}
      {reserve?.map((f, i) => <span key={'r' + i} className="s-face" data-on={false} aria-hidden="true">{f}</span>)}
    </span>
  )
}

function DoneMark() {
  return (
    <svg className="s-done-mark" width="12" height="12" viewBox="0 0 14 14" aria-hidden="true">
      <path d="M3 7.2 5.8 10 11 4.2" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  )
}

/**
 * A 32px black pill with a hairline; `primary` is the cream pill, one per area. An async onClick holds the button
 * (dimmed, same label, or `busyText` with a spinner); when it resolves to something truthy and `doneText` is set, the
 * button says so with a green check for 1.6s.
 */
export function Btn({ children, onClick, kind = 'plain', disabled = false, busy = false, compact = false, label, busyText, doneText, reserve }: {
  children: ReactNode; onClick?: (e: MouseEvent) => unknown; kind?: BtnKind; disabled?: boolean; busy?: boolean; compact?: boolean; label?: string
  busyText?: string; doneText?: string; reserve?: ReactNode[]
}) {
  const [phase, setPhase] = useState<'idle' | 'running' | 'done'>('idle')
  const live = useRef(true)
  const timer = useRef<number | undefined>(undefined)
  // Set on every mount: StrictMode mounts twice, and a stale false would hold the button disabled after its first click.
  useEffect(() => { live.current = true; return () => { live.current = false; window.clearTimeout(timer.current) } }, [])
  const click = async (e: MouseEvent) => {
    if (!onClick) return
    const r = onClick(e)
    if (!r || typeof (r as Promise<unknown>).then !== 'function') return
    setPhase('running')
    let ok: unknown = false
    try { ok = await r } catch { ok = false }
    if (!live.current) return
    if (ok && doneText) {
      setPhase('done')
      timer.current = window.setTimeout(() => { if (live.current) setPhase('idle') }, DONE_MS)
    } else setPhase('idle')
  }
  const working = busy || phase === 'running'
  const faces: ReactNode[] = [children]
  if (busyText) faces.push(<><Spinner small />{busyText}</>)
  if (doneText) faces.push(<><DoneMark />{doneText}</>)
  const on = phase === 'done' ? faces.length - 1 : working && busyText ? 1 : 0
  return (
    <button type="button" aria-label={label} aria-busy={working || undefined}
      className={`s-btn${kind !== 'plain' ? ` is-${kind}` : ''}${compact ? ' is-compact' : ''}${working && !busyText ? ' is-busy' : ''}${phase === 'done' ? ' is-done' : ''}`}
      disabled={disabled || working || phase === 'done'} onClick={e => void click(e)}>
      <Faces faces={faces} on={on} reserve={reserve} />
    </button>
  )
}

/** A quiet text button; `reserve` holds the width of its other label (Show and Hide). */
export function LinkBtn({ children, onClick, disabled = false, reserve }: { children: ReactNode; onClick: () => void; disabled?: boolean; reserve?: ReactNode[] }) {
  return (
    <button type="button" className="s-link" disabled={disabled} onClick={onClick}>
      {reserve ? <Faces faces={[children]} on={0} reserve={reserve} /> : children}
    </button>
  )
}

/** A 32px round icon button: close, back. */
function IconBtn({ label, onClick, children }: { label: string; onClick: () => void; children: ReactNode }) {
  return (
    <button type="button" className="s-icon-btn" aria-label={label} onClick={onClick}>
      <svg width="16" height="16" viewBox="0 0 24 24" fill="none" aria-hidden="true">{children}</svg>
    </button>
  )
}

export function InfoBtn({ label, onClick }: { label: string; onClick: () => void }) {
  return (
    <IconBtn label={label} onClick={onClick}>
      <circle cx="12" cy="12" r="9" stroke="currentColor" strokeWidth="1.8" />
      <path d="M12 11v5.5M12 7.6v.2" stroke="currentColor" strokeWidth="2" strokeLinecap="round" />
    </IconBtn>
  )
}

export function CloseBtn({ label, onClick }: { label: string; onClick: () => void }) {
  return <IconBtn label={label} onClick={onClick}><path d="M18 6 6 18M6 6l12 12" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" /></IconBtn>
}

export function BackBtn({ label, onClick }: { label: string; onClick: () => void }) {
  return <IconBtn label={label} onClick={onClick}><path d="M15 5.5 8.5 12l6.5 6.5" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" /></IconBtn>
}

export function Bar({ percent, tone }: { percent?: number; tone?: 'bad' | 'good' }) {
  const indeterminate = percent == null
  return (
    <span className={`s-bar${indeterminate ? ' is-indeterminate' : ''}${tone ? ` is-${tone}` : ''}`}>
      <span style={indeterminate ? undefined : { width: `${Math.max(0, Math.min(100, percent))}%` }} />
    </span>
  )
}

export function Empty({ title, line, children }: { title: string; line?: ReactNode; children?: ReactNode }) {
  return (
    <div className="s-empty">
      <p className="s-empty-title">{title}</p>
      {line ? <p className="s-empty-line">{line}</p> : null}
      {children}
    </div>
  )
}

export function Spinner({ small = false }: { small?: boolean }) {
  return <span className={`s-spinner${small ? ' is-small' : ''}`} aria-hidden="true" />
}

/** A dialog form's error line. It's always there (empty when fine) so the dialog doesn't grow when one shows. */
export function FieldError({ children }: { children?: ReactNode }) {
  return <p className="s-field-error" role={children ? 'alert' : undefined}>{children}</p>
}

// ------------------------------------------------------------------ dialogs

export type AskButton = { label: string; value: string; kind?: BtnKind }
type Layer = { id: number; render: (close: () => void) => ReactNode; onClose: () => void; wide?: boolean }

let layers: Layer[] = []
let layerSeq = 0
const layerSubs = new Set<() => void>()
const emit = () => layerSubs.forEach(fn => fn())

/** Opens a dialog over the page; resolves when it closes. */
export function openDialog(render: (close: () => void) => ReactNode, opts: { wide?: boolean } = {}): Promise<void> {
  return new Promise(resolve => {
    const id = ++layerSeq
    const close = () => {
      layers = layers.filter(l => l.id !== id)
      emit()
      resolve()
    }
    layers = [...layers, { id, render, onClose: close, wide: opts.wide }]
    emit()
  })
}

/** The app's confirm dialog: a title, its text, buttons and an optional checkbox. `value` is null when dismissed. */
export function ask(spec: { title: string; text?: ReactNode | ((checked: boolean) => ReactNode); buttons: AskButton[]; checkbox?: string; checkboxOn?: boolean; cancel?: string }): Promise<{ value: string | null; checked: boolean }> {
  let result: { value: string | null; checked: boolean } = { value: null, checked: !!spec.checkboxOn }
  return openDialog(close => (
    <AskBody spec={spec} onDone={(value, checked) => { result = { value, checked }; close() }} />
  )).then(() => result)
}

/** Shorthand: one confirm button; true when it was pressed. */
export async function confirm(title: string, text: ReactNode, confirmText: string, danger = false): Promise<boolean> {
  const r = await ask({ title, text, buttons: [{ label: confirmText, value: 'ok', kind: danger ? 'danger' : 'primary' }] })
  return r.value === 'ok'
}

function AskBody({ spec, onDone }: { spec: Parameters<typeof ask>[0]; onDone: (value: string | null, checked: boolean) => void }) {
  const [checked, setChecked] = useState(!!spec.checkboxOn)
  const text = typeof spec.text === 'function' ? spec.text(checked) : spec.text
  return (
    <>
      <h2 className="s-dialog-title">{spec.title}</h2>
      {text ? <div className="s-dialog-text">{typeof text === 'string' ? text.split('\n\n').map((p, i) => <p key={i}>{p}</p>) : text}</div> : null}
      {spec.checkbox ? <Check checked={checked} onChange={setChecked}>{spec.checkbox}</Check> : null}
      <div className="s-dialog-actions">
        <Btn onClick={() => onDone(null, checked)}>{spec.cancel ?? 'Cancel'}</Btn>
        {spec.buttons.map(b => <Btn key={b.value} kind={b.kind} onClick={() => onDone(b.value, checked)}>{b.label}</Btn>)}
      </div>
    </>
  )
}

export function DialogHost() {
  const list = useSyncExternalStore(fn => { layerSubs.add(fn); return () => { layerSubs.delete(fn) } }, () => layers)
  useEffect(() => {
    if (!list.length) return
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.stopPropagation(); list[list.length - 1].onClose() } }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [list])
  return (
    <>
      {list.map(l => (
        <div key={l.id} className="s-scrim" onMouseDown={e => { if (e.target === e.currentTarget) l.onClose() }}>
          <div className={`s-dialog${l.wide ? ' is-wide' : ''}`} role="dialog" aria-modal="true">{l.render(l.onClose)}</div>
        </div>
      ))}
    </>
  )
}

/** A page that throws shows one line and a way back instead of a blank frame. */
export class Boundary extends Component<{ children: ReactNode; onReset?: () => void }, { error: string | null }> {
  state = { error: null as string | null }
  static getDerivedStateFromError(e: unknown) { return { error: e instanceof Error ? e.message : String(e) } }
  render() {
    if (!this.state.error) return this.props.children
    return (
      <Empty title="This page couldn’t be shown" line={this.state.error}>
        <Btn kind="primary" onClick={() => { this.setState({ error: null }); this.props.onReset?.() }}>Try again</Btn>
      </Empty>
    )
  }
}
