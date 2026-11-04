// The browser side of the Windlass API. Same origin only: the page is served by the server it talks to.
import type { ChatMessage, LogLine, ModelListing, PlaybookDoc, Status } from './types';
import { SseParser } from './sse';

export class ApiError extends Error {
  constructor(
    message: string,
    public status: number,
    public kind: string
  ) {
    super(message);
  }
}

/** In memory only. The API key, when the server has one, is kept for this tab (sessionStorage) by the session store. */
export const credentials = { token: '', apiKey: '' };

async function errorFrom(res: Response): Promise<ApiError> {
  let message = `${res.status} ${res.statusText}`;
  let kind = 'error';
  try {
    const body = await res.json();
    if (body?.error?.message) message = String(body.error.message);
    if (body?.error?.type) kind = String(body.error.type);
  } catch {
    /* not JSON */
  }
  return new ApiError(message, res.status, kind);
}

async function call<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers);
  if (credentials.token) headers.set('X-Windlass-Session', credentials.token);
  const res = await fetch(path, { ...init, headers, cache: 'no-store', credentials: 'same-origin', referrerPolicy: 'no-referrer' });
  if (!res.ok) throw await errorFrom(res);
  return (await res.json()) as T;
}

/** POST /api/session: the token every other /api/ route needs. 401 when the server has an API key and none was given. */
export async function openSession(apiKey = ''): Promise<{ token: string; apiKeyRequired: boolean }> {
  const headers: Record<string, string> = {};
  if (apiKey) headers.Authorization = `Bearer ${apiKey}`;
  const res = await fetch('/api/session', { method: 'POST', headers, cache: 'no-store', credentials: 'same-origin', referrerPolicy: 'no-referrer' });
  if (!res.ok) throw await errorFrom(res);
  return (await res.json()) as { token: string; apiKeyRequired: boolean };
}

export const getStatus = () => call<Status>('/api/status');
export const getPlaybook = () => call<{ playbook: PlaybookDoc | null }>('/api/playbook').then((r) => r.playbook);
export const getLogs = (after: number) => call<{ lines: LogLine[]; last: number }>(`/api/logs?after=${after}`);
export const listModels = (dir: string | null) => call<ModelListing>(`/api/models${dir ? `?dir=${encodeURIComponent(dir)}` : ''}`);
/** POST /api/model: 202 once the load has started (watch the status); 409 while a reply is running or a model is loading. */
export const loadModel = (path: string) =>
  call<{ state: string; path: string; id: string }>('/api/model', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path }) });
export const unloadModel = () => call<{ state: string }>('/api/model/unload', { method: 'POST' });
export const shutdown = () => call<{ stopping: boolean }>('/api/shutdown', { method: 'POST' });

export interface ChatParams {
  model?: string;
  messages: ChatMessage[];
  maxTokens: number;
  temperature: number;
  seed?: number | null;
}

export interface ChatHandlers {
  /** The server has the request (`: accepted`); it may still be waiting its turn. */
  onAccepted?: () => void;
  onKeepalive?: () => void;
  onText: (text: string) => void;
  onUsage?: (usage: { prompt_tokens: number; completion_tokens: number; total_tokens: number }) => void;
  onFinish?: (reason: string) => void;
}

/** POST /v1/chat/completions with stream:true. Resolves when the stream ends; rejects with ApiError, or AbortError when stopped. */
export async function streamChat(p: ChatParams, h: ChatHandlers, signal: AbortSignal): Promise<void> {
  const body: Record<string, unknown> = {
    messages: p.messages,
    stream: true,
    stream_options: { include_usage: true },
    max_tokens: p.maxTokens,
    temperature: p.temperature
  };
  if (p.model) body.model = p.model;
  if (p.seed != null) body.seed = p.seed;
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (credentials.apiKey) headers.Authorization = `Bearer ${credentials.apiKey}`;
  const res = await fetch('/v1/chat/completions', { method: 'POST', headers, body: JSON.stringify(body), signal, cache: 'no-store', referrerPolicy: 'no-referrer' });
  if (!res.ok) throw await errorFrom(res);
  if (!res.body) throw new ApiError('The browser gave no response stream.', 0, 'error');
  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  const parser = new SseParser();
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    for (const ev of parser.push(decoder.decode(value, { stream: true }))) {
      if (ev.type === 'comment') {
        if (ev.text === 'accepted') h.onAccepted?.();
        else if (ev.text === 'keepalive') h.onKeepalive?.();
        continue;
      }
      if (ev.data === '[DONE]') return;
      let chunk: any;
      try {
        chunk = JSON.parse(ev.data);
      } catch {
        continue;
      }
      if (chunk.error) throw new ApiError(String(chunk.error.message ?? 'The server reported an error.'), 500, String(chunk.error.type ?? 'server_error'));
      const choice = chunk.choices?.[0];
      const text = choice?.delta?.content;
      if (typeof text === 'string' && text.length > 0) h.onText(text);
      if (choice?.finish_reason) h.onFinish?.(String(choice.finish_reason));
      if (chunk.usage) h.onUsage?.(chunk.usage);
    }
  }
}
