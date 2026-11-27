/**
 * 真机端到端验证 popup 的站点放行按钮：
 *   大按钮「本网站不接管下载」（橙色警示）——整站（含子域）都不接管，再点恢复。
 *
 * 环境：真实 Edge（独立 profile + 加载 extension-v2 副本）+ 真实海兔客户端 + 本地 HTTP 服务。
 * 做法：CDP 打开 popup 页 → 真实鼠标点击按钮 → 在测试页真实点击下载链接 →
 *       轮询客户端 /api/tasks 与浏览器下载目录，确认"该接管的接管、该放行的放行"。
 *
 * 测试副本会往 manifest 的 permissions 里加 "tabs"：脚本把 popup 当普通标签页打开，
 * 没有用户点图标这个动作，拿不到 activeTab；生产代码不用改。
 *
 * 用法：node tools/ext_bypass_live.mjs [--port=16800] [--keep]
 *       密钥默认从 %APPDATA%\KokonaDownloader\settings.json 读，避免出现在命令行历史里。
 */

import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import fs from 'node:fs';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..');
const SRC_EXT = path.join(ROOT, 'extension-v2');
const arg = (k, d) => { const h = process.argv.find((a) => a.startsWith(`--${k}=`)); return h ? h.slice(k.length + 3) : d; };
const keep = process.argv.includes('--keep');

const CDP_PORT = 9336;
const SRV_PORT = 16831;
const FILE_SIZE = 512 * 1024;
const RUN = Date.now().toString(36);
const OUT_DIR = path.join(os.tmpdir(), 'kokona-bypass-shots');
fs.mkdirSync(OUT_DIR, { recursive: true });

const wait = (ms) => new Promise((r) => setTimeout(r, ms));

/* ---------------- 客户端配置（密钥不落命令行） ---------------- */

const settingsFile = path.join(process.env.APPDATA || '', 'KokonaDownloader', 'settings.json');
if (!fs.existsSync(settingsFile)) { console.error('[live] 找不到客户端设置：' + settingsFile); process.exit(2); }
const clientCfg = JSON.parse(fs.readFileSync(settingsFile, 'utf8'));
// settings.json 是 PascalCase（ApiSecret/ApiPort），这里按小写查，免得大小写踩坑
const cfg = {};
for (const [k, v] of Object.entries(clientCfg)) cfg[k.toLowerCase()] = v;
const SECRET = arg('secret', cfg.apisecret || '');
const CLIENT_PORT = parseInt(arg('port', String(cfg.apiport || 16800)), 10);
if (!SECRET) { console.error('[live] 客户端没有 apiSecret'); process.exit(2); }

