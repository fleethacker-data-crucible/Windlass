// End-to-end check of the built UI (../ui/dist) against the mock server, in a real browser.
//   npm run build && npm run test:e2e
// Needs Playwright with a Chromium: PLAYWRIGHT_CHROMIUM=/path/to/chromium if it is not the bundled one.
import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require(path.join(process.env.NODE_PATH ?? '', 'playwright'))); }

let failed = 0;
const ok = (cond, what) => { if (!cond) { failed++; console.log('FAIL  ' + what); } else console.log('PASS  ' + what); };

function mock(port, scenario, extra = []) {
  const p = spawn(process.execPath, [path.join(here, 'mock-windlass.mjs'), '--port', String(port), '--scenario', scenario, ...extra], { stdio: 'ignore', env: { ...process.env, MOCK_TOKEN_MS: '40' } });
  return { stop: () => p.kill(), url: `http://127.0.0.1:${port}/` };
}
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

const browser = await chromium.launch(process.env.PLAYWRIGHT_CHROMIUM ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM, args: ['--no-sandbox'] } : {});
async function open(url, opts = {}) {
  const ctx = await browser.newContext({ viewport: { width: 1360, height: 820 }, ...opts });
  const page = await ctx.newPage();
  const problems = [];
  page.on('console', (m) => { if (m.type() === 'error' || m.type() === 'warning') problems.push(m.text()); });
  page.on('pageerror', (e) => problems.push(e.message));
  await page.goto(url);
  return { ctx, page, problems };
}

// ready: the whole flow
let m = mock(18911, 'ready'); await wait(600);
{
  const { ctx, page, problems } = await open(m.url, { colorScheme: 'dark' });
  await page.waitForSelector('[data-testid=state-card]');
  ok((await page.textContent('[data-testid=state-card]')).includes('Ready'), 'overview: the model shows as Ready');
  ok((await page.textContent('[data-testid=status-engine]')) === 'Running' && (await page.textContent('[data-testid=status-address]')) === '127.0.0.1:18911', 'status bar: engine word and the bound address');
  ok((await page.textContent('[data-testid=base-url]')) === 'http://127.0.0.1:18911/v1', 'overview: base URL is the bound address');
  ok((await page.evaluate(() => document.documentElement.dataset.app)) === 'windlass', 'html carries data-app="windlass"');
  ok((await page.evaluate(() => getComputedStyle(document.querySelector('aside')).width)) === '240px', 'sidebar is 240 px');
  ok((await page.evaluate(() => getComputedStyle(document.querySelector('header')).height)) === '56px', 'header is 56 px');
  ok((await page.evaluate(() => getComputedStyle(document.querySelector('footer')).height)) === '28px', 'status bar is 28 px');

  // keyboard: g then c goes to Chat; ? opens the help dialog and Esc closes it
  await page.keyboard.press('g'); await page.keyboard.press('c');
  ok((await page.textContent('[data-testid=page-title]')) === 'Chat', 'keyboard: g c opens Chat');
  await page.keyboard.press('?');
  ok(await page.isVisible('dialog[open]'), 'keyboard: ? opens the shortcut list');
  await page.keyboard.press('Escape');
  ok(!(await page.isVisible('dialog[open]')), 'Esc closes the dialog');

  // chat: stream, stats, stop
  await page.fill('[data-testid=chat-input]', 'Hello');
  await page.keyboard.press('Control+Enter');
  await page.waitForSelector('[data-testid=turn-stats]', { timeout: 10000 });
  ok((await page.textContent('[data-testid=turn-assistant]')).includes('Hello from the mock Windlass'), 'chat: the reply streams in');
  ok((await page.textContent('[data-testid=turn-stats]')).includes('13 reply tokens'), 'chat: usage is shown');
  await page.fill('[data-testid=chat-input]', 'Again');
  await page.click('[data-testid=chat-send]');
  await page.waitForSelector('[data-testid=chat-stop]');
  await page.click('[data-testid=chat-stop]');
  await page.waitForSelector('[data-testid=chat-send]');
  ok((await page.locator('[data-testid=turn-stats]').last().textContent()).includes('Stopped'), 'chat: Stop cancels and says so');

  // other pages
  await page.click('[data-testid=nav-playbook]');
  await page.waitForSelector('[data-testid=playbook-settings]');
  ok((await page.textContent('[data-testid=playbook-rules]')).includes('Threads'), 'playbook: rules are listed');
  await page.click('[data-testid=nav-logs]');
  await page.waitForSelector('[data-testid=log-box] div');
  ok((await page.textContent('[data-testid=log-box]')).includes('Windlass 0.3.0'), 'log: lines arrive');

  // theme: override and persist
  await page.click('[data-testid=nav-settings]');
  await page.check('[data-testid=theme-light]');
  ok((await page.evaluate(() => document.documentElement.dataset.theme)) === 'light', 'theme: override sets data-theme');
  await page.reload(); await page.waitForSelector('[data-testid=app-shell]');
  ok((await page.evaluate(() => localStorage.getItem('windlass-theme'))) === 'light' && (await page.evaluate(() => document.documentElement.dataset.theme)) === 'light', 'theme: saved as windlass-theme and applied after a reload');

  // sidebar: collapse to 64 px; narrow window collapses by itself
  await page.click('[data-testid=nav-collapse]');
  await wait(300);
  ok((await page.evaluate(() => getComputedStyle(document.querySelector('aside')).width)) === '64px', 'sidebar collapses to 64 px');
  await page.click('[data-testid=nav-collapse]');
  await page.setViewportSize({ width: 900, height: 700 }); await wait(300);
  ok((await page.evaluate(() => getComputedStyle(document.querySelector('aside')).width)) === '64px', 'under 1024 px the sidebar collapses by itself');
  await page.setViewportSize({ width: 1280, height: 720 });
  ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'no horizontal scroll at 1280 x 720');

  // focus ring is visible on keyboard focus
  await page.keyboard.press('Tab');
  ok((await page.evaluate(() => { const e = document.activeElement; return e ? getComputedStyle(e).outlineStyle : ''; })) !== 'none', 'a focused control shows an outline');
  ok(problems.length === 0, 'no console errors or warnings (' + problems.join(' | ') + ')');
  await ctx.close();
}
m.stop();

