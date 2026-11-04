<script lang="ts">
  // Choose a .gguf file on this computer and load it, replacing the loaded model. Browsing is done by Windlass on this
  // computer (GET /api/models): folders and .gguf files only, never file contents, never network paths.
  import { ApiError, listModels, loadModel } from '$lib/api';
  import { picker } from '$lib/picker.svelte';
  import { session } from '$lib/session.svelte';
  import { toast } from '$lib/toast.svelte';
  import { formatBytes } from '$lib/format';
  import type { ModelEntry, ModelListing } from '$lib/types';
  import Modal from './Modal.svelte';
  import Folder from '@lucide/svelte/icons/folder';
  import FileBox from '@lucide/svelte/icons/file-box';
  import ArrowUp from '@lucide/svelte/icons/arrow-up';
  import HardDrive from '@lucide/svelte/icons/hard-drive';

  let listing = $state<ModelListing | null>(null);
  let path = $state('');
  let error = $state('');
  let busy = $state(false);
  let wasOpen = false;

  const s = $derived(session.status);
  const working = $derived(!!s && (s.busy || s.queued > 0 || s.state === 'loading'));
  const swapping = $derived(!!s && s.state !== 'idle');
  const valid = $derived(/\.gguf$/i.test(path.trim().replace(/"+$/, '')));

  async function browse(dir: string | null) {
    error = '';
    try {
      listing = await listModels(dir);
    } catch (e) {
      error = e instanceof ApiError ? e.message : 'The list could not be read.';
    }
  }

  $effect(() => {
    if (picker.open && !wasOpen) {
      path = s?.model.path ?? '';
      error = '';
      // Start in the folder of the loaded model; otherwise at the top (drives and recent models).
      const here = s?.model.path ? s.model.path.replace(/[\\/][^\\/]*$/, '') : null;
      void browse(here || null);
    }
    wasOpen = picker.open;
  });

  function pick(e: ModelEntry) {
    if (e.type === 'dir') void browse(e.path);
    else path = e.path;
  }

  async function load() {
    if (!valid || busy) return;
    busy = true;
    error = '';
    try {
      const r = await loadModel(path.trim().replace(/^"+|"+$/g, ''));
      picker.open = false;
      toast.info(`Loading ${r.id}…`);
      await session.refresh();
    } catch (e) {
      error = e instanceof ApiError ? e.message : 'Windlass did not answer.';
    } finally {
      busy = false;
    }
  }
</script>

<Modal bind:open={picker.open} title={swapping ? 'Change model' : 'Load a model'} description={swapping ? 'The current model is closed first, so two big models never share memory. Requests get a 503 until the new one is ready.' : 'Choose a GGUF file on this computer. Windlass reads files only; it never downloads anything.'} wide>
  <div class="grid gap-3">
    {#if working}
      <p class="text-[13px] text-warning" role="status" data-testid="picker-busy">{s?.state === 'loading' ? 'A model is loading. Wait until it is ready or has failed.' : 'Windlass is writing a reply. Wait for it to finish, or stop it in Chat, then change the model.'}</p>
    {/if}
    <div class="grid gap-1.5">
      <label class="fl-eyebrow" for="model-path">Model file</label>
      <input id="model-path" class="fl-input w-full font-mono" placeholder="D:\models\Llama-3.3-70B-Instruct-Q4_K_M.gguf" bind:value={path} onkeydown={(e) => { if (e.key === 'Enter') { e.preventDefault(); void load(); } }} autocomplete="off" spellcheck="false" data-testid="model-path" />
      <span class="text-[12px] text-muted-foreground">Paste a path, or pick one below. For a split model, any one of its -00001-of-0000N.gguf parts.</span>
    </div>

    <div class="rounded-lg border border-border">
      <div class="flex items-center gap-2 border-b border-border px-2 py-1.5">
        <button class="fl-btn fl-btn--ghost fl-btn--icon" aria-label="Up one folder" disabled={!listing || listing.dir == null} onclick={() => void browse(listing?.parent ?? null)} data-testid="picker-up"><ArrowUp /></button>
        <div class="min-w-0 flex-1 truncate font-mono text-[12px] text-muted-foreground" data-testid="picker-dir" title={listing?.dir ?? ''}>{listing?.dir ?? 'This computer'}</div>
      </div>
      <ul class="max-h-[280px] overflow-y-auto py-1" data-testid="picker-list">
        {#if listing && listing.dir == null && listing.recent.length}
          <li class="fl-eyebrow px-3 pt-2 pb-1">Recent</li>
          {#each listing.recent as e (e.path)}
            <li><button class="flex min-h-8 w-full items-center gap-2 px-3 text-left text-[13px] hover:bg-muted disabled:opacity-60" disabled={e.exists === false} onclick={() => pick(e)} title={e.path} data-testid="picker-recent"><FileBox class="size-4 shrink-0" aria-hidden="true" /><span class="min-w-0 flex-1 truncate">{e.name}</span><span class="font-mono text-[12px] text-muted-foreground">{e.exists === false ? 'missing' : formatBytes(e.bytes)}</span></button></li>
          {/each}
          <li class="fl-eyebrow px-3 pt-3 pb-1">Drives</li>
        {/if}
        {#each listing?.entries ?? [] as e (e.path)}
          <li><button class="flex min-h-8 w-full items-center gap-2 px-3 text-left text-[13px] hover:bg-muted {path === e.path ? 'bg-muted' : ''}" onclick={() => pick(e)} title={e.path} aria-current={path === e.path ? 'true' : undefined} data-testid={e.type === 'dir' ? 'picker-dir-entry' : 'picker-file'}>
            {#if e.type === 'dir'}{#if listing?.dir == null}<HardDrive class="size-4 shrink-0" aria-hidden="true" />{:else}<Folder class="size-4 shrink-0" aria-hidden="true" />{/if}{:else}<FileBox class="size-4 shrink-0 text-primary" aria-hidden="true" />{/if}
            <span class="min-w-0 flex-1 truncate">{e.name}</span>
            {#if e.type === 'gguf'}<span class="font-mono text-[12px] text-muted-foreground">{formatBytes(e.bytes)}</span>{/if}
          </button></li>
        {:else}
          <li class="px-3 py-3 text-[13px] text-muted-foreground">{listing ? 'No folders or .gguf files here.' : 'Reading…'}</li>
        {/each}
        {#if listing?.truncated}<li class="px-3 py-2 text-[12px] text-muted-foreground">Only the first entries are shown. Type the path instead.</li>{/if}
      </ul>
    </div>
    {#if error}<p class="text-[13px] text-danger" role="alert" data-testid="picker-error">{error}</p>{/if}
  </div>
  {#snippet footer()}
    <button class="fl-btn fl-btn--secondary" onclick={() => (picker.open = false)}>Cancel</button>
    <button class="fl-btn fl-btn--primary" disabled={!valid || busy || working} onclick={load} data-testid="picker-load">{swapping ? 'Change model' : 'Load model'}</button>
  {/snippet}
</Modal>
