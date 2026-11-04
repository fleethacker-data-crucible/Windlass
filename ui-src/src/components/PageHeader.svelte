<script lang="ts">
  import type { Snippet } from 'svelte';
  let { title, actions }: { title: string; actions?: Snippet } = $props();
</script>

<header class="flex shrink-0 items-center justify-between gap-3 border-b border-border bg-surface px-6 h-[var(--fl-header-height)]">
  <h1 class="font-display text-[20px]" data-testid="page-title">{title}</h1>
  {#if actions}<div class="flex items-center gap-2">{@render actions()}</div>{/if}
</header>
