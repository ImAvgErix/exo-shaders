// Exo Shaders, an unofficial fork based on SCSKiller. Copyright (C) the SCSKiller authors and Exo Shaders contributors. GPL-3.0-or-later.
// Exo's text size (Settings > General > Text size) as a factor on --exo-scale, which every font size in
// shaders.css multiplies. Outside Exo, or with an Exo that does not answer, the type stays at 1.
import { call, embedded, on } from './engine'

const apply = (value: unknown) => {
  const scale = Number(value)
  if (Number.isFinite(scale) && scale > 0.5 && scale < 2) document.documentElement.style.setProperty('--exo-scale', String(scale))
}

export function followExoTextScale() {
  if (!embedded) return
  call<number>('exo.textScale').then(apply, () => { })
  on('exo.textScale', data => apply((data as { scale?: unknown } | null)?.scale))
}
