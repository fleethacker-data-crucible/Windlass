<script lang="ts">
  import { keyStore, prefs, type Theme } from '$lib/prefs.svelte';
  import { session } from '$lib/session.svelte';
  import { confirm } from '$lib/confirm.svelte';
  import { credentials } from '$lib/api';
  import { formatDuration, formatTime } from '$lib/format';
  import PageHeader from '$components/PageHeader.svelte';
  import Power from '@lucide/svelte/icons/power';

  const s = $derived(session.status);
  const themes: { id: Theme; label: string; note: string }[] = [
    { id: 'system', label: 'Follow the system', note: 'Night watch or Chart room, as Windows is set' },
    { id: 'dark', label: 'Night watch', note: 'Dark' },
    { id: 'light', label: 'Chart room', note: 'Light' }
  ];

  async function forgetKey() {
    credentials.apiKey = '';
    keyStore.save('');
    await session.connect('');
  }

  async function endSession() {
    const ok = await confirm({
      title: 'End session?',
      description: 'Windlass stops, and any reply it is writing stops with it. Start it again with Launch-Windlass.bat.',
      confirmLabel: 'End session',
      destructive: true
    });
    if (ok) await session.endSession();
  }

  const life = $derived(!s ? '' : !s.autoStop.enabled ? 'Auto-stop is off: Windlass keeps serving until you end the session or run Stop-Windlass.ps1.' : s.autoStop.ownsLife ? 'This tab owns the server: closing it stops Windlass within about 5 seconds, and a reload does not.' : 'Windlass was not started with --open, so closing this tab does not stop it. Programs that heartbeat (Fairlead) can still own it.');
</script>

<PageHeader title="Settings" />
<div class="min-h-0 flex-1 overflow-y-auto">
  <div class="mx-auto grid max-w-[820px] gap-4 p-6">
    <section class="fl-card" aria-labelledby="s-theme">
      <h2 id="s-theme" class="mb-1 text-[16px] font-semibold">Appearance</h2>
      <p class="mb-3 text-[13px] text-muted-foreground">Kept in this browser only.</p>
      <div class="grid gap-2" role="radiogroup" aria-labelledby="s-theme">
        {#each themes as t (t.id)}
          <label class="flex min-h-8 items-center gap-3 text-[14px]">
            <input type="radio" name="theme" class="size-4" checked={prefs.theme === t.id} onchange={() => prefs.setTheme(t.id)} data-testid={`theme-${t.id}`} />
            <span>{t.label}</span><span class="text-[12px] text-muted-foreground">{t.note}</span>
          </label>
        {/each}
      </div>
    </section>

    <section class="fl-card" aria-labelledby="s-server">
      <h2 id="s-server" class="mb-3 text-[16px] font-semibold">This server</h2>
      {#if s}
        <table class="fl-table" data-testid="server-table">
          <tbody>
            <tr><td>Address</td><td class="num">{s.address}</td></tr>
            <tr><td>Version</td><td class="num">{s.version}</td></tr>
            <tr><td>Started</td><td class="num">{formatTime(s.startedAt)} ({formatDuration(s.uptimeSeconds)} ago)</td></tr>
            <tr><td>API key</td><td class="num">{s.apiKeyRequired ? 'Required' : 'Not set'}</td></tr>
            <tr><td>Requests at once</td><td class="num">{s.batch}</td></tr>
            <tr><td>Queue</td><td class="num">{s.queueMax} waiting at most</td></tr>
            <tr><td>Auto-stop</td><td class="num">{s.autoStop.enabled ? 'On' : 'Off'}</td></tr>
          </tbody>
        </table>
        <p class="mt-3 text-[13px] text-muted-foreground">{life}</p>
        <p class="mt-2 text-[12px] text-muted-foreground">These are set when Windlass starts (see <code class="font-mono">Launch-Windlass.bat help</code>); this page shows them and does not change them.</p>
      {/if}
      {#if s?.apiKeyRequired && credentials.apiKey}
        <div class="mt-3"><button class="fl-btn fl-btn--secondary" onclick={forgetKey}>Forget the API key in this tab</button></div>
      {/if}
    </section>

    <section class="fl-card" aria-labelledby="s-end">
      <h2 id="s-end" class="mb-1 text-[16px] font-semibold">End session</h2>
      <p class="mb-3 text-[13px] text-muted-foreground">Stops Windlass on this computer. Programs that use its API lose the model until you start it again.</p>
      <button class="fl-btn fl-btn--danger" onclick={endSession} data-testid="settings-end"><Power aria-hidden="true" /> End session</button>
    </section>
  </div>
</div>
