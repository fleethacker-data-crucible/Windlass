<script lang="ts">
  import { session } from '$lib/session.svelte';
  import { copyText } from '$lib/copy';
  import { DASH, formatBytes, formatDuration, formatNumber, formatSecPerToken, formatTokensPerSecond, formatPercent, formatTime } from '$lib/format';
  import PageHeader from '$components/PageHeader.svelte';
  import Stat from '$components/Stat.svelte';
  import Copy from '@lucide/svelte/icons/copy';
  import CircleCheck from '@lucide/svelte/icons/circle-check';
  import CircleX from '@lucide/svelte/icons/circle-x';
  import Loader from '@lucide/svelte/icons/loader';

  const s = $derived(session.status);
  const w = $derived(s?.windlass);
  const probeHint = 'Run Launch-Windlass.bat playbook probe --model <file>';
  const evalHint = 'Run Launch-Windlass.bat playbook eval --model <file>';
  const fidelityHint = 'Run Launch-Windlass.bat playbook fidelity --model <file>';
  const base = $derived(s ? `http://${s.address}/v1` : '');
  const sourceLabel: Record<string, string> = { 'real-runs': 'averaged over real replies on this computer', probe: 'predicted by playbook probe', eval: 'measured by playbook eval', none: '' };
  const fid = $derived(w?.fidelity);
</script>

