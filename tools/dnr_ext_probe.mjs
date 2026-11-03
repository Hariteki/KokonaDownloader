/**
 * EXT-403 真扩展验收：加载真实的 extension-v2，验证
 *  1) 打开开关 + 授权站点 + 接管开着 → 动态规则真的注册进浏览器
 *  2) 导航到 .pdf 会产生下载项（这就是 downloads.onCreated 的入口）
 *  3) 关掉开关 → 规则被清掉，.pdf 不再变成下载
 *  4) 清单外的 .txt 始终不受影响
 *
 * 客户端指向一个死端口（19999），这样扩展处于"离线"状态：
 * 下载不会被真的转发到用户的客户端，失败回退也不会取消浏览器下载，
 * 我们才能干净地观察 DNR 的效果。
 *
 * 用法：node tools/dnr_ext_probe.mjs      退出码 0=PASS / 1=FAIL / 2=环境不支持
 */

import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import fs from 'node:fs';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const extArg = process.argv.find((a) => a.startsWith('--ext='));
// 允许指向解包后的产物，用来验证"打包出来的东西真的能加载"
const EXT_DIR = extArg ? path.resolve(extArg.slice(6)) : path.resolve(__dirname, '..', 'extension-v2');
const EDGE = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const CDP_PORT = 9335;
const DEAD_PORT = 19999;
const DBG = (...a) => console.log('[ext-dnr]', ...a);

const BODY = Buffer.from('%PDF-1.4\n% kokona ext probe\n', 'latin1');

/* ---------------- 1. 测试服务 ---------------- */

const hits = [];
const server = http.createServer((req, res) => {
  const u = new URL(req.url, 'http://127.0.0.1');
  hits.push(u.pathname + u.search);
  const isPdf = u.pathname.endsWith('.pdf');
  res.writeHead(200, {
    'Content-Type': isPdf ? 'application/pdf' : 'text/plain',
    'Content-Length': BODY.length,
    'Cache-Control': 'no-store',
  });
  res.end(BODY);
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const PORT = server.address().port;

/* ---------------- 2. 启动带真扩展的浏览器 ---------------- */

/**
 * 真扩展的可选站点权限要靠用户点弹窗授予，无头模式下那条路会卡在弹窗上。
 * 这里复制一份，只把 optional_host_permissions 提升为 host_permissions —— 纯粹为了免掉交互，
 * 代码路径一行没改（否则测的就不是真实实现了）。
 */
const extCopy = fs.mkdtempSync(path.join(os.tmpdir(), 'ext-dnr-copy-'));
fs.cpSync(EXT_DIR, extCopy, {
  recursive: true,
  filter: (src) => !src.includes(`${path.sep}node_modules`) && !src.includes(`${path.sep}test`),
});
{
  const mf = JSON.parse(fs.readFileSync(path.join(extCopy, 'manifest.json'), 'utf8'));
  const optional = mf.optional_host_permissions || [];
  mf.host_permissions = Array.from(new Set([...(mf.host_permissions || []), ...optional]));
  delete mf.optional_host_permissions;
  fs.writeFileSync(path.join(extCopy, 'manifest.json'), JSON.stringify(mf, null, 2));
}

const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'ext-dnr-profile-'));
const args = [
  `--user-data-dir=${profile}`,
  `--disable-extensions-except=${extCopy}`,
  `--load-extension=${extCopy}`,
  `--remote-debugging-port=${CDP_PORT}`,
  '--no-first-run',
  '--no-default-browser-check',
  '--disable-features=msEdgeFirstRunExperience,msImplicitSignin',
];
if (process.env.DNR_HEADLESS !== '0') args.unshift('--headless=new');
args.push('about:blank');

const proc = spawn(EDGE, args, { stdio: 'ignore' });

const wait = (ms) => new Promise((r) => setTimeout(r, ms));
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
  proc.kill(); server.close();
  process.exit(2);
}
DBG('浏览器', version['Browser']);

