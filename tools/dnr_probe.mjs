/**
 * DNR 强制附件探针：验证"modifyHeaders 把 main_frame 响应改成 attachment"在真实浏览器里成立。
 *
 * 为什么不看下载结果：本机 IDM 会抢下载，而且下载落盘受策略影响。
 * 响应头是更靠前的信号——CDP 的 Network.responseReceived 能直接读到最终响应头，
 * 有 content-disposition 就说明规则生效了，与下载是否被劫走无关。
 *
 * 用法：node tools/dnr_probe.mjs
 * 退出码：0 = 机制成立；1 = 规则没生效；2 = 环境不支持（浏览器/扩展加载不起来）
 */

import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import fs from 'node:fs';
import { spawn } from 'node:child_process';

const EDGE = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const CDP_PORT = 9334;
const DBG = (...a) => console.log('[dnr]', ...a);

const BODY = Buffer.from('%PDF-1.4\n% kokona probe\n', 'latin1');

/* ---------------- 1. 本地测试服务 ---------------- */

const hits = [];
const server = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  hits.push(url.pathname + url.search);
  // 关键：**不**发 Content-Disposition。浏览器看到 application/pdf 会内联打开，
  // 这正是 DNR 要补的那个洞。
  const type = url.pathname.endsWith('.pdf') ? 'application/pdf' : 'text/plain';
  res.writeHead(200, {
    'Content-Type': type,
    'Content-Length': BODY.length,
    'Cache-Control': 'no-store',
  });
  res.end(BODY);
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const PORT = server.address().port;

/* ---------------- 2. 探针扩展（静态规则，等价于扩展下发的动态规则） ---------------- */

const extDir = fs.mkdtempSync(path.join(os.tmpdir(), 'dnr-probe-ext-'));
const rule = {
  id: 1,
  priority: 1,
  action: {
    type: 'modifyHeaders',
    responseHeaders: [{ header: 'content-disposition', operation: 'set', value: 'attachment' }],
  },
  condition: {
    regexFilter: '^https?://[^?#]*\\.(pdf)(?:[?#]|$)',
    isUrlFilterCaseSensitive: false,
    resourceTypes: ['main_frame'],
  },
};
fs.writeFileSync(path.join(extDir, 'rules.json'), JSON.stringify([rule], null, 2));
fs.writeFileSync(path.join(extDir, 'manifest.json'), JSON.stringify({
  manifest_version: 3,
  name: 'dnr-probe',
  version: '1.0',
  permissions: ['declarativeNetRequestWithHostAccess'],
  host_permissions: ['http://127.0.0.1/*'],
  declarative_net_request: { rule_resources: [{ id: 'r1', enabled: true, path: 'rules.json' }] },
}, null, 2));

/* ---------------- 3. 启动浏览器 ---------------- */

const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'dnr-probe-profile-'));
const args = [
  `--user-data-dir=${profile}`,
  `--disable-extensions-except=${extDir}`,
  `--load-extension=${extDir}`,
  `--remote-debugging-port=${CDP_PORT}`,
  '--no-first-run',
  '--no-default-browser-check',
  '--disable-features=msEdgeFirstRunExperience,msImplicitSignin',
  '--window-size=900,600',
  'about:blank',
];
if (process.env.DNR_HEADLESS === '1') args.unshift('--headless=new');

const proc = spawn(EDGE, args, { stdio: 'ignore' });
proc.on('exit', (c) => { if (c !== 0) DBG('浏览器退出码', c); });

async function waitFor(fn, ms = 15000) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    try { const v = await fn(); if (v) return v; } catch { /* 继续等 */ }
    await new Promise((r) => setTimeout(r, 200));
  }
  return null;
}

const version = await waitFor(async () => {
  const r = await fetch(`http://127.0.0.1:${CDP_PORT}/json/version`);
  return r.ok ? r.json() : null;
}, 20000);

if (!version) {
  DBG('无法连接 CDP —— 浏览器没起来');
  proc.kill(); server.close();
  process.exit(2);
}
DBG('浏览器', version['Browser']);

/* ---------------- 4. 用 CDP 打开页面并读响应头 ---------------- */

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
  pending.set(id, res);
  const msg = { id, method, params };
  if (sessionId) msg.sessionId = sessionId;
  ws.send(JSON.stringify(msg));
});