<PageHeader title="Overview" />
<div class="min-h-0 flex-1 overflow-y-auto">
  {#if !s}
    <div class="mx-auto grid max-w-[1120px] gap-4 p-6" aria-busy="true"><div class="fl-skeleton h-20"></div><div class="fl-skeleton h-40"></div></div>
  {:else}
    <div class="mx-auto grid max-w-[1120px] gap-4 p-6">
      <section class="fl-card flex flex-wrap items-center gap-4" aria-label="Model state" data-testid="state-card">
        {#if s.state === 'ready'}
          <span class="fl-badge fl-badge--success"><CircleCheck aria-hidden="true" /> Ready</span>
        {:else if s.state === 'loading'}
          <span class="fl-badge fl-badge--info"><Loader aria-hidden="true" class="wl-pulse" /> Loading</span>
        {:else}
          <span class="fl-badge fl-badge--danger"><CircleX aria-hidden="true" /> Failed</span>
        {/if}
        <div class="min-w-0 flex-1">
          <div class="truncate text-[16px] font-semibold" data-testid="model-id">{s.model.id}</div>
          <div class="text-[13px] text-muted-foreground">
            {#if s.state === 'ready'}{s.busy ? `Writing a reply${s.queued > 0 ? `, ${s.queued} waiting` : ''}.` : 'Idle. Waiting for a request.'}
            {:else if s.state === 'loading'}Reading the model's header and tables. Requests get a 503 until it is ready.
            {:else}{s.loadError ?? 'The model did not load.'} See the Log page.{/if}
            {#if s.stub} This is a stub server: a canned reply, no model.{/if}
          </div>
        </div>
        <div class="text-[12px] text-muted-foreground">Up {formatDuration(s.uptimeSeconds)}</div>
      </section>

      <div class="grid gap-4 md:grid-cols-2">
        <section class="fl-card" aria-labelledby="h-model">
          <h2 id="h-model" class="mb-3 text-[16px] font-semibold">Model</h2>
          <div class="grid grid-cols-2 gap-x-6 gap-y-4">
            <Stat label="Architecture" value={w?.architecture} hint="Known once the model has loaded" />
            <Stat label="Weights" value={w?.quant} hint="Known once the model has loaded" />
            <Stat label="Layers" value={w?.layers == null ? null : formatNumber(w.layers)} hint="Known once the model has loaded" />
            <Stat label="Size on disk" value={formatBytes(w?.fileBytes)} hint="Known once the model has loaded" />
            <Stat label="Context allowed" value={w?.contextLength == null ? null : `${formatNumber(w.contextLength)} tokens`} hint="Known once the model has loaded" />
            <Stat label="Model maximum" value={w?.modelContextLength == null ? null : `${formatNumber(w.modelContextLength)} tokens`} hint="Known once the model has loaded" />
          </div>
        </section>

        <section class="fl-card" aria-labelledby="h-speed">
          <h2 id="h-speed" class="mb-3 text-[16px] font-semibold">Speed</h2>
          <div class="grid grid-cols-2 gap-x-6 gap-y-4">
            <Stat label="Per reply token" value={formatSecPerToken(w?.speed.secPerToken.value)} hint={probeHint} />
            <Stat label="Replies at" value={formatTokensPerSecond(w?.speed.secPerToken.value)} hint={probeHint} />
            <Stat label="Per prompt token" value={formatSecPerToken(w?.speed.secPerPromptToken.value)} hint={evalHint} />
            <Stat label="Limited by" value={w?.speed.limit === 'disk' ? 'Disk reads' : w?.speed.limit === 'cpu' ? 'CPU' : null} hint={probeHint} mono={false} />
          </div>
          {#if w?.speed.secPerToken.measured}
            <p class="mt-3 text-[12px] text-muted-foreground" data-testid="speed-source">Token speed: {sourceLabel[w.speed.secPerToken.source] ?? w.speed.secPerToken.source}{w.speed.secPerToken.samples ? ` (${formatNumber(w.speed.secPerToken.samples)} tokens)` : ''}.</p>
          {/if}
        </section>

        <section class="fl-card" aria-labelledby="h-quality">
          <h2 id="h-quality" class="mb-3 text-[16px] font-semibold">Quality</h2>
          <div class="grid grid-cols-2 gap-x-6 gap-y-4">
            <Stat label="Perplexity" value={w?.quality.perplexity == null ? null : w.quality.perplexity.toFixed(2)} hint={evalHint} />
            <Stat label="Context recall" value={fid?.total ? `${fid.correct} of ${fid.total} (${formatPercent(fid.score)})` : null} hint={fidelityHint} />
          </div>
          <p class="mt-3 text-[12px] text-muted-foreground">
            Perplexity is lower for a better fit; compare quantizations of one model.{#if w?.quality.measuredAt}{' '}Measured {formatTime(w.quality.measuredAt)}.{/if}
            {#if fid?.total}{' '}Recall was checked on a log of {formatNumber(fid.promptTokens)} tokens.{/if}
          </p>
        </section>

        <section class="fl-card" aria-labelledby="h-queue">
          <h2 id="h-queue" class="mb-3 text-[16px] font-semibold">Work</h2>
          <div class="grid grid-cols-2 gap-x-6 gap-y-4">
            <Stat label="Running" value={s.busy ? 'Yes' : 'No'} />
            <Stat label="Waiting" value={`${s.queued} of ${s.queueMax}`} />
            <Stat label="At once" value={s.batch > 1 ? `${s.batch} requests (batch)` : '1 request'} mono={false} />
            <Stat label="Guess-ahead" value={w?.guessAhead == null ? null : w.guessAhead ? 'On' : 'Off'} hint="Not reported by a stub" mono={false} />
          </div>
        </section>
      </div>

      <section class="fl-card" aria-labelledby="h-connect">
        <h2 id="h-connect" class="mb-1 text-[16px] font-semibold">Connect a program</h2>
        <p class="mb-3 text-[13px] text-muted-foreground">Any OpenAI-compatible client on this computer can use this base URL.{s.apiKeyRequired ? ' It needs the API key this server was started with.' : ' No API key is set: any program on this PC can use the model.'}</p>
        <div class="flex flex-wrap items-center gap-2">
          <code class="fl-input flex min-w-0 flex-1 items-center font-mono" data-testid="base-url">{base}</code>
          <button class="fl-btn fl-btn--secondary" onclick={() => copyText(base, 'Base URL copied')}><Copy aria-hidden="true" /> Copy</button>
          <span class="fl-badge {s.apiKeyRequired ? 'fl-badge--warning' : 'fl-badge--muted'}">{s.apiKeyRequired ? 'API key required' : 'No API key'}</span>
        </div>
        <ol class="mt-4 grid list-decimal gap-1 pl-5 text-[13px] text-muted-foreground">
          <li>In Fairlead, Providers, Add provider: the Custom (OpenAI-compatible) preset with this base URL.</li>
          <li>Tick Local and set the timeout to 3600 seconds. Fetch the models: Fairlead reads the id and context length.</li>
          <li>Routes, Add route named <code class="font-mono text-foreground">deep-local</code>, with this model as its only target, hedging and local fallback off. Do not add it to interactive routes: it is far too slow for them.</li>
        </ol>
      </section>
    </div>
  {/if}
</div>
