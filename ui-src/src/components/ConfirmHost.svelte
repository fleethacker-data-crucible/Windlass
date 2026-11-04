<script lang="ts">
  import { confirmHost } from '$lib/confirm.svelte';
  import Modal from './Modal.svelte';
  let open = $state(false);
  $effect(() => {
    open = confirmHost.request != null;
  });
</script>

<Modal bind:open title={confirmHost.request?.title ?? ''} description={confirmHost.request?.description ?? ''} onclose={() => confirmHost.request && confirmHost.answer(false)}>
  {#snippet footer()}
    <button class="fl-btn fl-btn--secondary" onclick={() => confirmHost.answer(false)}>Cancel</button>
    <button class="fl-btn {confirmHost.request?.destructive ? 'fl-btn--danger' : 'fl-btn--primary'}" onclick={() => confirmHost.answer(true)} data-testid="confirm-ok">{confirmHost.request?.confirmLabel ?? 'OK'}</button>
  {/snippet}
</Modal>
