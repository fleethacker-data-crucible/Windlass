<script lang="ts">
  import { onMount, tick } from 'svelte';
  import { getLogs } from '$lib/api';
  import { copyText } from '$lib/copy';
  import type { LogLine } from '$lib/types';
  import PageHeader from '$components/PageHeader.svelte';
  import Copy from '@lucide/svelte/icons/copy';
  import Eraser from '@lucide/svelte/icons/eraser';

  let lines = $state<LogLine[]>([]);
  let cursor = 0;
  let follow = $state(true);
  let level = $state<'all' | 'warn' | 'error'>('all');
  let box: HTMLElement | undefined = $state();
  let failed = $state(false);

  const LEVEL = /^\S+ \S+ (\w+)\s/;
  const levelOf = (t: string) => LEVEL.exec(t)?.[1] ?? 'INFO';
  const shown = $derived(lines.filter((l) => level === 'all' || (level === 'warn' ? levelOf(l.text) !== 'INFO' && levelOf(l.text) !== 'DEBUG' : levelOf(l.text) === 'ERROR')));

  async function poll() {
    try {
      const r = await getLogs(cursor);
      failed = false;
      if (r.lines.length) {
        cursor = r.last;
        lines = [...lines, ...r.lines].slice(-1000);
        if (follow) {
          await tick();
          box?.scrollTo({ top: box.scrollHeight });
        }
      }
    } catch {
      failed = true;
    }
  }

  onMount(() => {
    void poll();
    const t = setInterval(poll, 2000);
    return () => clearInterval(t);
  });
</script>

<PageHeader title="Log">
  {#snippet actions()}
    <label class="flex items-center gap-2 text-[13px]"><span class="text-muted-foreground">Show</span>
      <select class="fl-input" bind:value={level} aria-label="Show">
        <option value="all">Everything</option><option value="warn">Warnings and errors</option><option value="error">Errors only</option>
      </select>
    </label>
    <label class="flex items-center gap-2 text-[13px]"><input type="checkbox" bind:checked={follow} class="size-4" /> Follow</label>
    <button class="fl-btn fl-btn--secondary" onclick={() => copyText(shown.map((l) => l.text).join('\n'), 'Log copied')} disabled={shown.length === 0}><Copy aria-hidden="true" /> Copy</button>
    <button class="fl-btn fl-btn--secondary" onclick={() => (lines = [])} disabled={lines.length === 0}><Eraser aria-hidden="true" /> Clear view</button>
  {/snippet}
</PageHeader>

<div class="flex min-h-0 flex-1 flex-col p-6">
  <p class="mb-3 text-[13px] text-muted-foreground">Request ids, token counts and timings. Windlass never logs prompts or replies. The same lines go to the console and to the file in <code class="font-mono">logs\</code>.{#if failed} <span class="text-danger">The server did not answer; trying again.</span>{/if}</p>
  <div bind:this={box} class="min-h-0 flex-1 overflow-auto rounded-lg border border-border bg-background p-3 font-mono text-[12px] leading-5" data-testid="log-box" role="log" aria-label="Server log">
    {#each shown as l (l.seq)}
      {@const lv = levelOf(l.text)}
      <div class="whitespace-pre-wrap break-words {lv === 'ERROR' ? 'text-danger' : lv === 'WARN' ? 'text-warning' : ''}">{l.text}</div>
    {:else}
      <div class="text-muted-foreground">Nothing logged yet.</div>
    {/each}
  </div>
</div>
