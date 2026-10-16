// Exo Shaders, an unofficial fork based on SCSKiller. Copyright (C) the SCSKiller authors and Exo Shaders contributors. GPL-3.0-or-later.
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import '@fontsource-variable/geist'
import './shaders.css'
import { embedded } from './engine'
import { App } from './App'
import { followExoTextScale } from './textScale'

async function boot() {
  followExoTextScale()
  // Opened on its own in the dev server: a stand-in engine. The shipped build has no mock.
  if (import.meta.env.DEV && !embedded) (await import('./dev/mock')).installMock()
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <App />
    </StrictMode>,
  )
}

void boot()
