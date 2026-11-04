<script lang="ts">
  // Fleet status bar (UNIFIED-UI.md section 5): engine state (a dot plus a word), the bound address, session state, version.
  import { session } from '$lib/session.svelte';

  const word = $derived(session.engine === 'running' ? 'Running' : session.engine === 'stopping' ? 'Stopping' : 'Disconnected');
  const dot = $derived(session.engine === 'running' ? 'bg-success' : session.engine === 'stopping' ? 'bg-warning' : 'bg-danger');
  const s = $derived(session.status);
  const model = $derived(!s ? '' : s.state === 'ready' ? (s.busy ? `Generating${s.queued > 0 ? ` · ${s.queued} waiting` : ''}` : 'Model ready') : s.state === 'loading' ? 'Loading model' : 'Model failed to load');
  const address = $derived(s?.address ?? `127.0.0.1:${typeof location !== 'undefined' ? location.port : ''}`);
</script>

<footer class="flex shrink-0 items-center gap-4 border-t border-border bg-sidebar px-3 text-[12px] text-muted-foreground h-[var(--fl-statusbar-height)]" aria-label="Status" data-testid="status-bar">
  <span class="inline-flex items-center gap-1.5"><span class="size-2 rounded-full {dot}" aria-hidden="true"></span><span data-testid="status-engine">{word}</span></span>
  <span class="font-mono" title="Windlass listens on this computer only" data-testid="status-address">{address}</span>
  {#if model}<span data-testid="status-session">{model}</span>{/if}
  <span class="ml-auto font-mono">Windlass {s ? `v${s.version}` : ''}</span>
</footer>
