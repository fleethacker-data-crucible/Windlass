<script lang="ts">
  import { onMount } from 'svelte';
  import { getPlaybook } from '$lib/api';
  import { toast } from '$lib/toast.svelte';
  import { copyText } from '$lib/copy';
  import { formatDuration, formatNumber, formatPercent, formatSecPerToken, formatTime } from '$lib/format';
  import type { PlaybookDoc } from '$lib/types';
  import PageHeader from '$components/PageHeader.svelte';
  import RefreshCw from '@lucide/svelte/icons/refresh-cw';
  import Copy from '@lucide/svelte/icons/copy';

  let pb = $state<PlaybookDoc | null>(null);
  let loaded = $state(false);
  let busy = $state(false);

  async function load() {
    busy = true;
    try {
      pb = await getPlaybook();
    } catch {
      toast.error('The playbook could not be read.');
    } finally {
      loaded = true;
      busy = false;
    }
  }
  onMount(load);
  const acceptRate = $derived(pb && pb.observed.guessed > 0 ? pb.observed.accepted / pb.observed.guessed : null);
</script>

<PageHeader title="Playbook">
  {#snippet actions()}
    <button class="fl-btn fl-btn--secondary" onclick={load} disabled={busy}><RefreshCw aria-hidden="true" /> Refresh</button>
  {/snippet}
</PageHeader>

<div class="min-h-0 flex-1 overflow-y-auto">
  <div class="mx-auto grid max-w-[1120px] gap-4 p-6">
    <p class="max-w-[72ch] text-[13px] text-muted-foreground">
      The playbook is what Windlass learned about this computer and this model: it picks the context, prefetch, how much of the model to keep in RAM and the thread count, and says why. Measuring is a command, because it takes a while and reads the whole model from disk.
    </p>
    {#if !loaded}
      <div class="fl-skeleton h-40" aria-busy="true"></div>
    {:else if !pb}
      <section class="fl-card fl-empty py-10" data-testid="playbook-empty">
        <h2>No playbook</h2>
        <p class="max-w-[52ch] text-[13px] text-muted-foreground">This server runs without one: it was started with <code class="font-mono">--no-playbook</code>, or it is a stub. Start it with a model and without that option to get one.</p>
      </section>
    {:else}
      <div class="grid gap-4 md:grid-cols-2">
        <section class="fl-card" aria-labelledby="p-set">
          <h2 id="p-set" class="mb-3 text-[16px] font-semibold">Settings in use</h2>
          <table class="fl-table" data-testid="playbook-settings">
            <tbody>
              <tr><td>Context</td><td class="num">{formatNumber(pb.settings.contextLength)} tokens</td></tr>
              <tr><td>Prefetch</td><td class="num">{pb.settings.prefetch ? 'On' : 'Off'}</td></tr>
              <tr><td>Kept in RAM</td><td class="num">{pb.settings.cacheGB.toFixed(1)} GB</td></tr>
              <tr><td>Threads</td><td class="num">{pb.settings.threads}</td></tr>
              <tr><td>Guess-ahead</td><td class="num">{pb.settings.speculate ? 'On' : 'Off'}</td></tr>
              <tr><td>Predicted per token</td><td class="num">{formatSecPerToken(pb.settings.predictedSecPerToken)}</td></tr>
              <tr><td>Limited by</td><td class="num">{pb.settings.limit === 'disk' ? 'Disk reads' : pb.settings.limit === 'cpu' ? 'CPU' : '—'}</td></tr>
            </tbody>
          </table>
        </section>

        <section class="fl-card" aria-labelledby="p-mach">
          <h2 id="p-mach" class="mb-3 text-[16px] font-semibold">This computer</h2>
          <table class="fl-table">
            <tbody>
              <tr><td>Processor</td><td class="num">{pb.machine.cpu || '—'}</td></tr>
              <tr><td>Cores</td><td class="num">{pb.machine.physicalCores} cores, {pb.machine.logicalCores} threads</td></tr>
              <tr><td>RAM</td><td class="num">{pb.machine.totalRamGB} GB, {pb.machine.freeRamGB} GB free at start</td></tr>
              <tr><td>Power</td><td class="num">{pb.machine.hasBattery ? (pb.machine.onBattery ? 'On battery' : 'Plugged in') : 'Mains'}</td></tr>
              <tr><td>Graphics</td><td class="num">{pb.machine.gpus.length ? pb.machine.gpus.join(', ') : '—'}</td></tr>
            </tbody>
          </table>
          <p class="mt-2 text-[12px] text-muted-foreground">Windlass computes on the CPU; graphics are listed, not used.</p>
        </section>

        <section class="fl-card" aria-labelledby="p-meas">
          <h2 id="p-meas" class="mb-3 text-[16px] font-semibold">Measured by playbook probe</h2>
          {#if pb.measured.readGBps > 0}
            <table class="fl-table">
              <tbody>
                <tr><td>Read speed</td><td class="num">{pb.measured.readGBps.toFixed(1)} GB/s</td></tr>
                <tr><td>One layer, physical cores</td><td class="num">{formatDuration(pb.measured.layerSecPhysical)}</td></tr>
                <tr><td>One layer, all threads</td><td class="num">{formatDuration(pb.measured.layerSecLogical)}</td></tr>
              </tbody>
            </table>
          {:else}
            <p class="text-[13px] text-muted-foreground">Not measured yet: — <br />Run <code class="font-mono">Launch-Windlass.bat playbook probe --model &lt;file&gt;</code>.</p>
          {/if}
        </section>

        <section class="fl-card" aria-labelledby="p-obs">
          <h2 id="p-obs" class="mb-3 text-[16px] font-semibold">Learned from real replies</h2>
          {#if pb.observed.runs > 0}
            <table class="fl-table">
              <tbody>
                <tr><td>Replies</td><td class="num">{formatNumber(pb.observed.runs)}</td></tr>
                <tr><td>Reply tokens</td><td class="num">{formatNumber(pb.observed.tokens)} in {formatDuration(pb.observed.seconds)}</td></tr>
                <tr><td>Prompt tokens</td><td class="num">{formatNumber(pb.observed.promptTokens)} in {formatDuration(pb.observed.promptSeconds)}</td></tr>
                <tr><td>Guesses kept</td><td class="num">{acceptRate == null ? '—' : `${formatPercent(acceptRate)} of ${formatNumber(pb.observed.guessed)}`}</td></tr>
              </tbody>
            </table>
          {:else}
            <p class="text-[13px] text-muted-foreground">No replies recorded yet: —</p>
          {/if}
        </section>
      </div>

      <section class="fl-card" aria-labelledby="p-rules">
        <h2 id="p-rules" class="mb-1 text-[16px] font-semibold">Why these settings</h2>
        <p class="mb-3 text-[12px] text-muted-foreground">Playbook made {formatTime(pb.created)} for {pb.model}.</p>
        <ul class="grid list-disc gap-1.5 pl-5 text-[13px]" data-testid="playbook-rules">{#each pb.rules as r, i (i)}<li>{r}</li>{/each}</ul>
        <div class="mt-4">
          <button class="fl-btn fl-btn--secondary" onclick={() => copyText(JSON.stringify(pb, null, 2), 'Playbook JSON copied')}><Copy aria-hidden="true" /> Copy as JSON</button>
        </div>
      </section>
    {/if}
  </div>
</div>