async function api(pathname, method = 'GET', body) {
  const headers = { 'X-Kokona-Secret': SECRET };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const r = await fetch(`http://127.0.0.1:${CLIENT_PORT}${pathname}`, {
    method, headers, body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!r.ok) throw new Error(`客户端 ${method} ${pathname} → HTTP ${r.status}`);
  if (r.status === 204) return null;
  const txt = await r.text();
  return txt ? JSON.parse(txt) : null;
}

try {
  await api('/api/ping');
} catch (e) {
  console.error('[live] 客户端没运行（先启动海兔客户端再跑本脚本）：' + e.message);
  process.exit(2);
}

/* ---------------- 本地测试服务 ---------------- */

const payload = Buffer.alloc(FILE_SIZE);
for (let i = 0; i < FILE_SIZE; i++) payload[i] = (i * 17 + 3) & 0xff;
const hits = [];

const server = http.createServer((req, res) => {
  const u = new URL(req.url, 'http://127.0.0.1');
  hits.push(u.pathname + u.search);

  if (u.pathname === '/page') {
    const c = u.searchParams.get('c') || 'x';
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
    return res.end(`<!doctype html><meta charset="utf-8"><title>kokona bypass e2e</title>
<style>body{font:16px sans-serif;padding:40px}#dl{display:inline-block;padding:12px 18px;background:#2f6fd0;color:#fff;text-decoration:none;border-radius:6px}</style>
<a id="dl" href="/f.bin?c=${encodeURIComponent(c)}">下载测试文件</a>`);
  }

  if (u.pathname === '/f.bin') {
    const c = u.searchParams.get('c') || 'x';
    const name = `kokona-${c}.bin`;
    const m = /^bytes=(\d+)-(\d*)$/.exec(req.headers.range || '');
    if (m) {
      const start = Number(m[1]);
      const end = m[2] ? Math.min(Number(m[2]), payload.length - 1) : payload.length - 1;
      res.writeHead(206, {
        'Content-Type': 'application/octet-stream',
        'Content-Disposition': `attachment; filename="${name}"`,
        'Content-Length': String(end - start + 1),
        'Content-Range': `bytes ${start}-${end}/${payload.length}`,
        'Accept-Ranges': 'bytes',
      });
      return res.end(payload.subarray(start, end + 1));
    }
    res.writeHead(200, {
      'Content-Type': 'application/octet-stream',
      'Content-Disposition': `attachment; filename="${name}"`,
      'Content-Length': String(payload.length),
      'Accept-Ranges': 'bytes',
    });
    return res.end(payload);
  }

  res.writeHead(404);
  res.end();
});
await new Promise((r) => server.listen(SRV_PORT, '127.0.0.1', r));

/* ---------------- 工作目录与扩展副本 ---------------- */

const work = fs.mkdtempSync(path.join(os.tmpdir(), 'kokona-bypass-'));
const EXT_DIR = path.join(work, 'ext');
const browserDl = path.join(work, 'browser-dl');
const clientDl = path.join(work, 'client-dl');
fs.mkdirSync(browserDl, { recursive: true });
fs.mkdirSync(clientDl, { recursive: true });
fs.cpSync(SRC_EXT, EXT_DIR, { recursive: true, filter: (s) => !s.includes(`${path.sep}test`) });

const manifestPath = path.join(EXT_DIR, 'manifest.json');
const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
if (!manifest.permissions.includes('tabs')) manifest.permissions.push('tabs');
fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 2));

/* ---------------- CDP ---------------- */

async function cdpList() {
  const r = await fetch(`http://127.0.0.1:${CDP_PORT}/json/list`);
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
  send(method, params = {}) {
    const id = ++this.id;
    return new Promise((res) => { this.pending.set(id, res); this.ws.send(JSON.stringify({ id, method, params })); });
  }
  async eval(expr) {
    const r = await this.send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true });
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
    if (r.error) throw new Error('截图失败: ' + r.error.message);
    fs.writeFileSync(file, Buffer.from(r.result.data, 'base64'));
    return file;
  }
  close() { try { this.ws.close(); } catch { } }
}

async function waitFor(fn, timeoutMs, stepMs = 200) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) { const v = await fn(); if (v) return v; await wait(stepMs); }
  return null;
}

/** 在指定标签页里按选择器真实点击（先取元素中心坐标）。 */
async function clickEl(conn, selector) {
  const raw = await conn.eval(`(() => {
    const el = document.querySelector(${JSON.stringify(selector)});
    if (!el) return null;
    const b = el.getBoundingClientRect();
    return JSON.stringify({ x: b.left + b.width / 2, y: b.top + b.height / 2 });
  })()`);
  if (!raw || typeof raw !== 'string') throw new Error('找不到元素 ' + selector + '：' + raw);
  const p = JSON.parse(raw);
  await conn.clickAt(Math.round(p.x), Math.round(p.y));
}

/** 在浏览器里开一个同窗口的标签页，返回 { targetId, conn }。 */
async function newTab(browser, url) {
  const r = await browser.send('Target.createTarget', { url, newWindow: false });
  const targetId = r.result && r.result.targetId;
  if (!targetId) throw new Error('Target.createTarget 失败: ' + JSON.stringify(r.error || r.result));
  const meta = await waitFor(async () => (await cdpList()).find((t) => t.id === targetId), 8000);
  if (!meta || !meta.webSocketDebuggerUrl) throw new Error('拿不到标签页调试地址');
  const conn = await new Cdp(meta.webSocketDebuggerUrl).open();
  await conn.send('Page.enable');
  return { targetId, conn };
}