// 浏览器级下载事件：不依赖扩展权限，能直接证明"这次导航变成了下载"
const dlDir = fs.mkdtempSync(path.join(os.tmpdir(), 'dnr-probe-dl-'));
await send('Browser.setDownloadBehavior', {
  behavior: 'allow', downloadPath: dlDir, eventsEnabled: true,
});
const startedDownloads = () => events
  .filter((e) => e.method === 'Browser.downloadWillBegin')
  .map((e) => ({ url: e.params.url, name: e.params.suggestedFilename }));

/**
 * 每个地址单开一个标签页。
 * 原因：导航一旦被转成下载，后续再对同一标签页导航会打断这次下载，
 * downloadWillBegin 就永远不来（第一次跑就是这么误判的）。
 */
async function probeUrl(url, timeoutMs = 12000) {
  const t = await send('Target.createTarget', { url: 'about:blank' });
  const tid = t.result && t.result.targetId;
  const a = await send('Target.attachToTarget', { targetId: tid, flatten: true });
  const s = a.result && a.result.sessionId;
  if (!s) return { error: 'attach 失败' };

  await send('Network.enable', {}, s);
  await send('Page.enable', {}, s);
  const before = startedDownloads().length;

  await send('Page.navigate', { url }, s);

  const docEv = await waitFor(() => events.find((e) => e.method === 'Network.responseReceived'
    && e.sessionId === s
    && e.params.type === 'Document'
    && e.params.response.url.startsWith(url)), timeoutMs);

  if (!docEv) {
    await send('Target.closeTarget', { targetId: tid });
    return { error: '没有 Document 响应' };
  }

  const headers = docEv.params.response.headers || {};
  const lower = {};
  for (const [k, v] of Object.entries(headers)) lower[k.toLowerCase()] = v;

  // 等下载事件落定：响应先到，downloadWillBegin 稍后才来
  await waitFor(() => startedDownloads().length > before, 3000);
  const downloads = startedDownloads().slice(before);

  await send('Target.closeTarget', { targetId: tid });
  return {
    status: docEv.params.response.status,
    contentType: lower['content-type'] ?? null,
    disposition: lower['content-disposition'] ?? null,
    downloads,
  };
}

const pdfUrl = `http://127.0.0.1:${PORT}/sample.pdf`;
const pdfQueryUrl = `http://127.0.0.1:${PORT}/sample.pdf?token=abc`;
const txtUrl = `http://127.0.0.1:${PORT}/sample.txt`;

const txt = await probeUrl(txtUrl);
DBG('.txt        →', JSON.stringify(txt));
const pdf = await probeUrl(pdfUrl);
DBG('.pdf        →', JSON.stringify(pdf));
const pdfQ = await probeUrl(pdfQueryUrl);
DBG('.pdf?token  →', JSON.stringify(pdfQ));

/* ---------------- 5. 判定 ---------------- */

const okPdf = pdf.disposition && /attachment/i.test(pdf.disposition);
const okQuery = pdfQ.disposition && /attachment/i.test(pdfQ.disposition);
const okTxt = !txt.disposition;   // 对照：清单外的类型不能被改
const dlPdf = (pdf.downloads || []).length === 1 && pdf.downloads[0].url.includes('sample.pdf');
const dlQuery = (pdfQ.downloads || []).length === 1;
const dlTxt = (txt.downloads || []).length > 0;

console.log('');
console.log('  规则命中 .pdf        :', okPdf ? 'PASS' : 'FAIL');
console.log('  规则命中 .pdf?token  :', okQuery ? 'PASS' : 'FAIL');
console.log('  对照 .txt 不受影响   :', okTxt ? 'PASS' : 'FAIL');
console.log('  .pdf 真的产生下载项  :', dlPdf ? 'PASS' : 'FAIL');
console.log('  .pdf?token 产生下载  :', dlQuery ? 'PASS' : 'FAIL');
console.log('  对照 .txt 不产生下载 :', dlTxt ? 'FAIL' : 'PASS');
console.log('  服务端收到的请求      :', hits.join(', ') || '(无)');

ws.close();
proc.kill();
server.close();
setTimeout(() => {
  try { fs.rmSync(extDir, { recursive: true, force: true }); } catch { }
  try { fs.rmSync(profile, { recursive: true, force: true }); } catch { }
}, 0);

process.exit(okPdf && okQuery && okTxt && dlPdf && dlQuery && !dlTxt ? 0 : 1);
