<script lang="ts">
  import { tick } from 'svelte';
  import { session } from '$lib/session.svelte';
  import { chat } from '$lib/chat.svelte';
  import { prefs } from '$lib/prefs.svelte';
  import { DASH, formatDuration, formatSecPerToken, worstCaseSeconds } from '$lib/format';
  import PageHeader from '$components/PageHeader.svelte';
  import Emblem from '$components/Emblem.svelte';
  import Send from '@lucide/svelte/icons/send';
  import Square from '@lucide/svelte/icons/square';
  import Eraser from '@lucide/svelte/icons/eraser';

  let draft = $state('');
  let list: HTMLElement | undefined = $state();
  const s = $derived(session.status);
  const ready = $derived(s?.state === 'ready');
  const spt = $derived(s?.windlass.speed.secPerToken.value ?? null);
  const worst = $derived(worstCaseSeconds(chat.maxTokens, spt));

  chat.maxTokens = prefs.maxTokens;

  async function send() {
    const text = draft;
    if (!text.trim() || chat.busy || !ready) return;
    draft = '';
    const done = chat.send(text, s?.model.id);
    await tick();
    list?.scrollTo({ top: list.scrollHeight });
    await done;
  }

  $effect(() => {
    void chat.turns.at(-1)?.content;
    if (list) list.scrollTo({ top: list.scrollHeight });
  });

  function onKey(e: KeyboardEvent) {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      void send();
    }
  }

  const status = $derived(
    chat.phase === 'waiting'
      ? chat.accepted
        ? `Accepted by Windlass. Waiting for its turn or the first token (${formatDuration(chat.elapsed)}${chat.keepalives ? `, ${chat.keepalives} keepalive${chat.keepalives === 1 ? '' : 's'}` : ''}). A big model takes minutes to answer a first prompt.`
        : 'Sending…'
      : chat.phase === 'streaming'
        ? `Writing (${formatDuration(chat.elapsed)})`
        : ''
  );
</script>

<PageHeader title="Chat">
  {#snippet actions()}
    <button class="fl-btn fl-btn--secondary" onclick={() => chat.clear()} disabled={chat.turns.length === 0} data-testid="chat-clear"><Eraser aria-hidden="true" /> Clear</button>
  {/snippet}
</PageHeader>

<div class="flex min-h-0 flex-1">
  <div class="flex min-w-0 flex-1 flex-col">
    <div bind:this={list} class="min-h-0 flex-1 overflow-y-auto p-6" data-testid="chat-list" aria-live="polite" aria-label="Conversation">
      {#if chat.turns.length === 0}
        <div class="fl-empty mx-auto mt-16 max-w-[460px]">
          <Emblem size={160} />
          <h2>A slow, deep reply</h2>
          <p class="text-[13px] text-muted-foreground">
            Windlass reads the whole model through memory for every token, so {spt ? `at ${formatSecPerToken(spt)}` : 'a very large model'} this is for testing, not for conversation. Keep replies short.
          </p>
        </div>
      {:else}
        <ol class="mx-auto grid max-w-[820px] gap-4">
          {#each chat.turns as t (t.id)}
            <li class="grid gap-1" data-testid={`turn-${t.role}`}>
              <div class="fl-eyebrow">{t.role === 'user' ? 'You' : 'Windlass'}</div>
              <div class="whitespace-pre-wrap rounded-lg border border-border px-3 py-2 text-[14px] {t.role === 'user' ? 'bg-muted' : 'bg-card'}">{t.content}{#if t.role === 'assistant' && !t.content && !t.error && !t.stats}<span class="text-muted-foreground">…</span>{/if}</div>
              {#if t.error}<div class="text-[13px] text-danger" role="alert">Error: {t.error}</div>{/if}
              {#if t.stats}
                <div class="text-[12px] text-muted-foreground" data-testid="turn-stats">
                  {t.cancelled ? 'Stopped' : t.error ? 'Failed' : t.stats.finish === 'length' ? 'Cut off at the token limit' : 'Finished'}
                  · {formatDuration(t.stats.seconds)}
                  {#if t.stats.firstTokenSeconds != null}· first token after {formatDuration(t.stats.firstTokenSeconds)}{/if}
                  {#if t.stats.completionTokens != null}· {t.stats.completionTokens} reply tokens{/if}
                  {#if t.stats.promptTokens != null}· {t.stats.promptTokens} prompt tokens{/if}
                </div>
              {/if}
            </li>
          {/each}
        </ol>
      {/if}
    </div>

    <form class="border-t border-border bg-surface p-4" onsubmit={(e) => { e.preventDefault(); void send(); }}>
      <div class="mx-auto grid max-w-[820px] gap-2">
        {#if status}<div class="text-[13px] text-muted-foreground" role="status" data-testid="chat-status">{status}</div>{/if}
        {#if !ready}<div class="text-[13px] text-warning" role="status">{s?.state === 'loading' ? 'The model is still loading. You can write now and send when it is ready.' : 'The model is not available.'}</div>{/if}
        <label class="sr-only" for="draft">Message</label>
        <textarea id="draft" class="fl-input w-full resize-y" rows="3" placeholder="Ask something short. Ctrl+Enter sends." bind:value={draft} onkeydown={onKey} disabled={chat.busy} data-testid="chat-input"></textarea>
        <div class="flex items-center justify-between gap-3">
          <span class="text-[12px] text-muted-foreground" data-testid="chat-estimate">{worst == null ? `Time for a reply: ${DASH} (run playbook probe to measure it)` : `Up to ${formatDuration(worst)} for ${chat.maxTokens} tokens at ${formatSecPerToken(spt)}`}</span>
          {#if chat.busy}
            <button type="button" class="fl-btn fl-btn--secondary" onclick={() => chat.stop()} data-testid="chat-stop"><Square aria-hidden="true" /> Stop</button>
          {:else}
            <button type="submit" class="fl-btn fl-btn--primary" disabled={!ready || !draft.trim()} data-testid="chat-send"><Send aria-hidden="true" /> Send</button>
          {/if}
        </div>
      </div>
    </form>
  </div>

  <aside class="w-[280px] shrink-0 overflow-y-auto border-l border-border bg-sidebar p-4 max-[1100px]:hidden" aria-label="Chat settings">
    <div class="grid gap-4">
      <div class="grid gap-1.5">
        <label class="fl-eyebrow" for="system">System prompt</label>
        <textarea id="system" class="fl-input w-full resize-y" rows="4" placeholder="Optional" bind:value={chat.system} disabled={chat.busy}></textarea>
      </div>
      <div class="grid gap-1.5">
        <label class="fl-eyebrow" for="maxtok">Max reply tokens</label>
        <input id="maxtok" class="fl-input w-full font-mono" type="number" min="1" max="8192" bind:value={chat.maxTokens} onchange={() => prefs.setMaxTokens(chat.maxTokens)} disabled={chat.busy} />
      </div>
      <div class="grid gap-1.5">
        <label class="fl-eyebrow" for="temp">Temperature</label>
        <input id="temp" class="fl-input w-full font-mono" type="number" min="0" max="2" step="0.1" bind:value={chat.temperature} disabled={chat.busy} />
        <span class="text-[12px] text-muted-foreground">0 is greedy: the same prompt gives the same reply, and guess-ahead can speed it up.</span>
      </div>
      <div class="grid gap-1.5">
        <label class="fl-eyebrow" for="seed">Seed</label>
        <input id="seed" class="fl-input w-full font-mono" type="number" placeholder="Random" bind:value={chat.seed} disabled={chat.busy} />
      </div>
    </div>
  </aside>
</div>