async function activate(targetId) {
  await fetch(`http://127.0.0.1:${CDP_PORT}/json/activate/${targetId}`);
}

/** 浏览器原生下载完成的终态文件（.crdownload / 被扩展 cancel 的都不会是这个名字）。 */
function browserFinalFile(caseName) {
  return path.join(browserDl, `kokona-${caseName}.bin`);
}

async function waitBrowserFile(caseName, timeoutMs = 15000) {
  const file = browserFinalFile(caseName);
  return waitFor(() => fs.existsSync(file) && fs.statSync(file).size === FILE_SIZE, timeoutMs, 200);
}

async function clientTask(caseName) {
  const list = await api('/api/tasks');
  return (list || []).find((t) => (t.urls || []).some((u) => u.includes(`c=${caseName}`))) || null;
}

async function waitClientTask(caseName, timeoutMs = 20000) {
  return waitFor(() => clientTask(caseName), timeoutMs, 250);
}

/** 触发一次真实下载：页面导航到 /page?c=，再点真实链接。真机偶发一次点击没落到链接上，允许重试一次。 */
async function triggerDownload(pageConn, pageTargetId, caseName) {
  await activate(pageTargetId);
  await pageConn.send('Page.navigate', { url: `http://127.0.0.1:${SRV_PORT}/page?c=${encodeURIComponent(caseName)}` });
  const ready = await waitFor(async () => (await pageConn.eval('!!document.querySelector("#dl")')) === true, 8000);
  if (!ready) throw new Error('测试页没加载出下载链接');
  for (let attempt = 0; attempt < 2; attempt++) {
    await activate(pageTargetId);
    await clickEl(pageConn, '#dl');
    const hit = await waitFor(() => hits.some((h) => h.includes(`/f.bin?c=${caseName}`)), attempt === 0 ? 6000 : 10000);
    if (hit) return;
    console.log('[live] 点击后没触发下载，重试一次：' + caseName);
    await wait(500);
  }
  throw new Error('服务器没收到浏览器请求：' + caseName);
}

/* ---------------- 启动 Edge ---------------- */

const EDGE = ['C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe'].find((p) => fs.existsSync(p));
if (!EDGE) { console.error('[live] 找不到 Edge'); process.exit(2); }

const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'kokona-bypass-profile-'));
const edge = spawn(EDGE, [
  `--user-data-dir=${profile}`,
  `--load-extension=${EXT_DIR}`,
  `--disable-extensions-except=${EXT_DIR}`,
  `--remote-debugging-port=${CDP_PORT}`,
  '--no-first-run', '--no-default-browser-check',
  '--disable-features=msEdgeFirstRunExperience,msImplicitSignin,DisableLoadExtensionCommandLineSwitch',
  '--window-size=420,780', '--window-position=60,40',
  'about:blank',
], { stdio: 'ignore' });

const failures = [];
const shots = [];
let browser = null;
let initialClient = null;

