/**
 * 真机端到端（不依赖 stub 客户端）：
 *   真实 Edge + 真实扩展 + 真实海兔客户端 + 本地 HTTP 服务
 *
 * 做法：
 *   1. 用独立 profile 启动 Edge（不影响用户正在用的窗口），开 CDP
 *   2. 通过 CDP 找到扩展 id，打开扩展弹窗页，写入客户端密钥（等价于用户粘贴）
 *   3. 打开测试页，点真实下载链接
 *   4. 轮询真客户端 /api/stats，断言任务出现且完成
 *
 * 用法：node tools/ext_live_e2e.mjs --secret=<密钥> [--port=16800] [--keep]
 */

import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import fs from 'node:fs';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const EXT_DIR = path.resolve(__dirname, '..', 'extension-v2');

const arg = (k, d) => {
  const hit = process.argv.find((a) => a.startsWith(`--${k}=`));
  return hit ? hit.slice(k.length + 3) : d;
};
const SECRET = arg('secret', '');
const CLIENT_PORT = parseInt(arg('port', '16800'), 10);
const keep = process.argv.includes('--keep');
const CDP_PORT = 9334;
const SRV_PORT = 16830;
const FILE_NAME = 'kokona-e2e-' + Date.now() + '.bin';
const FILE_SIZE = 1024 * 1024;

const payload = Buffer.alloc(FILE_SIZE);
for (let i = 0; i < FILE_SIZE; i++) payload[i] = (i * 13 + 5) & 0xff;

const wait = (ms) => new Promise((r) => setTimeout(r, ms));

/* ---------------- 本地测试服务 ---------------- */

const hits = [];
const server = http.createServer((req, res) => {
  const u = new URL(req.url, 'http://127.0.0.1');
  hits.push(u.pathname + u.search);
  if (u.pathname === '/f.bin') {
    res.writeHead(200, {
      'Content-Type': 'application/octet-stream',
      'Content-Disposition': `attachment; filename="${FILE_NAME}"`,
      'Content-Length': String(payload.length),
    });
    return res.end(payload);
  }
  if (u.pathname === '/page') {
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
    return res.end(`<!doctype html><meta charset="utf-8"><title>kokona e2e</title>
<a id="dl" href="/f.bin">下载测试文件</a>
<script>setTimeout(() => document.getElementById('dl').click(), 600);</script>`);
  }
  res.writeHead(404);
  res.end();
});
await new Promise((r) => server.listen(SRV_PORT, '127.0.0.1', r));

/* ---------------- CDP 小工具 ---------------- */

async function cdpList() {
  const r = await fetch(`http://127.0.0.1:${CDP_PORT}/json/list`);
  return r.json();
}

class Cdp {
  constructor(wsUrl) { this.ws = new WebSocket(wsUrl); this.id = 0; this.pending = new Map(); }
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
  close() { try { this.ws.close(); } catch { } }
}

async function waitFor(fn, timeoutMs, stepMs = 250) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const v = await fn();
    if (v) return v;
    await wait(stepMs);
  }
  return null;
}

/* ---------------- 启动浏览器 ---------------- */

const EDGE = ['C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe'].find((p) => fs.existsSync(p));
if (!EDGE) { console.error('[live] 找不到 Edge'); process.exit(2); }

const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'kokona-live-'));
const edge = spawn(EDGE, [
  `--user-data-dir=${profile}`,
  `--load-extension=${EXT_DIR}`,
  `--disable-extensions-except=${EXT_DIR}`,
  `--remote-debugging-port=${CDP_PORT}`,
  '--no-first-run', '--no-default-browser-check',
  '--disable-features=msEdgeFirstRunExperience,msImplicitSignin,DisableLoadExtensionCommandLineSwitch',
  'about:blank',
], { stdio: 'ignore' });