// loading and error states
m = mock(18912, 'loading'); await wait(600);
{ const { ctx, page } = await open(m.url); await page.waitForSelector('[data-testid=state-card]');
  ok((await page.textContent('[data-testid=state-card]')).includes('Loading'), 'loading: shown as Loading');
  ok((await page.textContent('[data-testid=status-session]')) === 'Loading model', 'loading: status bar says so');
  await page.click('[data-testid=nav-chat]'); ok(await page.isDisabled('[data-testid=chat-send]'), 'loading: Send is disabled'); await ctx.close(); }
m.stop();
m = mock(18913, 'error'); await wait(600);
{ const { ctx, page } = await open(m.url); await page.waitForSelector('[data-testid=state-card]');
  const t = await page.textContent('[data-testid=state-card]');
  ok(t.includes('Failed') && t.includes('not a GGUF file'), 'error: failure and its reason are shown'); await ctx.close(); }
m.stop();

// never measured: dashes and how to get the number, not zeros
m = mock(18914, 'bare'); await wait(600);
{ const { ctx, page } = await open(m.url); await page.waitForSelector('[data-testid=state-card]');
  const t = await page.textContent('main');
  ok(t.includes('—') && t.includes('playbook probe') && !t.includes('0.00 s/token'), 'bare: missing values are dashes with a hint, never invented numbers');
  await page.click('[data-testid=nav-playbook]'); await page.waitForSelector('[data-testid=playbook-empty]');
  await page.click('[data-testid=nav-chat]');
  ok((await page.textContent('[data-testid=chat-estimate]')).includes('—'), 'bare: the reply-time estimate is a dash'); await ctx.close(); }
m.stop();

// API key
m = mock(18915, 'key'); await wait(600);
{ const { ctx, page } = await open(m.url);
  await page.waitForSelector('[data-testid=key-input]');
  await page.fill('[data-testid=key-input]', 'wrong'); await page.click('[data-testid=key-submit]');
  await page.waitForSelector('[role=alert]');
  await page.fill('[data-testid=key-input]', 'sesame'); await page.click('[data-testid=key-submit]');
  await page.waitForSelector('[data-testid=state-card]');
  ok(true, 'key: the form refuses a wrong key and accepts the right one');
  ok(!(await page.evaluate(() => JSON.stringify(localStorage))).includes('sesame'), 'key: never written to localStorage');
  await page.click('[data-testid=nav-chat]'); await page.fill('[data-testid=chat-input]', 'Hi'); await page.click('[data-testid=chat-send]');
  await page.waitForSelector('[data-testid=turn-stats]');
  ok((await page.textContent('[data-testid=turn-assistant]')).includes('Hello'), 'key: chat sends the key as a Bearer token'); await ctx.close(); }
m.stop();

// the tab owns the server: heartbeats start, and a leaving beacon goes out when the page closes
m = mock(18916, 'ready', ['--owns-life']); await wait(600);
{ const { ctx, page } = await open(m.url); await page.waitForSelector('[data-testid=state-card]'); await wait(800);
  const hb = await (await fetch(m.url + 'mock/heartbeats')).json();
  ok(hb.count >= 1, 'presence: heartbeats start when the server says the tab owns it');
  await page.close({ runBeforeUnload: true }); await wait(500);
  const after = await (await fetch(m.url + 'mock/heartbeats')).json();
  ok(after.leaving === true, 'presence: closing the tab sends the leaving signal'); await ctx.close(); }
m.stop();
// not owned: no heartbeats
m = mock(18917, 'ready'); await wait(600);
{ const { ctx, page } = await open(m.url); await page.waitForSelector('[data-testid=state-card]'); await wait(800);
  ok((await (await fetch(m.url + 'mock/heartbeats')).json()).count === 0, 'presence: no heartbeats when the tab does not own the server'); await ctx.close(); }
m.stop();

// end session
m = mock(18918, 'ready'); await wait(600);
{ const { ctx, page } = await open(m.url); await page.waitForSelector('[data-testid=state-card]');
  await page.click('[data-testid=nav-end]'); await page.waitForSelector('dialog[open]');
  await page.click('[data-testid=confirm-ok]'); await page.waitForSelector('[data-testid=ended]', { timeout: 8000 });
  ok(true, 'end session: confirm, then the page says Windlass has stopped'); await ctx.close(); }
m.stop();

await browser.close();
console.log(failed ? `\n${failed} failed` : '\nall passed');
process.exit(failed ? 1 : 0);
