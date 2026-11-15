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
const argOf = (k, d) => {
  const hit = process.argv.find((a) => a.startsWith(`--${k}=`));
  return hit ? hit.slice(k.length + 3) : d;
};
// 允许指向扩展副本（例如加了打点的 artifacts/ext-debug），默认用仓库里的正式版
const EXT_DIR = path.resolve(__dirname, '..', argOf('ext-dir', 'extension-v2'));

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
    // 必须真的支持 Range：扩展会先探测（Range 请求要回 206），
    // 不支持分段的链接现在**故意放给浏览器**（两周前新增的行为）。
    // 这个服务原先只会回 200，导致脚本一直报"扩展没接管"，其实是产品在按预期工作。
    const m = /^bytes=(\d+)-(\d*)$/.exec(req.headers.range || '');
    if (m) {
      const start = Number(m[1]);
      const end = m[2] ? Math.min(Number(m[2]), payload.length - 1) : payload.length - 1;
      res.writeHead(206, {
        'Content-Type': 'application/octet-stream',
        'Content-Disposition': `attachment; filename="${FILE_NAME}"`,
        'Content-Length': String(end - start + 1),
        'Content-Range': `bytes ${start}-${end}/${payload.length}`,
        'Accept-Ranges': 'bytes',
      });
      return res.end(payload.subarray(start, end + 1));
    }
    res.writeHead(200, {
      'Content-Type': 'application/octet-stream',
      'Content-Disposition': `attachment; filename="${FILE_NAME}"`,
      'Content-Length': String(payload.length),
      'Accept-Ranges': 'bytes',
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
  // MV3 的后台在"还没被事件唤醒"时，Edge 会把它报成 background_page 而不是 service_worker；
  // 两种都要认，否则脚本会误判成"扩展没加载"（实测 Edge 136 上就是这种表现）。
  // 我们的扩展是 MV3：目标 URL 一定是 chrome-extension://<id>/<manifest 里的 service_worker 路径>。
  // Edge 自带的那几个是 MV2 的 _generated_background_page.html，按路径就能直接排掉。
  const manifest = JSON.parse(fs.readFileSync(path.join(EXT_DIR, 'manifest.json'), 'utf8'));
  const swPath = '/' + (manifest.background && manifest.background.service_worker || '').replace(/^\/+/, '');

  // SW 是懒启动的：刚拉起浏览器时它可能还没出现，等一会儿
  let extTargets = [];
  await waitFor(async () => {
    const t = await cdpList();
    extTargets = t.filter((x) => (x.url || '').includes('chrome-extension://') && (x.url || '').endsWith(swPath));
    return extTargets.length > 0;
  }, 20000);
  if (extTargets.length === 0)
    throw new Error(`等不到扩展的后台（期望路径 ${swPath}）：`
      + JSON.stringify((await cdpList()).map((t) => [t.type, t.url])));

  const ver = await (await fetch(`http://127.0.0.1:${CDP_PORT}/json/version`)).json();
  const browser = await new Cdp(ver.webSocketDebuggerUrl).open();

  // 不能取"第一个 chrome-extension:// 目标"：Edge 自带若干扩展（它们报 _generated_background_page.html），
  // 真机实测正是这个坑——密钥被写进了 Edge 自带扩展的 storage，我们的扩展读到的 settings 是空的，
  // 于是一路"接管正常、探测通过、最后 401 密钥缺失"，看起来像扩展根本不工作。
  // 按 manifest 里的 service_worker 路径就能唯一认出自己的后台（上面 filter 已保证）。
  const sw = extTargets[0];
  const extId = new URL(sw.url).host;
  console.log('[live] 扩展 id:', extId, '（按 service_worker 路径认领）');

  // 写配置不在 SW 上下文里做：MV3 的 SW 目标上 Runtime.evaluate 拿不到 chrome 命名空间
  // （实测 "chrome is not defined"）。打开扩展自己的选项页，页面上下文里 chrome API 是齐的。
  const optPage = await (await fetch(
    `http://127.0.0.1:${CDP_PORT}/json/new?chrome-extension://${extId}/ui/options.html`,
    { method: 'PUT' })).json();
  const optConn = await new Cdp(optPage.webSocketDebuggerUrl).open();
  const inPage = (expr) => optConn.eval(expr, true);

  // 页面刚建出来时脚本还没跑，chrome.storage 可能还没挂上——等它就绪再写
  await waitFor(async () => {
    try { return (await inPage('typeof chrome !== "undefined" && !!chrome.storage')) === true; }
    catch { return false; }
  }, 8000);

  const settings = {
    host: '127.0.0.1', port: CLIENT_PORT, secret: SECRET,
    autoCapture: true, forceAttachment: false,
  };
  const wrote = await inPage(
    `chrome.storage.local.set({settings:${JSON.stringify(settings)}}).then(() => 'ok').catch(e => 'ERR ' + e.message)`);
  const readBack = await inPage(
    `chrome.storage.local.get('settings').then(o => JSON.stringify(o.settings || null))`);
  console.log('[live] 写入配置:', wrote, '回读:', readBack);

  if (process.argv.includes('--diag')) {
    // 诊断模式：查站点权限、ping、并直接 POST 一次 /api/download 验证扩展→客户端这条路。
    // 注意 POST 会在客户端留下一条失败任务（diag.bin，连的是 127.0.0.1:1），看完记得清列表。
    const perm = await inPage(
      `chrome.permissions.contains({origins:['http://*/*']}).then(v => 'hostAccess=' + v)`);
    const ping = await inPage(
      `fetch('http://127.0.0.1:${CLIENT_PORT}/api/ping')
         .then(r => 'ping ' + r.status).catch(e => 'ping ERR ' + e.message)`);
    const post = await inPage(
      `fetch('http://127.0.0.1:${CLIENT_PORT}/api/download', {
         method: 'POST',
         headers: { 'Content-Type': 'application/json', 'X-Kokona-Secret': ${JSON.stringify(SECRET)} },
         body: JSON.stringify({ urls: ['http://127.0.0.1:1/diag.bin'] })
       }).then(r => 'post ' + r.status).catch(e => 'post ERR ' + e.message)`);
    console.log('[diag]', perm, '|', ping, '|', post);
    browser.close();
    process.exit(0);
  }

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
      // /api/stats 只返回统计（没有任务数组）：从那里找 task 永远找不到，
      // 于是脚本不管扩展有没有接管都会报"没收到任务"。要查 /api/tasks。
      const r = await fetch(`http://127.0.0.1:${CLIENT_PORT}/api/tasks`, {
        headers: { 'X-Kokona-Secret': SECRET },
      });
      if (!r.ok) return null;
      const list = await r.json();
      return (list || []).find((t) => (t.urls || []).some((u) => u.includes('/f.bin'))) || null;
    } catch { return null; }
  }, 30000);

  if (!task) {
    // 失败时把扩展内部状态打出来：下载项是 complete/cancelled、以及 storage 里的转发记录，
    // 能直接区分"没接管"与"接管了但请求没发出去"
    try {
      const ver2 = await (await fetch(`http://127.0.0.1:${CDP_PORT}/json/version`)).json();
      const b2 = await new Cdp(ver2.webSocketDebuggerUrl).open();
      const t2 = await cdpList();
      const bg = t2.find((t) => (t.type === 'service_worker' || t.type === 'background_page')
        && (t.url || '').startsWith('chrome-extension://'));
      if (bg) {
        const at2 = await b2.send('Target.attachToTarget', { targetId: bg.id, flatten: true });
        const sid2 = at2.result && at2.result.sessionId;
        const dl = await b2.eval(
          `chrome.downloads.search({}).then(l => JSON.stringify(l.map(x => ({
             id: x.id, state: x.state, err: x.error, url: (x.url || '').slice(0, 60),
             byExt: x.byExtensionId || '' }))))`, true, sid2);
        const st = await b2.eval(
          `chrome.storage.local.get('dbgTrace').then(o => JSON.stringify(o.dbgTrace || []))`, true, sid2);
        console.log('[diag] 下载项:', dl);
        try {
          for (const line of JSON.parse(JSON.parse(st))) console.log('  [ext] ' + line);
        } catch {
          console.log('[diag] dbgTrace:', st);
        }
      } else {
        console.log('[diag] 没有后台目标');
      }
      b2.close();
    } catch (e) { console.log('[diag] 读取扩展状态失败:', e.message); }

    failures.push('客户端没有收到任务（扩展没接管，或密钥/端口不对）');
  } else {
    console.log('[live] 客户端已收到任务:', task.name, task.state);
    const done = await waitFor(async () => {
      // 同样别查 /api/stats（只有计数、没有任务数组），要查 /api/tasks；
      // 状态值是小写的 completed / failed
      const r = await fetch(`http://127.0.0.1:${CLIENT_PORT}/api/tasks`, { headers: { 'X-Kokona-Secret': SECRET } });
      const list = await r.json();
      const t = (list || []).find((x) => x.gid === task.gid);
      return t && (t.state === 'completed' || t.state === 'failed') ? t : null;
    }, 60000);
    if (!done) failures.push('任务一直没到终态');
    else if (done.state !== 'completed') failures.push('任务失败: ' + (done.errorMessage || done.state));
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
