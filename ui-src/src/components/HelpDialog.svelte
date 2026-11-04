<script lang="ts">
  import Modal from './Modal.svelte';
  let { open = $bindable(false) }: { open?: boolean } = $props();
  const keys: [string, string][] = [
    ['?', 'This list'],
    ['g then o / c / p / l / s', 'Go to Overview, Chat, Playbook, Log, Settings'],
    ['Ctrl+Enter', 'Send the message (Chat)'],
    ['Esc', 'Close a dialog']
  ];
</script>

<Modal bind:open title="Keyboard shortcuts">
  <table class="fl-table">
    <tbody>{#each keys as [k, d] (k)}<tr><td class="w-52"><kbd class="rounded border border-control px-1.5 py-0.5 font-mono text-[12px]">{k}</kbd></td><td>{d}</td></tr>{/each}</tbody>
  </table>
  {#snippet footer()}<button class="fl-btn fl-btn--primary" onclick={() => (open = false)}>Close</button>{/snippet}
</Modal>
