// Presence (Fleet local app lifecycle): when Windlass was started with `serve --open`, this tab owns the server. A
// heartbeat every 5 s says "still here"; a sendBeacon "leaving" signal on pagehide lets the server stop within 5 s
// instead of 20. A reload sends a fresh heartbeat inside the grace period, so nothing stops. The beacon cannot set
// headers, so on this one route the session token travels in the body. Heartbeats only start when the server says the
// tab owns it; a server started for other programs is never stopped by closing a page.
export const HEARTBEAT_PATH = '/api/heartbeat';
export const HEARTBEAT_MS = 5000;

export interface PresenceEnv {
  fetch: typeof fetch;
  sendBeacon?: (url: string, data: Blob) => boolean;
  setInterval: (fn: () => void, ms: number) => unknown;
  clearInterval: (id: unknown) => void;
  addEventListener: (target: 'window' | 'document', type: string, fn: () => void) => void;
  removeEventListener: (target: 'window' | 'document', type: string, fn: () => void) => void;
}

export function newTabId(): string {
  const b = new Uint8Array(8);
  crypto.getRandomValues(b);
  return Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('');
}

export function heartbeatBody(tab: string, leaving: boolean, token: string): string {
  return JSON.stringify({ tab, leaving, token });
}

function browserEnv(): PresenceEnv {
  const targetOf = (t: 'window' | 'document') => (t === 'window' ? window : document);
  return {
    fetch: (...a) => fetch(...a),
    sendBeacon: typeof navigator.sendBeacon === 'function' ? (u, d) => navigator.sendBeacon(u, d) : undefined,
    setInterval: (fn, ms) => window.setInterval(fn, ms),
    clearInterval: (id) => window.clearInterval(id as number),
    addEventListener: (t, type, fn) => targetOf(t).addEventListener(type, fn),
    removeEventListener: (t, type, fn) => targetOf(t).removeEventListener(type, fn)
  };
}

/** Starts the heartbeat for this tab; returns a function that stops it (after End session, or when the server is gone). */
export function startHeartbeat(token: string, env: PresenceEnv = browserEnv(), intervalMs = HEARTBEAT_MS, tab = newTabId()): () => void {
  let stopped = false;
  // Set on pagehide, cleared on pageshow: nothing but the leaving signal goes out while the page is going away.
  let leftPage = false;
  const send = (leaving: boolean) => {
    if (stopped || (leftPage && !leaving)) return;
    try {
      void env
        .fetch(HEARTBEAT_PATH, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'X-Windlass-Session': token },
          body: heartbeatBody(tab, leaving, token),
          credentials: 'same-origin',
          cache: 'no-store',
          keepalive: true,
          referrerPolicy: 'no-referrer'
        })
        .catch(() => undefined);
    } catch {
      /* the server is gone; nothing to do */
    }
  };
  const onHide = () => {
    if (stopped) return;
    leftPage = true;
    let sent = false;
    try {
      if (env.sendBeacon) sent = env.sendBeacon(HEARTBEAT_PATH, new Blob([heartbeatBody(tab, true, token)], { type: 'application/json' }));
    } catch {
      sent = false;
    }
    if (!sent) send(true);
  };
  const onShow = () => {
    leftPage = false;
    send(false);
  };
  send(false);
  const timer = env.setInterval(() => send(false), intervalMs);
  env.addEventListener('window', 'pagehide', onHide);
  env.addEventListener('window', 'pageshow', onShow);
  return () => {
    if (stopped) return;
    stopped = true;
    env.clearInterval(timer);
    env.removeEventListener('window', 'pagehide', onHide);
    env.removeEventListener('window', 'pageshow', onShow);
  };
}
