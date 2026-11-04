import { describe, expect, it } from 'vitest';
import { HEARTBEAT_PATH, heartbeatBody, startHeartbeat, type PresenceEnv } from './presence';

function fakeEnv() {
  const calls: { path: string; init: RequestInit }[] = [];
  const beacons: { path: string; body: string }[] = [];
  const listeners = new Map<string, () => void>();
  let tick: (() => void) | null = null;
  let cleared = false;
  const env: PresenceEnv = {
    fetch: ((path: string, init: RequestInit) => {
      calls.push({ path, init });
      return Promise.resolve(new Response('{}'));
    }) as typeof fetch,
    sendBeacon: (path, blob) => {
      void blob.text().then((t) => beacons.push({ path, body: t }));
      beacons.push({ path, body: '' });
      return true;
    },
    setInterval: (fn) => {
      tick = fn;
      return 1;
    },
    clearInterval: () => (cleared = true),
    addEventListener: (_t, type, fn) => listeners.set(type, fn),
    removeEventListener: (_t, type) => listeners.delete(type)
  };
  return { env, calls, beacons, listeners, tick: () => tick?.(), cleared: () => cleared };
}

describe('startHeartbeat', () => {
  it('sends at once and on every tick, with the token in the header and the body', () => {
    const f = fakeEnv();
    const stop = startHeartbeat('tok', f.env, 5000, 'tab1');
    expect(f.calls).toHaveLength(1);
    expect(f.calls[0].path).toBe(HEARTBEAT_PATH);
    expect((f.calls[0].init.headers as Record<string, string>)['X-Windlass-Session']).toBe('tok');
    expect(JSON.parse(f.calls[0].init.body as string)).toEqual({ tab: 'tab1', leaving: false, token: 'tok' });
    f.tick();
    expect(f.calls).toHaveLength(2);
    stop();
    expect(f.cleared()).toBe(true);
    f.tick();
    expect(f.calls).toHaveLength(2);
  });
  it('sends a leaving beacon on pagehide and nothing else until the page returns', () => {
    const f = fakeEnv();
    startHeartbeat('tok', f.env, 5000, 'tab1');
    f.listeners.get('pagehide')!();
    expect(f.beacons.length).toBeGreaterThan(0);
    expect(f.beacons[0].path).toBe(HEARTBEAT_PATH);
    const before = f.calls.length;
    f.tick();
    expect(f.calls).toHaveLength(before);
    f.listeners.get('pageshow')!();
    expect(f.calls).toHaveLength(before + 1);
  });
  it('builds the body the server reads', () => {
    expect(JSON.parse(heartbeatBody('a', true, 't'))).toEqual({ tab: 'a', leaving: true, token: 't' });
  });
});
