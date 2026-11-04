<script lang="ts">
  // A modal on the native <dialog>: the browser traps focus, Esc closes, the page behind is inert. 14 px radius (Fleet UI).
  import type { Snippet } from 'svelte';
  let { open = $bindable(false), title, description = '', onclose, children, footer }: { open?: boolean; title: string; description?: string; onclose?: () => void; children?: Snippet; footer?: Snippet } = $props();
  let el: HTMLDialogElement | undefined = $state();

  $effect(() => {
    if (!el) return;
    if (open && !el.open) el.showModal();
    else if (!open && el.open) el.close();
  });
</script>

<dialog bind:this={el} class="fl-dialog m-auto" aria-labelledby="modal-title" onclose={() => { open = false; onclose?.(); }} onclick={(e) => { if (e.target === el) el?.close(); }}>
  <div class="p-5">
    <h2 id="modal-title" class="text-[16px] font-semibold">{title}</h2>
    {#if description}<p class="mt-1.5 text-[13px] text-muted-foreground">{description}</p>{/if}
    {#if children}<div class="mt-4">{@render children()}</div>{/if}
  </div>
  {#if footer}<div class="flex justify-end gap-2 border-t border-border px-5 py-3">{@render footer()}</div>{/if}
</dialog>
