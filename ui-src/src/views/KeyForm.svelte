<script lang="ts">
  import { session } from '$lib/session.svelte';
  import BrandMark from '$components/BrandMark.svelte';
  let key = $state('');
  let busy = $state(false);
  async function submit() {
    busy = true;
    await session.connect(key.trim());
    busy = false;
  }
</script>

<div class="grid h-full place-items-center p-6">
  <form class="fl-card grid w-full max-w-[420px] gap-4 p-6" onsubmit={(e) => { e.preventDefault(); void submit(); }}>
    <BrandMark size={40} />
    <div>
      <h1 class="font-display text-[20px]">API key</h1>
      <p class="mt-1 text-[13px] text-muted-foreground">This Windlass was started with an API key. Enter it to use the page. It is kept for this tab only and sent to this server only.</p>
    </div>
    <div class="grid gap-1.5">
      <label class="fl-eyebrow" for="key">Key</label>
      <input id="key" class="fl-input w-full font-mono" type="password" autocomplete="off" bind:value={key} data-testid="key-input" />
      {#if session.keyError}<span class="text-[13px] text-danger" role="alert">{session.keyError}</span>{/if}
    </div>
    <button class="fl-btn fl-btn--primary fl-btn--lg justify-center" type="submit" disabled={busy || !key.trim()} data-testid="key-submit">Open</button>
  </form>
</div>