try {
  const ready = await waitFor(async () => { try { return (await cdpList()).length > 0; } catch { return false; } }, 20000);
  if (!ready) throw new Error('CDP 未就绪，Edge 没起来');

  const manifestNow = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
  const swPath = '/' + (manifestNow.background.service_worker || '').replace(/^\/+/, '');
  let extTargets = [];
  await waitFor(async () => {
    extTargets = (await cdpList()).filter((x) => (x.url || '').includes('chrome-extension://') && (x.url || '').endsWith(swPath));
    return extTargets.length > 0;
  }, 20000);
  if (!extTargets.length) throw new Error('等不到扩展后台');
  const extId = new URL(extTargets[0].url).host;
  console.log('[live] 扩展 id:', extId, '（测试副本，manifest 加了 tabs 权限）');

  const ver = await (await fetch(`http://127.0.0.1:${CDP_PORT}/json/version`)).json();
  browser = await new Cdp(ver.webSocketDebuggerUrl).open();
  await browser.send('Browser.setDownloadBehavior', { behavior: 'allow', downloadPath: browserDl, eventsEnabled: true });

  // 客户端：记下原值，测试期改为"接管开 + 下载到临时目录"
  initialClient = await api('/api/settings');
  await api('/api/settings', 'POST', { interceptBrowserDownloads: true, defaultDownloadDir: clientDl });
  const afterSet = await api('/api/settings');
  console.log('[live] 客户端 intercept:', initialClient.interceptBrowserDownloads, '→', afterSet.interceptBrowserDownloads);
  if (afterSet.interceptBrowserDownloads !== true) throw new Error('客户端拦截开关没打开');

  // 预置扩展设置（等价于用户粘贴密钥）
  const opt = await newTab(browser, `chrome-extension://${extId}/ui/options.html`);
  const optReady = await waitFor(async () => (await opt.conn.eval('typeof chrome !== "undefined" && !!chrome.storage')) === true, 8000);
  if (!optReady) throw new Error('选项页没就绪');
  const preset = { host: '127.0.0.1', port: CLIENT_PORT, secret: SECRET, autoCapture: true, forceAttachment: false };
  await opt.conn.eval(`chrome.storage.local.set({settings:${JSON.stringify(preset)}}).then(() => 'ok')`);
  opt.conn.close();
  await fetch(`http://127.0.0.1:${CDP_PORT}/json/close/${opt.targetId}`).catch(() => { });

  // 页面标签 + popup 标签（同一窗口）
  const pageTab = await newTab(browser, `http://127.0.0.1:${SRV_PORT}/page?c=warmup`);
  await waitFor(async () => (await pageTab.conn.eval('!!document.querySelector("#dl")')) === true, 8000);
  const popupTab = await newTab(browser, `chrome-extension://${extId}/ui/popup.html`);

  await popupTab.conn.send('Emulation.setDeviceMetricsOverride', {
    width: 360, height: 620, deviceScaleFactor: 1, mobile: false,
  });

  // popup 里的 tabs.query 需要"当前活动标签是测试页"；先激活测试页再刷新 popup
  await activate(pageTab.targetId);
  await popupTab.conn.send('Page.reload');
  const siteReady = await waitFor(async () => {
    const t = await popupTab.conn.eval('document.getElementById("siteBypass") && document.getElementById("siteBypass").textContent');
    return typeof t === 'string' && t.includes('127.0.0.1');
  }, 10000);
  const siteText0 = await popupTab.conn.eval('document.getElementById("siteBypass").textContent');
  console.log('[live] popup 站点按钮:', siteText0);
  if (!siteReady) throw new Error('popup 没识别出当前网站（tabs.query 拿不到 URL）：' + siteText0);

  // 界面要求：橙色警示底色、没有小字说明、没有「仅本次」按钮
  await waitFor(async () => (await popupTab.conn.eval('!document.getElementById("captureToggle").classList.contains("off")')) === true, 6000);
  const ui = JSON.parse(await popupTab.conn.eval(`(() => {
    const b = document.getElementById('siteBypass');
    const s = getComputedStyle(b);
    const track = document.querySelector('#captureToggle .track');
    const knob = document.querySelector('#captureToggle .knob');
    const tr = track.getBoundingClientRect();
    const kr = knob.getBoundingClientRect();
    return JSON.stringify({
      text: b.textContent.trim(), bg: s.backgroundColor, color: s.color,
      hints: document.querySelectorAll('.hint, .sub, .state, .status').length,
      once: !!document.getElementById('onceBypass'),
      height: Math.round(b.getBoundingClientRect().height),
      toggleOn: !document.getElementById('captureToggle').classList.contains('off'),
      knobLeft: Math.round(kr.left - tr.left),
      trackWidth: Math.round(tr.width),
    });
  })()`));
  console.log('[live] 大按钮样式:', ui);
  if (!/不接管/.test(ui.text)) failures.push('大按钮文案不对：' + ui.text);
  if (!/rgb\(217, 119, 6\)/.test(ui.bg)) failures.push('大按钮不是橙色：' + ui.bg);
  if (ui.hints !== 0) failures.push('popup 仍有小字说明元素：' + ui.hints);
  if (ui.once) failures.push('「仅本次不接管」按钮仍然存在');
  if (ui.toggleOn && ui.knobLeft < 18) failures.push('开关打开时圆球仍在左侧：' + ui.knobLeft + 'px');

  shots.push(await popupTab.conn.screenshot(path.join(OUT_DIR, '1-popup-default.png')));

  /* ---- 场景 1：本网站不接管 ---- */
  console.log('\n[live] === 场景 1：本网站不接管 ===');
  await clickEl(popupTab.conn, '#siteBypass');
  const becameBypassed = await waitFor(async () => {
    const t = await popupTab.conn.eval('document.getElementById("siteBypass").textContent');
    return t.startsWith('恢复接管本网站');
  }, 5000);
  if (!becameBypassed) failures.push('点大按钮后文案没变成「恢复接管本网站」');
  const saved = await popupTab.conn.eval(`chrome.storage.local.get('siteBypass').then(o => JSON.stringify(o.siteBypass || {}))`);
  console.log('[live] siteBypass =', saved);
  if (!String(saved).includes('127.0.0.1')) failures.push('站点没有写进 storage：' + saved);
  shots.push(await popupTab.conn.screenshot(path.join(OUT_DIR, '2-popup-site-bypassed.png')));

  const caseSite = `site-${RUN}`;
  await triggerDownload(pageTab.conn, pageTab.targetId, caseSite);
  const siteFile = await waitBrowserFile(caseSite);
  if (!siteFile) failures.push('整站放行后浏览器没有完成下载');
  await wait(2500); // 给可能的错误转发留出时间
  if (await clientTask(caseSite)) failures.push('整站放行的下载仍被转给了客户端');
  else console.log('[live] 站点放行：浏览器下载完成，客户端没有收到任务 ✓');

  /* ---- 场景 2：恢复接管 ---- */
  console.log('\n[live] === 场景 2：恢复接管 ===');
  await clickEl(popupTab.conn, '#siteBypass');
  const restored = await waitFor(async () => {
    const t = await popupTab.conn.eval('document.getElementById("siteBypass").textContent');
    return t.startsWith('本网站不接管下载');
  }, 5000);
  if (!restored) failures.push('点恢复后文案没回来');

  const caseCapture = `capture-${RUN}`;
  await triggerDownload(pageTab.conn, pageTab.targetId, caseCapture);
  const taskCapture = await waitClientTask(caseCapture);
  if (!taskCapture) failures.push('恢复接管后客户端没收到任务');
  else console.log('[live] 恢复接管：客户端收到任务', taskCapture.name || taskCapture.gid, '✓');
  await wait(2000);
  if (fs.existsSync(browserFinalFile(caseCapture))) failures.push('已接管但浏览器仍完成了下载（没取消）');
  else console.log('[live] 已接管的浏览器下载被取消 ✓');

  /* ---- 清理客户端任务 ---- */
  if (taskCapture && taskCapture.gid) {
    try { await api(`/api/tasks/${taskCapture.gid}/remove`, 'POST', {}); } catch { }
  }
  pageTab.conn.close();
  popupTab.conn.close();
} catch (e) {
  failures.push(e.message);
} finally {
  if (initialClient) {
    try {
      await api('/api/settings', 'POST', {
        interceptBrowserDownloads: initialClient.interceptBrowserDownloads,
        defaultDownloadDir: initialClient.defaultDownloadDir,
      });
    } catch { }
  }
  if (browser) browser.close();
  if (!keep) {
    edge.kill();
    await wait(600);
    try { fs.rmSync(profile, { recursive: true, force: true }); } catch { }
    try { fs.rmSync(work, { recursive: true, force: true }); } catch { }
    server.close();
  }
}

console.log('\n[shots] 截图：');
for (const s of shots) console.log('  ' + s);

if (failures.length) {
  console.error('\n[live] 失败:\n - ' + failures.join('\n - '));
  process.exit(1);
}
console.log('\n[live] 通过：橙色站点放行按钮在真实浏览器里生效（整站放行 / 恢复接管）');
