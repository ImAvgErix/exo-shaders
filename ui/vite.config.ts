// Exo Shaders, an unofficial fork based on SCSKiller. Copyright (C) the SCSKiller authors and Exo Shaders contributors. GPL-3.0-or-later.
import { defineConfig, type Plugin } from 'vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'

const root = dirname(fileURLToPath(import.meta.url))

// The dev server's hot reload needs an inline script the shipped CSP forbids, so only `vite` (serve) drops it.
const devCsp: Plugin = {
  name: 'exo-shaders-dev-csp',
  apply: 'serve',
  transformIndexHtml: html => html.replace(/<meta\s+http-equiv="Content-Security-Policy"[\s\S]*?\/>/, ''),
}

// Served from https://shaders.exo-launcher.local/index.html in a frame in Exo's Shaders sheet.
export default defineConfig({
  plugins: [react(), devCsp],
  base: './',
  build: {
    outDir: resolve(root, 'dist'),
    emptyOutDir: true,
    sourcemap: false,
  },
})
