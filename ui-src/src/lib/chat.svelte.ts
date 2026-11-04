// The chat playground: one conversation, streamed from /v1/chat/completions, cancellable. Nothing is stored; prompts
// and replies live in this page's memory only.
import * as api from './api';
import { ApiError } from './api';
import type { ChatMessage } from './types';

export type ChatPhase = 'idle' | 'waiting' | 'streaming';

export interface Turn extends ChatMessage {
  id: number;
  /** Set on assistant turns once the reply ends. */
  stats?: { promptTokens: number | null; completionTokens: number | null; seconds: number; firstTokenSeconds: number | null; finish: string };
  error?: string;
  cancelled?: boolean;
}

class Chat {
  turns = $state<Turn[]>([]);
  system = $state('');
  maxTokens = $state(256);
  temperature = $state(0);
  seed = $state<number | null>(null);
  phase = $state<ChatPhase>('idle');
  /** Where the request is: sent, accepted by the server (maybe queued), then text arriving. */
  accepted = $state(false);
  keepalives = $state(0);
  elapsed = $state(0);
  private n = 0;
  private abort: AbortController | null = null;
  private clock: ReturnType<typeof setInterval> | null = null;

  get busy() {
    return this.phase !== 'idle';
  }

  clear() {
    this.stop();
    this.turns = [];
  }

  stop() {
    this.abort?.abort();
  }

  async send(text: string, model: string | undefined): Promise<void> {
    const content = text.trim();
    if (!content || this.busy) return;
    const messages: ChatMessage[] = [];
    if (this.system.trim()) messages.push({ role: 'system', content: this.system.trim() });
    for (const t of this.turns) if (!t.error && !t.cancelled && t.content) messages.push({ role: t.role, content: t.content });
    messages.push({ role: 'user', content });
    const replyId = this.n + 2;
    this.turns = [...this.turns, { id: ++this.n, role: 'user', content }, { id: ++this.n, role: 'assistant', content: '' }];

    this.abort = new AbortController();
    this.phase = 'waiting';
    this.accepted = false;
    this.keepalives = 0;
    this.elapsed = 0;
    const started = performance.now();
    let first: number | null = null;
    this.clock = setInterval(() => (this.elapsed = (performance.now() - started) / 1000), 500);
    // The reply lives in the reactive array, so write through it.
    const live = () => this.turns.find((t) => t.id === replyId)!;
    let usage: { prompt_tokens: number; completion_tokens: number } | null = null;
    let finish = 'stop';
    try {
      await api.streamChat(
        { model, messages, maxTokens: this.maxTokens, temperature: this.temperature, seed: this.seed },
        {
          onAccepted: () => (this.accepted = true),
          onKeepalive: () => (this.keepalives += 1),
          onText: (t) => {
            if (first == null) first = (performance.now() - started) / 1000;
            this.phase = 'streaming';
            live().content += t;
          },
          onUsage: (u) => (usage = u),
          onFinish: (r) => (finish = r)
        },
        this.abort.signal
      );
    } catch (e) {
      if ((e as Error).name === 'AbortError') {
        live().cancelled = true;
        finish = 'cancelled';
      } else {
        live().error = e instanceof ApiError ? e.message : 'The connection to Windlass was lost.';
        finish = 'error';
      }
    } finally {
      if (this.clock) clearInterval(this.clock);
      this.clock = null;
      const t = live();
      const u = usage as { prompt_tokens: number; completion_tokens: number } | null;
      t.stats = { promptTokens: u?.prompt_tokens ?? null, completionTokens: u?.completion_tokens ?? null, seconds: (performance.now() - started) / 1000, firstTokenSeconds: first, finish };
      this.phase = 'idle';
      this.abort = null;
    }
  }
}

export const chat = new Chat();
