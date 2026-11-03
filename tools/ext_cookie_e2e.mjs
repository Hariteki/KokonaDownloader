/**
 * EXT-304 端到端：登录态下载。
 *
 * 场景：服务器对**没有 Cookie** 的请求返回 403，只有带上会话 Cookie 才给文件。
 * 这正好对应"需要登录才能下"的站点。要证明的是：
 *   浏览器里的 Cookie → 扩展采集 → 随 /api/download 发出 → 客户端带着它能拿到 200。
 *
 * 两种模式：
 *   --client=stub  假客户端：记录扩展发来的请求，并**真的**用收到的头去下载一次，
 *                  同时发一次"不带 Cookie"的请求做对照（应当 403）。默认。
 *   --client=real  真客户端：把扩展指向用户正在运行的海兔（默认 16800），
 *                  验证任务真的完成、文件逐字节一致；测完调用 remove 接口清掉任务。
 *
 * 用法：
 *   node tools/ext_cookie_e2e.mjs
 *   node tools/ext_cookie_e2e.mjs --client=real --secret=<密钥>
 * 退出码：0=PASS / 1=FAIL / 2=环境不支持
 */

import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import fs from 'node:fs';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const EXT_DIR = path.resolve(__dirname, '..', 'extension-v2');
const EDGE = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const CDP_PORT = 9336;

const arg = (name, dflt = null) => {
  const hit = process.argv.find((a) => a.startsWith(name + '='));
  return hit ? hit.slice(name.length + 1) : dflt;
};
const MODE = arg('--client', 'stub');
const IS_REAL = MODE === 'real';
let CLIENT_PORT = IS_REAL ? Number(arg('--port', '16800')) : 0; // stub 模式稍后回填
const SECRET = arg('--secret', 's3cret');

const DBG = (...a) => console.log('[cookie-e2e]', ...a);
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

/* ---------------- 1. 受保护的文件服务 ---------------- */

const PAYLOAD = Buffer.alloc(256 * 1024, 0);
for (let i = 0; i < PAYLOAD.length; i++) PAYLOAD[i] = (i * 31 + 7) & 0xff;

const seen = [];
const fileServer = http.createServer((req, res) => {
  const u = new URL(req.url, 'http://127.0.0.1');
  const cookie = req.headers.cookie || '';
  seen.push({ path: u.pathname, cookie });
  if (u.pathname === '/') {
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
    res.end('<!doctype html><title>ok</title>ok');
    return;
  }
  if (u.pathname === '/auth/file.bin') {
    if (!/(^|;\s*)session=letmein(;|$)/.test(cookie)) {
      res.writeHead(403, { 'Content-Type': 'text/plain; charset=utf-8' });
      res.end('forbidden: login required');
      return;
    }
    res.writeHead(200, {
      'Content-Type': 'application/octet-stream',
      'Content-Length': PAYLOAD.length,
      // 让浏览器一定走下载流程，从而触发 downloads.onCreated
      'Content-Disposition': 'attachment; filename="secret.bin"',
      'Cache-Control': 'no-store',
    });
    res.end(PAYLOAD);
    return;
  }
  res.writeHead(404); res.end();
});
await new Promise((r) => fileServer.listen(0, '127.0.0.1', r));
const FILE_PORT = fileServer.address().port;
const FILE_URL = `http://127.0.0.1:${FILE_PORT}/auth/file.bin`;
DBG('受保护地址', FILE_URL);

/* ---------------- 2. 假客户端（stub 模式） ---------------- */

const stubResults = [];
let stubServer = null;

function json(res, code, obj) {
  res.writeHead(code, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify(obj));
}

