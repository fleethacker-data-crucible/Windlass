<script lang="ts">
  // One labelled value. A missing value is a dash with a hint on how to get it (Fleet UI section 7).
  import { DASH } from '$lib/format';
  let { label, value, hint = '', mono = true }: { label: string; value: string | null | undefined; hint?: string; mono?: boolean } = $props();
  const missing = $derived(value == null || value === '' || value === DASH);
</script>

<div class="min-w-0">
  <div class="fl-eyebrow">{label}</div>
  <div class="mt-0.5 text-[20px] leading-7 {mono ? 'font-mono' : ''} {missing ? 'text-muted-foreground' : ''}">{missing ? DASH : value}</div>
  {#if missing && hint}<div class="text-[12px] text-muted-foreground">{hint}</div>{/if}
</div>
