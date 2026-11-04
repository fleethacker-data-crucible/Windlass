<script lang="ts">
  import { session } from '$lib/session.svelte';
  import Emblem from '$components/Emblem.svelte';
</script>

<div class="grid h-full place-items-center p-6" data-testid="ended">
  <div class="fl-empty max-w-[460px]">
    <Emblem size={176} />
    <h2>{session.engine === 'disconnected' && !session.status ? 'Windlass is not answering' : 'Windlass has stopped'}</h2>
    <p class="text-[13px] text-muted-foreground">Start it again with <code class="font-mono">Launch-Windlass.bat</code> and choose a model, then reload this page. You can close this tab.</p>
    <button class="fl-btn fl-btn--primary" onclick={() => location.reload()}>Reload</button>
  </div>
</div>