/* ---------------- 3. CDP 基础 ---------------- */

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
  // 有些调用在无头模式下会永远等不到回包（例如需要 UI 的权限弹窗），加超时兜底
  const timer = setTimeout(() => {
    if (pending.has(id)) { pending.delete(id); res({ error: { message: 'timeout: ' + method } }); }
  }, 12000);
  pending.set(id, (v) => { clearTimeout(timer); res(v); });
  ws.send(JSON.stringify(msg));
});

async function evaluate(expr, sessionId, { userGesture = false } = {}) {
  const r = await send('Runtime.evaluate', {
    expression: expr, awaitPromise: true, returnByValue: true, userGesture,
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

/* ---------------- 4. 找到扩展 id ---------------- */

// 不打开发现的话 getTargets 看不到 service_worker
await send('Target.setDiscoverTargets', { discover: true });
await wait(500);
const targets = await send('Target.getTargets');
let swTarget = (targets.result.targetInfos || []).find(
  (t) => t.type === 'service_worker' && /^chrome-extension:\/\/[a-z]+\/sw\/background\.js/.test(t.url || ''));

let extId = swTarget ? swTarget.url.split('/')[2] : null;

// 兜底：有些版本不把扩展 SW 暴露在 target 列表里，直接从 profile 的 Preferences 里读
if (!extId) {
  extId = await waitFor(() => {
    try {
      const pref = JSON.parse(fs.readFileSync(path.join(profile, 'Default', 'Preferences'), 'utf8'));
      const settings = (pref.extensions && pref.extensions.settings) || {};
      const hit = Object.entries(settings).find(([, v]) => String(v.path || '').toLowerCase().includes('ext-dnr-copy'));
      return hit ? hit[0] : null;
    } catch { return null; }
  }, 8000);
}

if (!extId) {
  DBG('拿不到扩展 id，targets =', JSON.stringify((targets.result.targetInfos || []).map((t) => [t.type, t.url])));
  proc.kill(); server.close();
  process.exit(2);
}
DBG('扩展 id', extId);

/* ---------------- 5. 打开开关 ---------------- */

const opt = await openTab(`chrome-extension://${extId}/ui/options.html`);
if (!opt.sid) {
  DBG('打不开选项页');
  proc.kill(); server.close();
  process.exit(2);
}
await wait(800);

const hasHost = await evaluate(`chrome.permissions.contains({origins:['http://*/*']})`, opt.sid);
DBG('站点权限 →', JSON.stringify(hasHost));
if (hasHost.value !== true) {
  DBG('测试副本没拿到站点权限，DNR 无法生效');
  proc.kill(); server.close();
  process.exit(2);
}

const saved = await evaluate(
  `chrome.runtime.sendMessage({type:'save-settings', patch:{
      host:'127.0.0.1', port:${DEAD_PORT}, secret:'', autoCapture:true, forceAttachment:true}})
     .then((r) => JSON.stringify(r && r.ok)).catch((e) => 'ERR ' + e.message)`,
  opt.sid);
DBG('保存设置 →', JSON.stringify(saved));
await wait(1200);

const selfReport = await evaluate(
  `Promise.all([
     chrome.runtime.sendMessage({type:'get-status'}).then((r) => JSON.stringify(r && r.forceAttachment)),
     chrome.declarativeNetRequest ? 'DNR-OK' : 'DNR-UNDEFINED',
   ]).then((a) => a.join(' | ')).catch((e) => 'ERR ' + e.message)`,
  opt.sid);
DBG('扩展自报 →', selfReport.value);

const rulesOn = await evaluate(
  `chrome.declarativeNetRequest.getDynamicRules().then((r) => JSON.stringify(r))`, opt.sid);
let parsedOn = [];
try { parsedOn = JSON.parse(rulesOn.value || '[]'); } catch { /* ignore */ }
DBG('动态规则条数 →', parsedOn.length);
DBG('pdf 规则 →', JSON.stringify(parsedOn.find((r) => (r.condition && r.condition.regexFilter || '').includes('\\.pdf')) || null));

const pdfRule = parsedOn.find((r) => r.condition && String(r.condition.regexFilter || '').includes('\\.pdf'));
const ruleOk = parsedOn.length > 0
  && !!pdfRule
  && pdfRule.action
  && pdfRule.action.type === 'modifyHeaders'
  && (pdfRule.action.responseHeaders || []).some(
    (h) => h.header.toLowerCase() === 'content-disposition' && /attachment/i.test(h.value || ''));

// 设置页要如实显示"已生效"，否则用户开了开关却不知道有没有起作用
await send('Page.reload', {}, opt.sid);
await wait(1500);
const uiHint = await evaluate(
  `(document.getElementById('forceHint') || {}).textContent || ''`, opt.sid);
DBG('设置页提示 →', JSON.stringify(uiHint.value));
const uiOk = /已生效/.test(uiHint.value || '');

/* ---------------- 6. 观察下载 ---------------- */

const dlDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ext-dnr-dl-'));
await send('Browser.setDownloadBehavior', { behavior: 'allow', downloadPath: dlDir, eventsEnabled: true });
const dlCount = () => events.filter((e) => e.method === 'Browser.downloadWillBegin').length;

async function visit(url) {
  const tab = await openTab('about:blank');
  if (!tab.sid) return { downloads: 0 };
  await send('Page.enable', {}, tab.sid);
  const before = dlCount();
  await send('Page.navigate', { url }, tab.sid);
  await waitFor(() => dlCount() > before, 4000);
  await wait(300);
  const dl = events.filter((e) => e.method === 'Browser.downloadWillBegin')
    .slice(before).map((e) => e.params.url);
  await send('Target.closeTarget', { targetId: tab.tid });
  return { downloads: dl.length, urls: dl };
}

const pdfOn = await visit(`http://127.0.0.1:${PORT}/a.pdf`);
DBG('开关开：.pdf  →', JSON.stringify(pdfOn));
const txtOn = await visit(`http://127.0.0.1:${PORT}/a.txt`);
DBG('开关开：.txt  →', JSON.stringify(txtOn));

// 关掉开关
await evaluate(
  `chrome.runtime.sendMessage({type:'save-settings', patch:{forceAttachment:false}})`, opt.sid);
await wait(800);
const rulesOff = await evaluate(
  `chrome.declarativeNetRequest.getDynamicRules().then((r) => JSON.stringify(r))`, opt.sid);
DBG('关闭后规则 →', rulesOff.value);

const pdfOff = await visit(`http://127.0.0.1:${PORT}/b.pdf`);
DBG('开关关：.pdf  →', JSON.stringify(pdfOff));

/* ---------------- 7. 判定 ---------------- */

const okRule = ruleOk;
const okDownload = pdfOn.downloads >= 1;
const okTxt = txtOn.downloads === 0;
const okCleared = (rulesOff.value || '[]') === '[]';
const okNoForce = pdfOff.downloads === 0;

console.log('');
console.log('  开 → 规则注册进浏览器   :', okRule ? 'PASS' : 'FAIL');
console.log('  设置页显示「已生效」    :', uiOk ? 'PASS' : 'FAIL');
console.log('  开 → .pdf 产生下载项    :', okDownload ? 'PASS' : 'FAIL');
console.log('  开 → .txt 不产生下载    :', okTxt ? 'PASS' : 'FAIL');
console.log('  关 → 规则被清空         :', okCleared ? 'PASS' : 'FAIL');
console.log('  关 → .pdf 不再强制下载  :', okNoForce ? 'PASS' : 'FAIL');
console.log('  服务端收到的请求         :', hits.join(', ') || '(无)');

ws.close();
proc.kill();
server.close();
setTimeout(() => {
  try { fs.rmSync(profile, { recursive: true, force: true }); } catch { }
  try { fs.rmSync(extCopy, { recursive: true, force: true }); } catch { }
}, 0);

process.exit(okRule && uiOk && okDownload && okTxt && okCleared && okNoForce ? 0 : 1);
