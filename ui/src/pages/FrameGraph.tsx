// Exo Shaders, an unofficial fork based on SCSKiller. Copyright (C) the SCSKiller authors and Exo Shaders contributors. GPL-3.0-or-later.
// Frame times of one play (or a before and after pair) as a line: peaks spread evenly over the run, hitches as marks.
import { useLayoutEffect, useRef, useState } from 'react'
import type { Hitch } from '../types'

export type Series = { label: string; durationMs: number; startupMs: number; peaks: number[]; hitches: Hitch[]; tone: 'ink' | 'faint' }

const H = 160
const PAD_L = 36
const PAD_B = 22

/** TimeSpan "hh:mm:ss.fffffff" or a number of ms. */
function hitchAt(h: Hitch): number {
  if (typeof h.atMs === 'number') return h.atMs
  if (typeof h.at === 'number') return h.at
  if (typeof h.at === 'string') {
    const m = /^(?:(\d+)\.)?(\d+):(\d+):(\d+(?:\.\d+)?)$/.exec(h.at)
    if (m) return ((Number(m[1] ?? 0) * 24 + Number(m[2])) * 3600 + Number(m[3]) * 60 + Number(m[4])) * 1000
  }
  return 0
}

const isStutter = (c: string) => /^(shader|other)$/i.test(c)

/** The app's scale: the worst shader/other hitch plus 15%, between 60 and 250 ms. */
function topFor(series: Series[]) {
  const worst = Math.max(50, ...series.flatMap(s => s.hitches.filter(h => isStutter(h.cause)).map(h => h.ms)))
  return Math.min(250, Math.max(60, worst * 1.15))
}

function bucket(peaks: number[], n: number) {
  const out = new Array<number>(n).fill(0)
  if (!peaks.length) return out
  for (let i = 0; i < peaks.length; i++) {
    const j = Math.floor((i * n) / peaks.length)
    out[j] = Math.max(out[j], peaks[i])
  }
  return out
}

const fmtTime = (ms: number) => {
  const s = Math.round(ms / 1000)
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`
}

/**
 * Drawn at the width it's shown at, so the axis text is a real 12px (a fixed viewBox scaled into a 400px column
 * shrank it to about 6px).
 */
export function FrameGraph({ series }: { series: Series[] }) {
  const usable = series.filter(s => s.peaks.length > 0)
  const hasData = usable.length > 0
  const ref = useRef<HTMLElement>(null)
  const [width, setWidth] = useState(0)
  useLayoutEffect(() => {
    const el = ref.current
    if (!el) return
    setWidth(el.clientWidth)
    const ro = new ResizeObserver(([entry]) => setWidth(Math.round(entry.contentRect.width)))
    ro.observe(el)
    return () => ro.disconnect()
  }, [hasData])
  if (!hasData) return null
  return (
    <figure ref={ref} className="s-graph">
      {width > 0 ? <Plot usable={usable} W={width} /> : <div style={{ height: H }} />}
      {usable.length > 1 || usable.some(s => s.hitches.some(h => /shader/i.test(h.cause))) ? (
        <figcaption className="s-graph-legend">
          {usable.map(s => <span key={s.label} className={`s-legend is-${s.tone}`}>{s.label}</span>)}
          {usable.some(s => s.hitches.some(h => /shader/i.test(h.cause))) ? <span className="s-legend is-shader">Shader stutter</span> : null}
        </figcaption>
      ) : null}
    </figure>
  )
}

function Plot({ usable, W }: { usable: Series[]; W: number }) {
  const top = topFor(usable)
  const span = Math.max(1, ...usable.map(s => s.durationMs))
  const plotW = W - PAD_L
  const plotH = H - PAD_B
  const x = (ms: number) => PAD_L + (ms / span) * plotW
  const y = (ms: number) => plotH - (Math.min(ms, top) / top) * (plotH - 6)
  const ticks = [50, 100, 200].filter(t => t < top)
  return (
    <svg width={W} height={H} viewBox={`0 0 ${W} ${H}`} role="img" aria-label={usable.map(s => s.label).join(' and ') + ' frame times'}>
      {ticks.map(t => (
        <g key={t}>
          <line x1={PAD_L} x2={W} y1={y(t)} y2={y(t)} className="s-graph-grid" />
          <text x={PAD_L - 6} y={y(t) + 4} className="s-graph-axis" textAnchor="end">{Math.round(t)}</text>
        </g>
      ))}
      <line x1={PAD_L} x2={W} y1={plotH} y2={plotH} className="s-graph-base" />
      <text x={PAD_L} y={H - 5} className="s-graph-axis">0:00</text>
      <text x={W} y={H - 5} className="s-graph-axis" textAnchor="end">{fmtTime(span)}</text>
      <text x={PAD_L - 6} y={12} className="s-graph-axis" textAnchor="end">ms</text>
      {usable.map(s => s.startupMs > 0 ? (
        <rect key={s.label + 'startup'} x={PAD_L} y={0} width={Math.max(0, x(s.startupMs) - PAD_L)} height={plotH} className="s-graph-startup" />
      ) : null)}
      {usable.map(s => {
        const n = Math.max(2, Math.min(Math.round(plotW * (s.durationMs / span)), s.peaks.length))
        const b = bucket(s.peaks, n)
        const pts = b.map((v, i) => `${(x(((i + 0.5) / n) * s.durationMs)).toFixed(1)},${y(v).toFixed(1)}`).join(' ')
        return <polyline key={s.label} points={pts} className={`s-graph-line is-${s.tone}`} />
      })}
      {usable.map(s => s.hitches.filter(h => isStutter(h.cause)).map((h, i) => (
        <circle key={s.label + i} cx={x(hitchAt(h))} cy={y(h.ms)} r={2.6} className={`s-graph-hitch${/shader/i.test(h.cause) ? ' is-shader' : ''}${s.tone === 'faint' ? ' is-faint' : ''}`} />
      )))}
    </svg>
  )
}