if (!IS_REAL) {
  stubServer = http.createServer((req, res) => {
    let body = '';
    req.on('data', (c) => { body += c; });
    req.on('end', async () => {
      if (req.url === '/api/ping') return json(res, 200, { ok: true, version: 'stub' });
      if (req.url === '/api/stats') return json(res, 200, { ok: true, tasks: [] });
      if (req.url === '/api/settings') return json(res, 200, { interceptBrowserDownloads: true });
      if (req.url !== '/api/download') return json(res, 404, { ok: false });

      const payload = JSON.parse(body || '{}');
      const url = (payload.urls || [])[0];
      const headers = payload.headers || [];
      const cookieHeader = headers
        .filter((h) => /^cookie:/i.test(h))
        .map((h) => h.slice(h.indexOf(':') + 1).trim())
        .join('; ');

      // 客户端收到请求后会带着这些头去下载。这里如实模拟：
      // 一次带上扩展给的 Cookie，一次完全不带，用来证明"是这个 Cookie 解的锁"。
      const withCookie = await fetch(url, { headers: cookieHeader ? { cookie: cookieHeader } : {} });
      const withBuf = Buffer.from(await withCookie.arrayBuffer());
      const without = await fetch(url);

      stubResults.push({
        url,
        cookieHeader,
        hasUserAgent: headers.some((h) => /^user-agent:/i.test(h)),
        referer: payload.referer || '',
        withStatus: withCookie.status,
        withBytes: withBuf.length,
        identical: withBuf.equals(PAYLOAD),
        withoutStatus: without.status,
      });
      json(res, 200, { ok: true, gid: 'stub-1' });
    });
  });
  await new Promise((r) => stubServer.listen(0, '127.0.0.1', r));
  CLIENT_PORT = stubServer.address().port;
}
const CLIENT_BASE = `http://127.0.0.1:${CLIENT_PORT}`;
DBG('客户端', IS_REAL ? `真客户端 ${CLIENT_BASE}` : `假客户端 ${CLIENT_BASE}`);

/* ---------------- 3. 扩展副本（可选站点权限提升为必需，免掉交互弹窗） ---------------- */

const extCopy = fs.mkdtempSync(path.join(os.tmpdir(), 'cookie-ext-'));
fs.cpSync(EXT_DIR, extCopy, {
  recursive: true,
  filter: (src) => !src.includes(`${path.sep}node_modules`) && !src.includes(`${path.sep}test`),
});
{
  const mfPath = path.join(extCopy, 'manifest.json');
  const mf = JSON.parse(fs.readFileSync(mfPath, 'utf8'));
  mf.host_permissions = Array.from(new Set([...(mf.host_permissions || []), ...(mf.optional_host_permissions || [])]));
  delete mf.optional_host_permissions;
  fs.writeFileSync(mfPath, JSON.stringify(mf, null, 2));
}

/* ---------------- 4. 启动浏览器 ---------------- */

const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'cookie-profile-'));
const args = [
  `--user-data-dir=${profile}`,
  `--disable-extensions-except=${extCopy}`,
  `--load-extension=${extCopy}`,
  `--remote-debugging-port=${CDP_PORT}`,
  '--no-first-run',
  '--no-default-browser-check',
  '--disable-features=msEdgeFirstRunExperience,msImplicitSignin',
];
if (process.env.COOKIE_HEADLESS !== '0') args.unshift('--headless=new');
args.push('about:blank');

const proc = spawn(EDGE, args, { stdio: 'ignore' });

async function waitFor(fn, ms = 15000) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    try { const v = await fn(); if (v) return v; } catch { /* 继续 */ }
    await wait(200);
  }
  return null;
}

const version = await waitFor(async () => {
  const r = await fetch(`http://127.0.0.1:${CDP_PORT}/json/version`);
  return r.ok ? r.json() : null;
}, 20000);
if (!version) {
  DBG('CDP 连不上');
  proc.kill(); fileServer.close(); if (stubServer) stubServer.close();
  process.exit(2);
}
DBG('浏览器', version['Browser']);

/* ---------------- 5. CDP ---------------- */

const ws = new WebSocket(version.webSocketDebuggerUrl);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });

let seq = 0;
const pending = new Map();
const events = [];
ws.onmessage = (m) => {
  const d = JSON.parse(m.data);
  if (d.id && pending.has(d.id)) { pending.get(d.id)(d); pending.delete(d.id); }
  else if (d.method) events.push(d);
};
const send = (method, params = {}, sessionId) => new Promise((res) => {
  const id = ++seq;
  const msg = { id, method, params };
  if (sessionId) msg.sessionId = sessionId;
  const timer = setTimeout(() => {
    if (pending.has(id)) { pending.delete(id); res({ error: { message: 'timeout: ' + method } }); }
  }, 12000);
  pending.set(id, (v) => { clearTimeout(timer); res(v); });
  ws.send(JSON.stringify(msg));
});

