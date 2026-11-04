// A stand-in for the Windlass server, for developing and testing the UI without a model.
//   node tools/mock-windlass.mjs [--port 8777] [--scenario ready|idle|loading|error|key|bare]
// Serves ../ui/dist and the same /api/ and /v1 routes as src/Server.cs, with plausible numbers ("ready"), or none ("bare").
// Synthetic data only: nothing here is a measurement.
import http from 'node:http';
import { readFileSync, existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const dist = path.resolve(here, '../../ui/dist');
const arg = (n, d) => { const i = process.argv.indexOf(`--${n}`); return i > 0 ? process.argv[i + 1] : d; };
const port = Number(arg('port', 8777));
const scenario = arg('scenario', 'ready');
const TOKEN = 'mock-session-token';
const KEY = 'sesame';
const started = Date.now();
let hb = { count: 0, last: null, leaving: false };
const logs = [];
const log = (level, text) => logs.push({ seq: logs.length + 1, text: `${new Date().toISOString().slice(0, 19).replace('T', ' ')} ${level.padEnd(5)} ${text}` });
log('INFO', 'Windlass 0.2.0 (mock) on http://127.0.0.1:' + port + '/v1');
log('INFO', 'playbook: 96 GB RAM, plenty free: pin 10 GB of layers.');
log('WARN', 'request carries tools; Windlass does not do tool calling and ignores them');

const fig = (v, p, o, src, n) => ({ value: v, predicted: p, observed: o, measured: v != null, source: v == null ? 'none' : src, samples: n });
const facts = () => scenario === 'bare' || cur.state === 'idle'
  ? { schema: 'windlass.model/1', architecture: null, quant: null, layers: null, fileBytes: null, contextLength: null, modelContextLength: null, guessAhead: null,
      speed: { secPerToken: fig(null, null, null), secPerPromptToken: fig(null, null, null), limit: null }, quality: { perplexity: null, tokens: null, measuredAt: null }, fidelity: { score: null, correct: null, total: null, promptTokens: null, version: null, measuredAt: null }, queue: { length: 0, max: 8 } }
  : { schema: 'windlass.model/1', architecture: 'llama', quant: 'Q4_K', layers: 80, fileBytes: 42520395200, contextLength: 8192, modelContextLength: 131072, guessAhead: true,
      speed: { secPerToken: fig(8.1, 7.9, 8.1, 'real-runs', 300), secPerPromptToken: fig(1.2, 1.1, 1.2, 'real-runs', 2927), limit: 'cpu' }, quality: { perplexity: 4.31, tokens: 256, measuredAt: '2026-10-30T17:00:00Z' }, fidelity: { score: 0.75, correct: 3, total: 4, promptTokens: 301, version: 1, measuredAt: '2026-10-30T17:05:00Z' }, queue: { length: 0, max: 8 } };
// The model the mock "has loaded": starts as the scenario says, and POST /api/model changes it (a load takes ~1.5 s).
const cur = { state: scenario === 'loading' ? 'loading' : scenario === 'error' ? 'error' : scenario === 'idle' ? 'idle' : 'ready',
  id: scenario === 'idle' ? null : scenario === 'bare' ? 'windlass-stub' : 'llama-3.3-70b-instruct-q4_k_m',
  path: scenario === 'idle' || scenario === 'bare' ? null : 'D:\\models\\llama-3.3-70b-instruct-q4_k_m.gguf', loadError: scenario === 'error' ? 'InvalidDataException: not a GGUF file' : null };
let replying = 0;
const state = () => cur.state;
const tree = { '': [['C:\\', 'dir'], ['D:\\', 'dir']], 'D:\\': [['models', 'dir']],
  'D:\\models': [['old', 'dir'], ['llama-3.3-70b-instruct-q4_k_m.gguf', 'gguf', 42520395200], ['qwen3-4b-q8_0.gguf', 'gguf', 4280000000], ['too-big-405b.gguf', 'gguf', 243000000000]], 'D:\\models\\old': [['mistral-7b.gguf', 'gguf', 4370000000]] };
const parentOf = (d) => { const t = d.replace(/\\$/, ''); const i = t.lastIndexOf('\\'); if (i < 0) return null; const up = t.slice(0, i); return /^[A-Za-z]:$/.test(up) ? up + '\\' : up || null; };
const recent = ['D:\\models\\qwen3-4b-q8_0.gguf'];
const status = () => ({ schema: 'windlass.ui.status/1', version: '0.2.0', state: state(), loadError: cur.loadError, model: { id: cur.id, path: cur.path, contextLength: state() === 'ready' ? 8192 : null }, canSwap: scenario !== 'bare',
  busy: replying > 0, queued: 0, queueMax: 8, address: `127.0.0.1:${port}`, apiKeyRequired: scenario === 'key', batch: 1, stub: scenario === 'bare', startedAt: new Date(started).toISOString(), uptimeSeconds: Math.floor((Date.now() - started) / 1000),
  autoStop: { enabled: true, ownsLife: process.argv.includes('--owns-life'), armed: hb.count > 0 }, windlass: facts() });
const playbook = () => scenario === 'bare' || cur.state === 'idle' ? null : { schema: 'windlass.playbook/1', created: '2026-10-30T16:40:00Z', model: 'llama-3.3-70b-instruct-q4_k_m',
  machine: { cpu: 'AMD Ryzen 9 9950X3D 16-Core Processor', logicalCores: 32, physicalCores: 16, totalRamGB: 96, freeRamGB: 71.4, onBattery: false, hasBattery: false, gpus: ['Synthetic GPU'] },
  measured: { readGBps: 6.8, layerSecPhysical: 0.083, layerSecLogical: 0.071, layerSecBatch: 0.12 }, observed: { runs: 12, promptTokens: 2927, promptSeconds: 3512, tokens: 300, seconds: 2430, guessed: 120, accepted: 54 },
  quality: { perplexity: 4.31, tokens: 256, promptSecPerToken: 1.2, measured: '2026-10-30T17:00:00Z' }, fidelity: { correct: 3, total: 4, promptTokens: 301, score: 0.75, version: 1, measured: '2026-10-30T17:05:00Z' },
  settings: { cacheGB: 10, prefetch: true, contextLength: 8192, threads: 16, speculate: true, predictedSecPerToken: 7.9, limit: 'cpu' },
  rules: ['RAM: 71.4 GB free of 96 GB; the model streams two layers at a time (about 1.1 GB).', 'Context 8192 tokens: the KV cache is 2.7 GB in fp16.', 'Threads: 16 physical cores were faster than 32 logical ones in the probe.', 'Guess-ahead on: 45% of guesses were kept in real runs.'] };

const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml', '.png': 'image/png', '.ico': 'image/x-icon' };
const json = (res, code, body) => { res.writeHead(code, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }); res.end(JSON.stringify(body)); };
const err = (res, code, message, type) => json(res, code, { error: { message, type } });
const body = (req) => new Promise((ok) => { let t = ''; req.on('data', (c) => (t += c)); req.on('end', () => { try { ok(JSON.parse(t || '{}')); } catch { ok({}); } }); });

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://127.0.0.1:${port}`);
  const p = url.pathname;
  const okToken = req.headers['x-windlass-session'] === TOKEN;
  if (p === '/api/session' && req.method === 'POST') {
    if (scenario === 'key' && req.headers.authorization !== `Bearer ${KEY}`) return err(res, 401, 'missing or wrong API key', 'authentication_error');
    return json(res, 200, { schema: 'windlass.ui.session/1', token: TOKEN, apiKeyRequired: scenario === 'key' });
  }
  if (p === '/api/heartbeat' && req.method === 'POST') {
    const b = await body(req);
    if (!okToken && b.token !== TOKEN) return err(res, 401, 'missing or wrong session token', 'authentication_error');
    hb = { count: hb.count + 1, last: Date.now(), leaving: !!b.leaving };
    return json(res, 200, { ok: true, ownsLife: process.argv.includes('--owns-life'), autoStop: process.argv.includes('--owns-life'), timeoutSeconds: 20, leavingGraceSeconds: 5 });
  }
  if (p === '/mock/heartbeats') return json(res, 200, hb);
  if (p.startsWith('/api/')) {
    if (!okToken) return err(res, 401, 'missing or wrong session token', 'authentication_error');
    if (p === '/api/status') return json(res, 200, status());
    if (p === '/api/playbook') return json(res, 200, { schema: 'windlass.ui.playbook/1', playbook: playbook() });
    if (p === '/api/logs') { const after = Number(url.searchParams.get('after') || 0); return json(res, 200, { schema: 'windlass.ui.logs/1', lines: logs.filter((l) => l.seq > after), last: logs.length }); }
    if (p === '/api/models') {
      const dir = url.searchParams.get('dir') || '';
      const key = dir === '' ? '' : tree[dir] ? dir : tree[dir.replace(/\\$/, '')] ? dir.replace(/\\$/, '') : null;
      if (key === null) return err(res, 404, `no folder at ${dir}`, 'not_found_error');
      const entries = tree[key].map(([name, type, bytes]) => ({ name, path: key === '' ? name : key.endsWith('\\') ? key + name : key + '\\' + name, type, bytes: bytes ?? null }));
      return json(res, 200, { schema: 'windlass.ui.models/1', dir: key || null, parent: key === '' ? null : parentOf(key), entries, truncated: false,
        recent: recent.map((p) => ({ name: p.replace(/^.*\\/, ''), path: p, type: 'gguf', bytes: 4280000000, exists: true })) });
    }
    if (p === '/api/model' && req.method === 'POST') {
      const b = await body(req);
      if (replying > 0) return err(res, 409, 'Windlass is writing a reply; wait for it to finish, then change the model', 'server_busy');
      if (cur.state === 'loading') return err(res, 409, 'A model is loading; wait until it is ready or failed', 'server_busy');
      if (!/\.gguf$/i.test(b.path || '')) return err(res, 400, 'a model file ends in .gguf', 'invalid_request_error');
      const id = b.path.replace(/^.*\\/, '').replace(/\.gguf$/i, '');
      Object.assign(cur, { state: 'loading', id, path: b.path, loadError: null });
      log('INFO', 'loading ' + b.path);
      setTimeout(() => {
        if (/too-big/.test(b.path)) { cur.state = 'error'; cur.loadError = 'ArgumentException: --batch 3 does not fit: 3 KV caches need up to 9.4 GB'; log('ERROR', 'model failed to load: ' + cur.loadError); }
        else { cur.state = 'ready'; log('INFO', id + ': loaded'); if (!recent.includes(b.path)) recent.unshift(b.path); }
      }, Number(process.env.MOCK_LOAD_MS || 1500));
      return json(res, 202, { schema: 'windlass.ui.model/1', state: 'loading', path: b.path, id });
    }
    if (p === '/api/model/unload' && req.method === 'POST') {
      if (replying > 0) return err(res, 409, 'Windlass is writing a reply; wait for it to finish, then unload the model', 'server_busy');
      Object.assign(cur, { state: 'idle', id: null, path: null, loadError: null });
      return json(res, 200, { schema: 'windlass.ui.model/1', state: 'idle', path: null, id: null });
    }
    if (p === '/api/shutdown' && req.method === 'POST') { json(res, 200, { stopping: true }); setTimeout(() => process.exit(0), 300); return; }
    return err(res, 404, `no route for ${req.method} ${p}`, 'not_found_error');
  }
  if (p === '/v1/chat/completions' && req.method === 'POST') {
    if (scenario === 'key' && req.headers.authorization !== `Bearer ${KEY}`) return err(res, 401, 'missing or wrong API key', 'authentication_error');
    if (state() === 'idle') return err(res, 503, 'no model is loaded; load one in the Windlass UI or start it with --model', 'no_model');
    if (state() !== 'ready') return err(res, 503, 'model is still loading', 'model_loading');
    const b = await body(req);
    res.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache' });
    res.write(': accepted\n\n');
    replying++;
    let counted = true;
    const release = () => { if (counted) { counted = false; replying--; } };
    const words = 'Hello from the mock Windlass, layer by layer, one token at a time.'.split(' ');
    let i = 0;
    const timer = setInterval(() => {
      if (i === 0) res.write(': keepalive\n\n');
      if (i < words.length) { res.write(`data: ${JSON.stringify({ choices: [{ index: 0, delta: { content: (i ? ' ' : '') + words[i] }, finish_reason: null }] })}\n\n`); i++; return; }
      res.write(`data: ${JSON.stringify({ choices: [{ index: 0, delta: {}, finish_reason: 'stop' }] })}\n\n`);
      res.write(`data: ${JSON.stringify({ choices: [], usage: { prompt_tokens: (b.messages || []).length * 6, completion_tokens: words.length, total_tokens: words.length + 6 } })}\n\n`);
      res.end('data: [DONE]\n\n'); clearInterval(timer); release();
    }, Number(process.env.MOCK_TOKEN_MS || 120));
    res.on('close', () => { clearInterval(timer); release(); });
    return;
  }
  if (p === '/health') return json(res, 200, { status: state(), model: 'mock', busy: false, queued: 0, error: null });
  const rel = p === '/' ? '/index.html' : p;
  const file = path.join(dist, rel);
  if (file.startsWith(dist) && existsSync(file)) { res.writeHead(200, { 'Content-Type': types[path.extname(file)] ?? 'application/octet-stream', 'Cache-Control': 'no-store' }); return res.end(readFileSync(file)); }
  err(res, 404, `no route for ${req.method} ${p}`, 'not_found_error');
});
server.listen(port, '127.0.0.1', () => console.log(`mock Windlass (${scenario}) on http://127.0.0.1:${port}/`));
