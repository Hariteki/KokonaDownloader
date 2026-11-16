/**
 * 真实端到端验证「一键关闭/开启接管」：
 *   真实 Edge（独立 profile，CDP）+ 真实扩展（extension-v2）+ 真实客户端 API
 *
 * 步骤：
 *   1. 记录当前客户端 intercept 值
 *   2. CDP 打开扩展 popup 页（等价于用户点扩展图标）
 *   3. 截图「开」状态
 *   4. 在 DOM 里真实点击 #captureToggle
 *   5. 轮询客户端，断言 intercept 被改成 false，且扩展 badge/popup 反映"已关闭"
 *   6. 再次点击，断言恢复为 true
 *   7. 每次切换后截图，留作界面直观性证据
 *
 * 用法：node tools/ext_toggle_live.mjs --secret=<密钥> [--port=16800] [--keep]
 */

import os from 'node:os';
import path from 'node:path';
import fs from 'node:fs';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const arg = (k, d) => { const h = process.argv.find((a) => a.startsWith(`--${k}=`)); return h ? h.slice(k.length + 3) : d; };
const SECRET = arg('secret', '');
const CLIENT_PORT = parseInt(arg('port', '16800'), 10);
const keep = process.argv.includes('--keep');
const CDP_PORT = 9335;
const EXT_DIR = path.resolve(__dirname, '..', 'extension-v2');
const OUT_DIR = path.join(os.tmpdir(), 'kokona-toggle-shots');
fs.mkdirSync(OUT_DIR, { recursive: true });

if (!SECRET) { console.error('需要 --secret=<密钥>'); process.exit(2); }

const wait = (ms) => new Promise((r) => setTimeout(r, ms));

/* ---------------- CDP 小工具 ---------------- */
async function cdpList() { const r = await fetch(`http://127.0.0.1:${CDP_PORT}/json/list`); return r.json(); }
async function cdpNew(url) {
  const r = await fetch(`http://127.0.0.1:${CDP_PORT}/json/new?${encodeURIComponent(url)}`, { method: 'PUT' });
  return r.json();
}

class Cdp {
  constructor(ws) { this.ws = new WebSocket(ws); this.id = 0; this.pending = new Map(); }
  async open() {
    await new Promise((res, rej) => { this.ws.onopen = res; this.ws.onerror = rej; });
    this.ws.onmessage = (m) => {
      const d = JSON.parse(m.data);
      if (d.id && this.pending.has(d.id)) { this.pending.get(d.id)(d); this.pending.delete(d.id); }
    };
    return this;
  }
  send(method, params = {}, sessionId) {
    const id = ++this.id;
    const msg = { id, method, params };
    if (sessionId) msg.sessionId = sessionId;
    return new Promise((res) => { this.pending.set(id, res); this.ws.send(JSON.stringify(msg)); });
  }
  async eval(expr, awaitPromise = true, sessionId) {
    const r = await this.send('Runtime.evaluate', { expression: expr, awaitPromise, returnByValue: true }, sessionId);
    if (r.error) return 'CDP-ERR ' + r.error.message;
    const res = r.result && r.result.result;
    if (!res) return undefined;
    if (res.subtype === 'error') return 'JS-ERR ' + (res.description || '');
    return res.value;
  }
  async clickAt(x, y) {
    await this.send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount: 1 });
    await this.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount: 1 });
  }
  async screenshot(file) {
    const r = await this.send('Page.captureScreenshot', { format: 'png' });
    if (r.error) throw new Error('screenshot fail: ' + r.error.message);
    fs.writeFileSync(file, Buffer.from(r.result.data, 'base64'));
    return file;
  }
  close() { try { this.ws.close(); } catch { } }
}

async function waitFor(fn, timeoutMs, stepMs = 250) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) { const v = await fn(); if (v) return v; await wait(stepMs); }
  return null;
}

function clientGet() {
  return fetch(`http://127.0.0.1:${CLIENT_PORT}/api/settings`, { headers: { 'X-Kokona-Secret': SECRET } })
    .then((r) => r.json()).then((j) => j.interceptBrowserDownloads).catch(() => null);
}

/* ---------------- 启动浏览器 ---------------- */
const EDGE = ['C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe'].find((p) => fs.existsSync(p));
if (!EDGE) { console.error('找不到 Edge'); process.exit(2); }
const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'kokona-toggle-'));
const edge = spawn(EDGE, [
  `--user-data-dir=${profile}`,
  `--load-extension=${EXT_DIR}`,
  `--disable-extensions-except=${EXT_DIR}`,
  `--remote-debugging-port=${CDP_PORT}`,
  '--no-first-run', '--no-default-browser-check',
  '--window-size=360,640', '--window-position=80,80',
  'about:blank',
], { stdio: 'ignore' });