async function evaluate(expr, sessionId) {
  const r = await send('Runtime.evaluate', {
    expression: expr, awaitPromise: true, returnByValue: true,
  }, sessionId);
  if (r.error) return { error: r.error.message };
  const res = r.result && r.result.result;
  if (!res) return { error: 'no result' };
  if (res.subtype === 'error') return { error: res.description || 'js error' };
  return { value: res.value };
}

async function openTab(url) {
  const t = await send('Target.createTarget', { url });
  const tid = t.result && t.result.targetId;
  const a = await send('Target.attachToTarget', { targetId: tid, flatten: true });
  return { tid, sid: a.result && a.result.sessionId };
}

/* ---------------- 6. 找扩展 id ---------------- */

await send('Target.setDiscoverTargets', { discover: true });
await wait(500);
const targets = await send('Target.getTargets');
let extId = null;
for (const t of (targets.result && targets.result.targetInfos) || []) {
  if (t.type === 'service_worker' && /^chrome-extension:\/\/[a-z]+\/sw\/background\.js/.test(t.url || '')) {
    extId = t.url.split('/')[2];
    break;
  }
}
if (!extId) {
  extId = await waitFor(() => {
    try {
      const pref = JSON.parse(fs.readFileSync(path.join(profile, 'Default', 'Preferences'), 'utf8'));
      const settings = (pref.extensions && pref.extensions.settings) || {};
      const hit = Object.entries(settings).find(([, v]) => String(v.path || '').toLowerCase().includes('cookie-ext'));
      return hit ? hit[0] : null;
    } catch { return null; }
  }, 8000);
}
if (!extId) {
  DBG('拿不到扩展 id');
  proc.kill(); fileServer.close(); if (stubServer) stubServer.close();
  process.exit(2);
}
DBG('扩展 id', extId);

/* ---------------- 7. 配置扩展指向我们的"客户端" ---------------- */

const opt = await openTab(`chrome-extension://${extId}/ui/options.html`);
await wait(700);
const saved = await evaluate(
  `chrome.runtime.sendMessage({type:'save-settings', patch:{
      host:'127.0.0.1', port:${CLIENT_PORT}, secret:${JSON.stringify(SECRET)},
      autoCapture:true, forceAttachment:false}})
     .then((r) => JSON.stringify(r && r.ok)).catch((e) => 'ERR ' + e.message)`,
  opt.sid);
DBG('保存设置 →', saved.value);

/* ---------------- 8. 在浏览器里种下会话 Cookie ---------------- */

const tab = await openTab('about:blank');
await send('Network.enable', {}, tab.sid);
await send('Page.enable', {}, tab.sid);
await send('Page.navigate', { url: `http://127.0.0.1:${FILE_PORT}/` }, tab.sid);
await waitFor(() => events.some((e) => e.method === 'Network.responseReceived'
  && e.sessionId === tab.sid
  && e.params.response.url === `http://127.0.0.1:${FILE_PORT}/`), 8000);

const setCookie = await send('Network.setCookie', {
  name: 'session', value: 'letmein', url: `http://127.0.0.1:${FILE_PORT}/`, path: '/',
}, tab.sid);
DBG('种 Cookie →', JSON.stringify(setCookie.result || setCookie.error));

const gotCookies = await send('Network.getCookies', {
  urls: [`http://127.0.0.1:${FILE_PORT}/`],
}, tab.sid);
const names = ((gotCookies.result && gotCookies.result.cookies) || []).map((c) => `${c.name}=${c.value}`);
DBG('浏览器内的 Cookie →', JSON.stringify(names));

const dlDir = fs.mkdtempSync(path.join(os.tmpdir(), 'cookie-dl-'));
await send('Browser.setDownloadBehavior', { behavior: 'allow', downloadPath: dlDir, eventsEnabled: true });

/* ---------------- 9. 触发下载 ---------------- */

DBG('导航到受保护地址…');
await send('Page.navigate', { url: FILE_URL }, tab.sid);

/* ---------------- 10. 收结果 ---------------- */

let ok = false;
let detail = {};

