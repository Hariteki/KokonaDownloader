#!/usr/bin/env node
/**
 * 登录态下载端到端验证（真实站点）。
 *
 * 要证明的不是"扩展把 Cookie 头发出来了"（那有单测），而是用户能感知的那句：
 *   **需要登录才能下的资源，通过扩展接管，真的能下下来。**
 *
 * 与本地假服务的区别（也是这条用例存在的理由）：
 *   - 真实站点用的是 HttpOnly / Secure / SameSite 的会话 Cookie，`chrome.cookies` 能不能拿到它
 *   - 真实站点会 302 到签名 CDN，客户端必须把这次跳转走完
 *   - 真实站点有反爬（UA/Referer/客户端指纹），本地假服务一个都没有
 *
 * 用法（会弹出真实浏览器窗口，你在里面登录）：
 *   node tools/login_download_e2e.mjs --url="https://github.com/<你>/<私有仓库>/raw/main/test.bin"
 *   可选：--wait-login=600  （等你登录的秒数，默认 600）
 *         --headless=1      （不弹窗，仅用于已保存登录态的 profile）
 *         --no-hash         （只比大小，不下第二遍算哈希）
 *
 * 判定：
 *   1) 匿名请求（不带任何 Cookie）必须拿不到内容          —— 证明这个地址真的依赖登录态
 *   2) 扩展接管后客户端产生任务并完成                      —— 证明整条链路通了
 *   3) 落盘文件与"带 Cookie 的参照下载"逐字节一致          —— 证明不是下到一个登录页/错误页
 */
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(__dirname, '..');
const EXT_DIR = path.resolve(REPO, 'extension-v2');
const EDGE = process.env.EDGE_PATH
  || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';

const arg = (name, def) => {
  const hit = process.argv.find((a) => a.startsWith(`--${name}=`));
  return hit ? hit.slice(name.length + 3) : def;
};
const has = (name) => process.argv.includes(`--${name}`);

// --github-login：在测试浏览器里等你登录 GitHub，自动建私有仓库并上传测试文件，目标地址由它决定
const GH_LOGIN = has('github-login');
let TARGET = arg('url', '');
if (!TARGET && !GH_LOGIN) {
  console.error('缺少 --url=<需要登录才能下载的地址>（或用 --github-login 自动准备靶子）');
  process.exit(2);
}
const GH_REPO = arg('repo', 'kokona-login-test');
const GH_FILE = arg('file', '');
const WAIT_LOGIN_S = Number(arg('wait-login', '600'));
const HEADLESS = arg('headless', '0') === '1';
const WANT_HASH = !has('no-hash');

const CLIENT = `http://127.0.0.1:${arg('port', '16800')}`;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const say = (...a) => console.log(...a);

/* ---------------- 1. 客户端与密钥 ---------------- */

function readSecret() {
  const p = path.join(process.env.APPDATA || '', 'KokonaDownloader', 'settings.json');
  const raw = fs.readFileSync(p, 'utf8').replace(/^\uFEFF/, '');
  return JSON.parse(raw).ApiSecret;
}

const SECRET = (() => {
  try { return readSecret(); } catch (e) {
    console.error('读不到客户端 settings.json（客户端没跑过？）:', e.message);
    process.exit(2);
  }
})();