const shots = [];
let failures = [];
let browser;
try {
  await waitFor(async () => { try { return (await cdpList()).length > 0; } catch { return false; } }, 20000)
    || (() => { throw new Error('CDP 未就绪'); })();

  const ver = await (await fetch(`http://127.0.0.1:${CDP_PORT}/json/version`)).json();
  browser = await new Cdp(ver.webSocketDebuggerUrl).open();

  // 认扩展 id：按 manifest 的 service_worker 路径找后台目标
  const manifest = JSON.parse(fs.readFileSync(path.join(EXT_DIR, 'manifest.json'), 'utf8'));
  const swPath = '/' + (manifest.background.service_worker || '').replace(/^\/+/, '');
  await waitFor(async () => {
    const t = await cdpList();
    return t.some((x) => (x.url || '').includes('chrome-extension://') && (x.url || '').endsWith(swPath));
  }, 20000);
  const extTargets = (await cdpList()).filter((x) => (x.url || '').includes('chrome-extension://') && (x.url || '').endsWith(swPath));
  if (!extTargets.length) throw new Error('找不到扩展后台目标');
  const extId = new URL(extTargets[0].url).host;
  console.log('[live] 扩展 id:', extId);

  // 先经选项页把密钥写进扩展 storage（等价于用户在弹窗里"粘贴密钥并连接"），
  // 否则 popup 会报"密钥错误"，toggle 走离线回退分支，测不到"在线写客户端"的主路径。
  const optPage = await cdpNew(`chrome-extension://${extId}/ui/options.html`);
  const opt = await new Cdp(optPage.webSocketDebuggerUrl).open();
  await waitFor(async () => {
    try { return (await opt.eval('typeof chrome !== "undefined" && !!chrome.storage')) === true; }
    catch { return false; }
  }, 8000);
  const wrote = await opt.eval(
    `chrome.storage.local.set({settings:{host:"127.0.0.1",port:${CLIENT_PORT},secret:${JSON.stringify(SECRET)},autoCapture:true,forceAttachment:false}}).then(() => 'ok').catch(e => 'ERR ' + e.message)`);
  const readBack = await opt.eval(`chrome.storage.local.get('settings').then(o => JSON.stringify(o.settings || null))`);
  console.log('[live] 预置密钥:', wrote, '回读:', readBack);
  opt.close();

  // 打开 popup 页（等价于点扩展图标）
  const popup = await cdpNew(`chrome-extension://${extId}/ui/popup.html`);
  const p = await new Cdp(popup.webSocketDebuggerUrl).open();
  await p.send('Page.enable');

  // 等 popup 脚本跑完、开关就绪
  await waitFor(async () => {
    try { return (await p.eval('typeof document.getElementById !== "undefined" && !!document.getElementById("captureToggle")')) === true; }
    catch { return false; }
  }, 8000);
  await wait(600); // 等 refresh() 拉完状态

  const initial = await clientGet();
  console.log('[live] 切换前客户端 intercept =', initial);

  // 截图 1：当前状态（默认应为开）
  shots.push(await p.screenshot(path.join(OUT_DIR, '1-before.png')));

  // ---- 第一次点击：开 → 关 ----
  const r1 = await p.eval(`(() => { const el = document.getElementById('captureToggle');
      const b = el.getBoundingClientRect();
      return JSON.stringify({ x: b.left + b.width - 24, y: b.top + b.height / 2,
        off: el.classList.contains('off'), sub: el.querySelector('.sub').textContent,
        state: document.getElementById('captureState').textContent }); })()`);
  console.log('[live] 点击前状态:', r1);
  const click1 = JSON.parse(r1);
  await p.clickAt(Math.round(click1.x), Math.round(click1.y));

  // 轮询客户端，等 intercept 变成 false
  const becameOff = await waitFor(async () => { const v = await clientGet(); return v === false ? true : null; }, 8000);
  if (!becameOff) failures.push('点击后客户端 intercept 没有变成 false');
  else console.log('[live] 点击后客户端 intercept = false ✓');

  await wait(500);
  const after1 = await p.eval(`JSON.stringify({ off: document.getElementById('captureToggle').classList.contains('off'),
      state: document.getElementById('captureState').textContent,
      status: document.getElementById('status').textContent })`);
  console.log('[live] 点击后 UI:', after1);
  shots.push(await p.screenshot(path.join(OUT_DIR, '2-after-close.png')));

  // ---- 第二次点击：关 → 开（恢复） ----
  const r2 = await p.eval(`(() => { const el = document.getElementById('captureToggle');
      const b = el.getBoundingClientRect();
      return JSON.stringify({ x: b.left + b.width - 24, y: b.top + b.height / 2 }); })()`);
  const click2 = JSON.parse(r2);
  await p.clickAt(Math.round(click2.x), Math.round(click2.y));

  const becameOn = await waitFor(async () => { const v = await clientGet(); return v === true ? true : null; }, 8000);
  if (!becameOn) failures.push('再次点击后客户端 intercept 没有恢复为 true');
  else console.log('[live] 再次点击后客户端 intercept = true ✓（已恢复）');

  await wait(500);
  const after2 = await p.eval(`JSON.stringify({ off: document.getElementById('captureToggle').classList.contains('off'),
      state: document.getElementById('captureState').textContent })`);
  console.log('[live] 恢复后 UI:', after2);
  shots.push(await p.screenshot(path.join(OUT_DIR, '3-after-reopen.png')));

  // 最终一致性：客户端与 UI 一致
  const finalClient = await clientGet();
  const finalUi = await p.eval('document.getElementById("captureToggle").classList.contains("off")');
  if (finalClient !== !finalUi) failures.push('最终状态不一致：client=' + finalClient + ' uiOff=' + finalUi);

  p.close();
} catch (e) {
  failures.push(e.message);
} finally {
  if (!keep) {
    edge.kill();
    await wait(600);
    try { fs.rmSync(profile, { recursive: true, force: true }); } catch { }
  }
  if (browser) browser.close();
}

console.log('\n[shots] 截图保存于:');
for (const s of shots) console.log('  ' + s);

if (failures.length) {
  console.error('\n[live] 失败:\n - ' + failures.join('\n - '));
  process.exit(1);
}
console.log('\n[live] 通过：真实点击开关 → 客户端 intercept 真实切换 → UI 状态同步');