if (!IS_REAL) {
  const got = await waitFor(() => stubResults.length > 0, 20000);
  if (!got) {
    DBG('假客户端 20 秒内没有收到 /api/download');
    DBG('文件服务的请求记录:', JSON.stringify(seen));
  } else {
    const r = stubResults[0];
    detail = r;
    DBG('假客户端收到 →', JSON.stringify(r));
    ok = r.withStatus === 200
      && r.identical === true
      && r.withoutStatus === 403
      && /session=letmein/.test(r.cookieHeader)
      && r.hasUserAgent === true;
  }
} else {
  // 真客户端：轮询任务，等到终态
  const headers = { 'X-Kokona-Secret': SECRET };
  const found = await waitFor(async () => {
    const r = await fetch(`${CLIENT_BASE}/api/tasks`, { headers });
    if (!r.ok) return null;
    const list = await r.json();
    const arr = Array.isArray(list) ? list : (list.tasks || []);
    return arr.find((t) => JSON.stringify(t.urls || t.Urls || []).includes('/auth/file.bin')) || null;
  }, 30000);

  if (!found) {
    DBG('真客户端没有创建任务');
  } else {
    const gid = found.gid || found.Gid;
    const state = found.state || found.State;
    DBG('任务 →', gid, state);
    const done = await waitFor(async () => {
      const r = await fetch(`${CLIENT_BASE}/api/tasks`, { headers });
      const list = await r.json();
      const arr = Array.isArray(list) ? list : (list.tasks || []);
      const t = arr.find((x) => (x.gid || x.Gid) === gid);
      const st = t && (t.state || t.State);
      return st && String(st).toLowerCase() === 'completed' ? t : null;
    }, 60000);

    const settings = JSON.parse(fs.readFileSync(
      path.join(os.homedir(), 'AppData', 'Roaming', 'KokonaDownloader', 'settings.json'), 'utf8'));
    const dir = settings.DefaultDownloadDir || path.join(os.homedir(), 'Downloads');
    const file = path.join(dir, (done && (done.name || done.Name)) || 'secret.bin');
    const exists = fs.existsSync(file);
    const bytes = exists ? fs.readFileSync(file) : Buffer.alloc(0);
    detail = { gid, state: done ? 'Completed' : 'timeout', file, size: bytes.length, identical: bytes.equals(PAYLOAD) };
    DBG('落盘 →', JSON.stringify(detail));
    ok = !!done && detail.identical;

    // 清理：调用 remove 接口，别把测试任务留在用户列表里
    try {
      // HttpListener 对没有 Content-Length 的 POST 直接回 411，必须显式给空 body
      await fetch(`${CLIENT_BASE}/api/tasks/${gid}/remove`, { method: 'POST', headers, body: '' });
      fs.rmSync(file, { force: true });
      DBG('已清理任务与文件');
    } catch (e) { DBG('清理失败', e.message); }
  }
}

/* ---------------- 11. 判定 ---------------- */

const browserSentCookie = seen.some((s) => s.path === '/auth/file.bin' && /session=letmein/.test(s.cookie));
DBG('浏览器请求是否带 Cookie →', browserSentCookie);
DBG('文件服务收到的请求 →', JSON.stringify(seen.map((s) => ({ p: s.path, c: s.cookie ? 'yes' : 'no' }))));

console.log('');
if (!IS_REAL) {
  console.log('  浏览器带着 Cookie 请求文件   :', browserSentCookie ? 'PASS' : 'FAIL');
  console.log('  扩展把 Cookie 发给了客户端   :', /session=letmein/.test(detail.cookieHeader || '') ? 'PASS' : 'FAIL');
  console.log('  同时带上 User-Agent          :', detail.hasUserAgent ? 'PASS' : 'FAIL');
  console.log('  带 Cookie 下载成功且字节一致 :', (detail.withStatus === 200 && detail.identical) ? 'PASS' : 'FAIL');
  console.log('  对照：不带 Cookie 被拒 403   :', detail.withoutStatus === 403 ? 'PASS' : 'FAIL');
} else {
  console.log('  任务完成并落盘               :', detail.state === 'Completed' ? 'PASS' : 'FAIL');
  console.log('  文件逐字节一致               :', detail.identical ? 'PASS' : 'FAIL');
}

ws.close();
proc.kill();
fileServer.close();
if (stubServer) stubServer.close();
setTimeout(() => {
  try { fs.rmSync(profile, { recursive: true, force: true }); } catch { }
  try { fs.rmSync(extCopy, { recursive: true, force: true }); } catch { }
  try { fs.rmSync(dlDir, { recursive: true, force: true }); } catch { }
}, 0);

process.exit(ok ? 0 : 1);
