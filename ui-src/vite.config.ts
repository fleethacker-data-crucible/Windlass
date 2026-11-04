// Canonical build: `cd ui-src && npm ci && npm run build` -> ../ui/dist. Windlass serves that folder as it is
// (src/Ui.cs); there is no manifest and no service worker, the engine is on this computer already.
import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import tailwindcss from '@tailwindcss/vite';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const outDir = path.resolve(here, '../ui/dist');
const pkg = JSON.parse(readFileSync(path.join(here, 'package.json'), 'utf8')) as { version: string };

export default defineConfig({
  root: here,
  base: './',
  publicDir: 'public',
  plugins: [tailwindcss(), svelte()],
  resolve: {
    alias: {
      $lib: path.join(here, 'src/lib'),
      $components: path.join(here, 'src/components'),
      $views: path.join(here, 'src/views')
    }
  },
  define: { __WINDLASS_UI_VERSION__: JSON.stringify(pkg.version) },
  server: { proxy: { '/api': 'http://127.0.0.1:8777', '/v1': 'http://127.0.0.1:8777' } },
  build: {
    outDir,
    emptyOutDir: true,
    target: ['chrome120', 'edge120', 'firefox121'],
    sourcemap: false,
    modulePreload: { polyfill: false },
    assetsInlineLimit: 0, // no data: URIs; the page's CSP does not allow them
    chunkSizeWarningLimit: 600
  }
});
