<script lang="ts">
  import { onMount } from 'svelte';
  import { session } from '$lib/session.svelte';
  import BrandMark from '$components/BrandMark.svelte';
  import AppShell from '$components/AppShell.svelte';
  import Toaster from '$components/Toaster.svelte';
  import ConfirmHost from '$components/ConfirmHost.svelte';
  import KeyForm from '$views/KeyForm.svelte';
  import Ended from '$views/Ended.svelte';

  onMount(() => {
    void session.boot();
  });

  $effect(() => {
    const m = session.status?.state;
    document.title = session.phase === 'ready' ? `Windlass · ${m === 'ready' ? 'Ready' : m === 'loading' ? 'Loading' : 'Error'}` : 'Windlass';
  });
</script>

<div class="h-full">
  {#if session.phase === 'boot'}
    <div class="grid h-full place-items-center" aria-busy="true" data-testid="boot">
      <div class="grid justify-items-center gap-4">
        <BrandMark size={48} />
        <div class="fl-skeleton h-1 w-40"></div>
        <p class="text-[13px] text-muted-foreground">Connecting to Windlass on this computer…</p>
      </div>
    </div>
  {:else if session.phase === 'needkey'}
    <KeyForm />
  {:else if session.phase === 'ready'}
    <AppShell />
  {:else}
    <Ended />
  {/if}
</div>
<ConfirmHost />
<Toaster />