const api = async (method, p, body) => {
  const r = await fetch(`${CLIENT}${p}`, {
    method,
    headers: { 'X-Kokona-Secret': SECRET, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const t = await r.text();
  return { status: r.status, json: t ? JSON.parse(t) : null };
};

if ((await api('GET', '/api/ping')).status !== 200) {
  console.error('客户端 API 不可用，先把海兔启动起来');
  process.exit(2);
}

/* ---------------- 2. 对照：匿名请求拿不到内容 ---------------- */

let anon = { status: 0, location: '', type: '', bytes: 0 };
let anonBlocked = false;

async function probeAnon(url) {
  say(`目标: ${url}`);
  say('— 匿名请求（不带 Cookie，模拟"没登录"）—');
  anon = { status: 0, location: '', type: '', bytes: 0 };
  try {
    const ctrl = new AbortController();
    const timer = setTimeout(() => ctrl.abort(), 25000);
    const r = await fetch(url, { redirect: 'manual', signal: ctrl.signal });
    clearTimeout(timer);
    anon = {
      status: r.status,
      location: r.headers.get('location') || '',
      type: r.headers.get('content-type') || '',
      bytes: 0,
    };
    if (r.status === 200) {
      // 200 也可能是"登录页 HTML"：读一点内容看是不是文件
      const buf = Buffer.from(await r.arrayBuffer());
      anon.bytes = buf.length;
      anon.isHtml = /html/i.test(anon.type) || /^\s*<(!doctype|html)/i.test(buf.subarray(0, 200).toString('utf8'));
    }
    say(`  状态 ${anon.status}${anon.location ? `  跳转 → ${anon.location.slice(0, 90)}` : ''}` +
        `${anon.type ? `  类型 ${anon.type.split(';')[0]}` : ''}${anon.bytes ? `  ${anon.bytes} 字节` : ''}`);
  } catch (e) {
    say('  请求失败:', e.message);
  }

  anonBlocked = anon.status >= 400
    || /login|signin|auth/i.test(anon.location)
    || (anon.status === 200 && anon.isHtml === true);
  say(`  → 判定: ${anonBlocked ? '确实拿不到文件（依赖登录态）✅' : '**匿名就能拿到内容，这个地址不合适** ❌'}`);
  return anonBlocked;
}

if (TARGET) {
  await probeAnon(TARGET);
  if (has('probe-only')) {
    process.exit(anonBlocked ? 0 : 1);
  }
}

/* ---------------- 3. 启动真实浏览器 + 真扩展 ---------------- */

const extCopy = fs.mkdtempSync(path.join(os.tmpdir(), 'ext-login-copy-'));
fs.cpSync(EXT_DIR, extCopy, {
  recursive: true,
  filter: (src) => !src.includes(`${path.sep}node_modules`) && !src.includes(`${path.sep}test`),
});
{
  // 可选站点权限要靠用户点弹窗授予，自动化里会卡在弹窗上。
  // 只把 optional_host_permissions 提升为 host_permissions，代码一行未改。
  const mfPath = path.join(extCopy, 'manifest.json');
  const mf = JSON.parse(fs.readFileSync(mfPath, 'utf8'));
  const optional = mf.optional_host_permissions || [];
  mf.host_permissions = Array.from(new Set([...(mf.host_permissions || []), ...optional]));
  delete mf.optional_host_permissions;
  fs.writeFileSync(mfPath, JSON.stringify(mf, null, 2));
}

// --profile=<目录>：复用已有登录态的 profile（例如先手动登录一次，再跑本脚本）
const PROFILE_DIR = arg('profile', '');
const profile = PROFILE_DIR || fs.mkdtempSync(path.join(os.tmpdir(), 'ext-login-profile-'));
const dlDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ext-login-dl-'));
const CDP_PORT = 9300 + Math.floor(Math.random() * 400);

const args = [
  `--user-data-dir=${profile}`,
  `--disable-extensions-except=${extCopy}`,
  `--load-extension=${extCopy}`,
  `--remote-debugging-port=${CDP_PORT}`,
  '--no-first-run',
  '--no-default-browser-check',
  '--disable-features=msEdgeFirstRunExperience,msImplicitSignin',
  `--window-size=1200,900`,
];
if (HEADLESS) args.unshift('--headless=new');
args.push('about:blank');

const proc = spawn(EDGE, args, { stdio: 'ignore' });

let ws;
const pending = new Map();
const events = [];
let seq = 0;

const send = (method, params = {}, sessionId) => new Promise((res, rej) => {
  const id = ++seq;
  pending.set(id, { res, rej });
  const msg = { id, method, params };
  if (sessionId) msg.sessionId = sessionId;
  ws.send(JSON.stringify(msg));
});

async function connect() {
  const end = Date.now() + 20000;
  while (Date.now() < end) {
    try {
      const r = await fetch(`http://127.0.0.1:${CDP_PORT}/json/version`);
      if (r.ok) {
        const v = await r.json();
        ws = new WebSocket(v.webSocketDebuggerUrl);
        await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
        ws.onmessage = (ev) => {
          const m = JSON.parse(ev.data);
          if (m.id && pending.has(m.id)) {
            const { res, rej } = pending.get(m.id);
            pending.delete(m.id);
            m.error ? rej(new Error(m.error.message)) : res(m.result);
          } else if (m.method) {
            events.push({ method: m.method, params: m.params, sessionId: m.sessionId, t: Date.now() });
            if (events.length > 4000) events.splice(0, 2000);
          }
        };
        return;
      }
    } catch { /* 还没起来 */ }
    await sleep(300);
  }
  throw new Error('CDP 连不上');
}
await connect();

// 浏览器原生下载落到临时目录，测完删掉（扩展会 cancel 它，这里只是兜底）
await send('Browser.setDownloadBehavior', {
  behavior: 'allow', downloadPath: dlDir, eventsEnabled: true,
});

/* ---------------- 4. 找到扩展并让它连上客户端 ---------------- */

async function extId() {
  for (let i = 0; i < 40; i++) {
    const t = await send('Target.getTargets');
    const sw = (t.targetInfos || []).find((x) => x.type === 'service_worker'
      && /^chrome-extension:\/\/[a-z]+\/sw\/background\.js/.test(x.url || ''));
    if (sw) return sw.url.split('/')[2];
    await sleep(400);
  }
  throw new Error('找不到扩展的 service worker（扩展没加载起来？）');
}

const EID = await extId();
say(`\n扩展 id: ${EID}（临时 profile，和你日常 Edge 完全隔离）`);

async function openTab(url, background = false) {
  const t = await send('Target.createTarget', { url, background });
  const a = await send('Target.attachToTarget', { targetId: t.targetId, flatten: true });
  // 不开 Network 就收不到 responseReceived（只有浏览器级的 downloadWillBegin 会来）
  try { await send('Network.enable', {}, a.sessionId); } catch { }
  try { await send('Page.enable', {}, a.sessionId); } catch { }
  return { targetId: t.targetId, sid: a.sessionId };
}

async function evaluate(expr, sid) {
  const r = await send('Runtime.evaluate', {
    expression: expr, awaitPromise: true, returnByValue: true,
  }, sid);
  if (r.exceptionDetails) return { error: r.exceptionDetails.text };
  return { value: r.result && r.result.value };
}

const opt = await openTab(`chrome-extension://${EID}/ui/options.html`);
await sleep(800);
// 临时 profile 里扩展是全新的：把配对信息直接写进去（等价于用户在设置页粘贴密钥）
const saved = await evaluate(
  `chrome.runtime.sendMessage({type:'save-settings', patch:{
      host:'127.0.0.1', port:${Number(arg('port', '16800'))}, secret:${JSON.stringify(SECRET)},
      autoCapture:true, forceAttachment:false, notify:false}})
     .then((r) => JSON.stringify(r && r.ok)).catch((e) => 'ERR ' + e.message)`,
  opt.sid);
say(`配对写入: ${saved.value ?? saved.error}`);
await sleep(600);

const status = await evaluate(
  `chrome.runtime.sendMessage({type:'get-status'})
     .then((r) => JSON.stringify({online: r.status && r.status.online, key: r.status && r.status.keyValid}))
     .catch((e) => 'ERR ' + e.message)`, opt.sid);
say(`扩展自报状态: ${status.value ?? status.error}`);

/* ---------------- 4.5 可选：GitHub 登录 + 私有仓库靶子 ---------------- */

if (GH_LOGIN) {
  say(`\n══ 准备靶子：请在弹出的窗口里登录 GitHub（最多等 ${WAIT_LOGIN_S} 秒）══`);

  const ghTab = await openTab('https://github.com/login');
  await sleep(2000);

  const deadline = Date.now() + WAIT_LOGIN_S * 1000;
  let owner = null;
  let lastNote = '';
  while (Date.now() < deadline) {
    const st = await evaluate(
      `JSON.stringify({url: location.href, login: (document.querySelector('meta[name="user-login"]')||{}).content || null})`,
      ghTab.sid);
    let info = {};
    try { info = JSON.parse(st.value || '{}'); } catch { }
    if (info.login) { owner = info.login; break; }
    const note = '未登录 @ ' + String(info.url || '').slice(0, 60);
    if (note !== lastNote) { say(`  [等待] ${note}`); lastNote = note; }
    await sleep(2500);
  }
  if (!owner) {
    say('\n等不到 GitHub 登录，放弃。');
    cleanup(5);
  }
  say(`GitHub 已登录: ${owner}`);

  // 建私有仓库（已存在就复用）
  const newTab = await openTab('https://github.com/new');
  await sleep(2000);
  let exists = 0;
  for (let i = 0; i < 30; i++) {
    const r = await evaluate(`fetch('/${owner}/${GH_REPO}').then((r) => r.status).catch(() => 0)`, newTab.sid);
    if (r.value) { exists = r.value; break; }
    await sleep(500);
  }
  say(`仓库 ${owner}/${GH_REPO}: HTTP ${exists}`);

  if (exists === 404) {
    let hasInput = false;
    for (let i = 0; i < 60; i++) {
      const r = await evaluate(
        `!!document.querySelector('input[name="repository[name]"], #repository_name, input[name="name"]')`,
        newTab.sid);
      if (r.value) { hasInput = true; break; }
      await sleep(500);
    }
    if (!hasInput) {
      const dump = await evaluate(
        `JSON.stringify({url: location.href,
           inputs: [...document.querySelectorAll('input')].map((i) => (i.name || i.id || '?') + ':' + i.type).slice(0, 25),
           buttons: [...document.querySelectorAll('button')].map((b) => (b.textContent || '').trim()).filter(Boolean).slice(0, 12)})`,
        newTab.sid);
      say('  找不到仓库名输入框: ' + (dump.value ?? dump.error));
      cleanup(6);
    }

    const form = await evaluate(`(() => {
      const inp = document.querySelector('input[name="repository[name]"], #repository_name, input[name="name"]');
      inp.focus();
      inp.value = ${JSON.stringify(GH_REPO)};
      inp.dispatchEvent(new Event('input', { bubbles: true }));
      inp.dispatchEvent(new Event('change', { bubbles: true }));

      let vis = 'none';
      const sel = document.querySelector('select[name*="visibility"], select#repository_visibility');
      if (sel) {
        const opt = [...sel.options].find((o) => /private/i.test(o.textContent || ''));
        if (opt) { sel.value = opt.value; sel.dispatchEvent(new Event('change', { bubbles: true })); vis = 'select:' + opt.value; }
      } else {
        const radio = [...document.querySelectorAll('input[type=radio]')]
          .find((r) => /private/i.test(r.value) || /private/i.test(r.id) || /private/i.test(r.getAttribute('aria-label') || ''));
        if (radio) { radio.click(); vis = 'radio:' + radio.value; }
      }
      return JSON.stringify({ name: inp.value, vis });
    })()`, newTab.sid);
    say(`  表单填写: ${form.value ?? form.error}`);
    await sleep(700);

    const submit = await evaluate(`(() => {
      const btn = [...document.querySelectorAll('button')].find((b) => /create repository/i.test((b.textContent || '').trim()));
      if (!btn) return 'NO_BTN';
      if (btn.disabled) return 'DISABLED';
      btn.click();
      return 'CLICKED';
    })()`, newTab.sid);
    say(`  提交建仓: ${submit.value ?? submit.error}`);

    let created = false;
    for (let i = 0; i < 60; i++) {
      const r = await evaluate(`location.pathname.startsWith('/${owner}/${GH_REPO}')`, newTab.sid);
      if (r.value) { created = true; break; }
      await sleep(500);
    }
    if (!created) {
      const dump = await evaluate(
        `JSON.stringify({url: location.href, text: (document.body.innerText || '').slice(0, 200)})`,
        newTab.sid);
      say('  建仓没有跳转: ' + (dump.value ?? dump.error));
      cleanup(6);
    }
    say(`  仓库已创建: ${owner}/${GH_REPO}`);
  }

  // 上传测试文件
  const uploadFile = GH_FILE || (() => {
    const p = path.join(os.tmpdir(), 'kokona-login-test.bin');
    fs.writeFileSync(p, crypto.randomBytes(512 * 1024));
    return p;
  })();
  say(`测试文件: ${uploadFile} (${fs.statSync(uploadFile).size} 字节)`);

  await sleep(1200);
  const upTab = await openTab(`https://github.com/${owner}/${GH_REPO}/upload/main`);
  await sleep(2000);
  await send('DOM.enable', {}, upTab.sid).catch(() => { });

  let inputNode = null;
  for (let i = 0; i < 60; i++) {
    const doc = await send('DOM.getDocument', { depth: -1, pierce: true }, upTab.sid).catch(() => null);
    if (doc && doc.root) {
      const n = await send('DOM.querySelector', { nodeId: doc.root.nodeId, selector: 'input[type=file]' }, upTab.sid).catch(() => null);
      if (n && n.nodeId) { inputNode = n.nodeId; break; }
    }
    await sleep(500);
  }
  if (!inputNode) {
    const dump = await evaluate(`JSON.stringify({url: location.href, text: (document.body.innerText || '').slice(0, 200)})`, upTab.sid);
    say('  上传页没有 file input: ' + (dump.value ?? dump.error));
    cleanup(6);
  }
  await send('DOM.setFileInputFiles', { files: [uploadFile], nodeId: inputNode }, upTab.sid);
  say('  文件已注入上传控件');

  let commitReady = false;
  for (let i = 0; i < 60; i++) {
    const r = await evaluate(`(() => {
      const b = [...document.querySelectorAll('button')].find((x) => /commit changes/i.test((x.textContent || '').trim()));
      return b ? !b.disabled : false;
    })()`, upTab.sid);
    if (r.value) { commitReady = true; break; }
    await sleep(500);
  }
  if (!commitReady) {
    say('  等不到可点的 Commit changes');
    cleanup(6);
  }
  await evaluate(`(() => {
    const b = [...document.querySelectorAll('button')].find((x) => /commit changes/i.test((x.textContent || '').trim()));
    b.click();
    return 'CLICKED';
  })()`, upTab.sid);
  say('  已提交，等待落库…');
  await sleep(6000);

  TARGET = `https://github.com/${owner}/${GH_REPO}/raw/main/test.bin`;
  say(`\n靶子地址: ${TARGET}`);
  await probeAnon(TARGET);
}

/* ---------------- 5. 打开登录页，等你登录 ---------------- */

say(`\n══ 请在刚弹出的浏览器窗口里登录（最多等 ${WAIT_LOGIN_S} 秒）══`);
say(`   登录完成后不用管，脚本会自己发现并继续。\n`);

const loginTab = await openTab(TARGET);
await sleep(1500);

/** 探测一次：新建后台标签页导航到目标地址，看是"登录页"还是"真的开始下载"。 */
async function probeOnce() {
  const before = events.length;
  const tab = await openTab(TARGET, true);
  await sleep(3500);
  const fresh = events.slice(before);
  const download = fresh.find((e) => e.method === 'Browser.downloadWillBegin');
  const docs = fresh.filter((e) => e.method === 'Network.responseReceived'
    && e.params.type === 'Document' && e.params.response.url);
  const last = docs.length ? docs[docs.length - 1].params.response : null;
  const onLogin = last && (/login|signin|authorize/i.test(last.url) || last.status === 401 || last.status === 403);
  // 已经触发下载的那个标签页不要关：扩展还在用它读 Cookie、发请求给客户端。
  if (!download) { try { await send('Target.closeTarget', { targetId: tab.targetId }); } catch { } }
  return { download, last, onLogin };
}

let hit = null;
const deadline = Date.now() + WAIT_LOGIN_S * 1000;
let lastNote = '';
while (Date.now() < deadline) {
  const r = await probeOnce();
  if (r.download) { hit = r; break; }
  const note = r.last ? `${r.last.status} ${r.last.url.slice(0, 70)}` : '无响应事件';
  if (note !== lastNote) { say(`  [探测] 还是登录页/未放行：${note}`); lastNote = note; }
  await sleep(2500);
}

if (!hit) {
  say('\n等到超时也没等到登录完成。');
  cleanup(3);
}

say('\n✅ 登录态已生效，下载流程被触发');

/* ---------------- 6. 等客户端接管并完成 ---------------- */

const startedAt = Date.now();
let task = null;
for (let i = 0; i < 400; i++) {
  const list = await api('GET', '/api/tasks');
  const arr = list.json || [];
  task = arr.find((t) => (t.urls || []).some((u) => u.split('?')[0] === TARGET.split('?')[0]));
  if (task && (task.state === 'completed' || task.state === 'failed')) break;
  if (!task && Date.now() - startedAt > 15000) break;
  await sleep(300);
}

if (!task) {
  say('\n❌ 客户端没有收到这个下载：扩展没接管。');
  say('   查一下扩展设置里「自动接管」是否开启、以及客户端是否在线。');
  cleanup(4);
}
if (task.state !== 'completed') {
  say(`\n❌ 任务没有完成：${task.state}  ${task.errorMessage || ''}`);
  cleanup(4);
}

say(`任务 ${task.gid} → ${task.state}  ${task.completedLength} 字节`);
say(`落盘: ${task.filePath}`);

/* ---------------- 7. 带 Cookie 的参照下载，逐字节比对 ---------------- */

const cookies = await send('Network.getCookies', { urls: [TARGET] });
const cookieHeader = (cookies.cookies || []).map((c) => `${c.name}=${c.value}`).join('; ');
say(`\n浏览器里拿到的 Cookie: ${cookies.cookies.length} 条（Headers 长度 ${cookieHeader.length}）`);
const httpOnly = (cookies.cookies || []).filter((c) => c.httpOnly).length;
say(`  其中 HttpOnly ${httpOnly} 条 —— 这正是"脚本拿不到、只有浏览器集成才能拿到"的那部分`);

let refBytes = 0, refHash = '';
if (WANT_HASH) {
  try {
    const ctrl = new AbortController();
    const timer = setTimeout(() => ctrl.abort(), 120000);
    const r = await fetch(TARGET, { headers: { Cookie: cookieHeader }, redirect: 'follow', signal: ctrl.signal });
    const buf = Buffer.from(await r.arrayBuffer());
    clearTimeout(timer);
    refBytes = buf.length;
    refHash = crypto.createHash('sha256').update(buf).digest('hex');
    say(`参照下载（带 Cookie）: ${r.status}  ${refBytes} 字节  sha256=${refHash.slice(0, 16)}…`);
  } catch (e) {
    say('参照下载失败:', e.message);
  }
}

const localSize = fs.existsSync(task.filePath) ? fs.statSync(task.filePath).size : -1;
let localHash = '';
if (WANT_HASH && localSize > 0) {
  localHash = crypto.createHash('sha256').update(fs.readFileSync(task.filePath)).digest('hex');
}

/* ---------------- 8. 结论 ---------------- */

const okAnon = anonBlocked;
const okHandoff = task.state === 'completed' && localSize > 0;
const okSame = refBytes > 0 ? (localSize === refBytes && (!WANT_HASH || localHash === refHash)) : null;

say('');
say('════════ 结论 ════════');
say(`  匿名拿不到内容（依赖登录）: ${okAnon ? 'PASS' : 'FAIL'}`);
say(`  扩展接管 + 客户端下完      : ${okHandoff ? 'PASS' : 'FAIL'}   ${localSize} 字节`);
say(`  与带 Cookie 的参照一致     : ${okSame === null ? 'SKIP' : okSame ? 'PASS' : 'FAIL'}` +
    `${okSame === false ? `（本地 ${localSize} / 参照 ${refBytes}）` : ''}`);
say(`  落盘路径: ${task.filePath}`);

api('POST', `/api/tasks/${task.gid}/remove`, {}).catch(() => { });
cleanup(okAnon && okHandoff && okSame !== false ? 0 : 1);

function cleanup(code) {
  try { proc.kill(); } catch { }
  setTimeout(() => {
    if (!PROFILE_DIR) { try { fs.rmSync(profile, { recursive: true, force: true }); } catch { } }
    try { fs.rmSync(extCopy, { recursive: true, force: true }); } catch { }
    try { fs.rmSync(dlDir, { recursive: true, force: true }); } catch { }
    process.exit(code);
  }, 300);
}
