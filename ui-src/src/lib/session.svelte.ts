// The connection to the Windlass server: opens the session, polls the status, runs the heartbeat when this tab owns
// the server, and knows when the server is gone.
import * as api from './api';
import { ApiError, credentials } from './api';
import { keyStore } from './prefs.svelte';
import { startHeartbeat } from './presence';
import type { Status } from './types';

export type Phase = 'boot' | 'needkey' | 'ready' | 'ended';
export type Engine = 'running' | 'stopping' | 'disconnected';

const POLL_MS = 2000;
const POLL_LOADING_MS = 1000;

class Session {
  phase = $state<Phase>('boot');
  status = $state<Status | null>(null);
  engine = $state<Engine>('running');
  keyError = $state('');
  private timer: ReturnType<typeof setTimeout> | null = null;
  private stopBeat: (() => void) | null = null;
  private misses = 0;
  private stopping = false;

  async boot(): Promise<void> {
    credentials.apiKey = keyStore.load();
    await this.connect(credentials.apiKey);
  }

  /** Called at start-up, and again from the key form when the server wants an API key. */
  async connect(apiKey: string): Promise<void> {
    this.keyError = '';
    try {
      const s = await api.openSession(apiKey);
      credentials.token = s.token;
      credentials.apiKey = apiKey;
      keyStore.save(apiKey);
      await this.refresh();
      this.phase = 'ready';
      this.schedule();
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) {
        credentials.apiKey = '';
        keyStore.save('');
        if (apiKey) this.keyError = 'That key was not accepted.';
        this.phase = 'needkey';
      } else {
        this.engine = 'disconnected';
        this.phase = this.phase === 'boot' ? 'ended' : this.phase;
      }
    }
  }

  async refresh(): Promise<void> {
    try {
      const s = await api.getStatus();
      this.status = s;
      this.misses = 0;
      if (!this.stopping) this.engine = 'running';
      if (s.autoStop.ownsLife && !this.stopBeat) this.stopBeat = startHeartbeat(credentials.token);
    } catch (e) {
      this.misses++;
      if (e instanceof ApiError && e.status === 401) {
        // The server restarted with a new token: start over.
        this.stopBeat?.();
        this.stopBeat = null;
        await this.connect(credentials.apiKey);
        return;
      }
      if (this.misses >= 2) this.engine = this.stopping ? 'stopping' : 'disconnected';
      if (this.stopping && this.misses >= 2) this.end();
      if (this.phase === 'boot') throw e;
    }
  }

  private schedule() {
    if (this.timer) clearTimeout(this.timer);
    const loading = this.status?.state === 'loading';
    this.timer = setTimeout(async () => {
      if (this.phase !== 'ready') return;
      await this.refresh().catch(() => undefined);
      this.schedule();
    }, loading ? POLL_LOADING_MS : POLL_MS);
  }

  /** End session: stops the server (docs/LIFECYCLE.md). A generation in flight is cancelled with it. */
  async endSession(): Promise<void> {
    this.stopping = true;
    this.engine = 'stopping';
    try {
      await api.shutdown();
    } catch {
      /* the server may close the connection while it stops */
    }
    setTimeout(() => this.end(), 800);
  }

  private end() {
    this.stopBeat?.();
    this.stopBeat = null;
    if (this.timer) clearTimeout(this.timer);
    this.phase = 'ended';
  }
}

export const session = new Session();
