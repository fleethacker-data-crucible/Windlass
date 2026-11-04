<script lang="ts">
  import { toasts } from '$lib/toast.svelte';
  import CircleCheck from '@lucide/svelte/icons/circle-check';
  import CircleX from '@lucide/svelte/icons/circle-x';
  import Info from '@lucide/svelte/icons/info';
  import X from '@lucide/svelte/icons/x';
</script>

<div class="pointer-events-none fixed right-4 z-50 grid max-w-[360px] gap-2 bottom-[calc(var(--fl-statusbar-height)+16px)]" role="region" aria-label="Notifications" aria-live="polite">
  {#each toasts.items as t (t.id)}
    <div class="fl-toast pointer-events-auto" role={t.kind === 'error' ? 'alert' : 'status'}>
      {#if t.kind === 'success'}<CircleCheck class="mt-0.5 size-4 shrink-0 text-success" />{:else if t.kind === 'error'}<CircleX class="mt-0.5 size-4 shrink-0 text-danger" />{:else}<Info class="mt-0.5 size-4 shrink-0 text-info" />{/if}
      <span class="min-w-0 flex-1">{t.text}</span>
      <button class="fl-btn fl-btn--ghost fl-btn--icon -my-1 -mr-1" aria-label="Dismiss" onclick={() => toasts.dismiss(t.id)}><X /></button>
    </div>
  {/each}
</div>