let failures = [];
try {
  const ok = await waitFor(async () => {
    try { return (await cdpList()).length > 0; } catch { return false; }
  }, 20000);
  if (!ok) throw new Error('CDP 未就绪，Edge 可能没起来');

  // 找 Service Worker 目标，从 URL 里取扩展 id
  const targets = await cdpList();
  const sw = targets.find((t) => t.type === 'service_worker' && t.url.startsWith('chrome-extension://'));
  if (!sw) throw new Error('没找到扩展 Service Worker，扩展可能没加载：' + JSON.stringify(targets.map((t) => t.type)));
  const extId = new URL(sw.url).host;
  console.log('[live] 扩展 id:', extId);

  // 通过浏览器级会话 attach 到扩展的 Service Worker 写配置
  const ver = await (await fetch(`http://127.0.0.1:${CDP_PORT}/json/version`)).json();
  const browser = await new Cdp(ver.webSocketDebuggerUrl).open();
  const attached = await browser.send('Target.attachToTarget', { targetId: sw.id, flatten: true });
  const sessionId = attached.result && attached.result.sessionId;
  if (!sessionId) throw new Error('attach 到扩展 SW 失败: ' + JSON.stringify(attached));

  const settings = {
    host: '127.0.0.1', port: CLIENT_PORT, secret: SECRET,
    autoCapture: true, forceAttachment: false,
  };
  const wrote = await browser.eval(
    `chrome.storage.local.set({settings:${JSON.stringify(settings)}}).then(() => 'ok').catch(e => 'ERR ' + e.message)`,
    true, sessionId);
  const readBack = await browser.eval(
    `chrome.storage.local.get('settings').then(o => JSON.stringify(o.settings || null))`, true, sessionId);
  console.log('[live] 写入配置:', wrote, '回读:', readBack);
  browser.close();

  // 打开测试页
  const page = await (await fetch(
    `http://127.0.0.1:${CDP_PORT}/json/new?http://127.0.0.1:${SRV_PORT}/page`,
    { method: 'PUT' })).json();
  const c2 = await new Cdp(page.webSocketDebuggerUrl).open();

  // 等真实下载被触发
  const gotHit = await waitFor(() => hits.some((h) => h.startsWith('/f.bin')), 15000);
  console.log('[live] 浏览器请求了文件:', !!gotHit);

  // 轮询真客户端
  const task = await waitFor(async () => {
    try {
      const r = await fetch(`http://127.0.0.1:${CLIENT_PORT}/api/stats`, {
        headers: { 'X-Kokona-Secret': SECRET },
      });
      if (!r.ok) return null;
      const d = await r.json();
      const list = d.tasks || d.Tasks || [];
      return list.find((t) => (t.urls || []).some((u) => u.includes('/f.bin'))) || null;
    } catch { return null; }
  }, 30000);

  if (!task) {
    failures.push('客户端没有收到任务（扩展没接管，或密钥/端口不对）');
  } else {
    console.log('[live] 客户端已收到任务:', task.name, task.state);
    const done = await waitFor(async () => {
      const r = await fetch(`http://127.0.0.1:${CLIENT_PORT}/api/stats`, { headers: { 'X-Kokona-Secret': SECRET } });
      const d = await r.json();
      const t = (d.tasks || []).find((x) => x.gid === task.gid);
      return t && (t.state === 'Completed' || t.state === 'Failed') ? t : null;
    }, 60000);
    if (!done) failures.push('任务一直没到终态');
    else if (done.state !== 'Completed') failures.push('任务失败: ' + (done.errorMessage || done.state));
    else {
      const file = path.join(process.env.USERPROFILE || '', 'Downloads', done.name || '');
      const ok2 = fs.existsSync(file) && fs.statSync(file).size === FILE_SIZE;
      console.log('[live] 落盘:', file, ok2 ? '大小正确' : '缺失或大小不符');
      if (!ok2) failures.push('文件没有正确落盘');
      else if (!fs.readFileSync(file).equals(payload)) failures.push('文件内容不一致');
      if (ok2) { try { fs.unlinkSync(file); } catch { } }
    }
  }

  c2.close();
} catch (e) {
  failures.push(e.message);
} finally {
  if (!keep) {
    edge.kill();
    await wait(600);
    try { fs.rmSync(profile, { recursive: true, force: true }); } catch { }
    server.close();
  }
}

if (failures.length) {
  console.error('[live] 失败:\n - ' + failures.join('\n - '));
  process.exit(1);
}
console.log('[live] 通过：真实浏览器 → 扩展 → 真实客户端 → 落盘');
