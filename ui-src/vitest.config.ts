import { defineConfig } from 'vitest/config';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));

export default defineConfig({
  plugins: [svelte()],
  resolve: { alias: { $lib: path.join(here, 'src/lib') } },
  define: { __WINDLASS_UI_VERSION__: JSON.stringify('test') },
  test: { include: ['src/**/*.test.ts'], environment: 'node' }
});
