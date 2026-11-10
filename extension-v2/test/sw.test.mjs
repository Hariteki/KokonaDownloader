/**
 * SW 级联调测试：加载**真实的** sw/background.js，用假 chrome.* 触发事件，
 * 用一个**真实的 HTTP 服务**充当客户端 API，断言实际发出的请求。
 * 这一层覆盖"逻辑 + 网络 + 事件注册"，不依赖浏览器。
 */

import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { makeChrome } from './fake-chrome.mjs';
import { __resetForceAttachmentCache } from '../lib/dnr.js';
import { FORCE_ATTACHMENT_EXTENSIONS, FORCE_ATTACHMENT_RULE_IDS } from '../lib/force-attachment.js';

/** 起一个假客户端：记录请求，按配置返回状态码。 */
async function startStub(opts = {}) {
  const requests = [];
  let settingsCalls = 0;
  let mode = opts.mode || 'ok';
  const server = http.createServer((req, res) => {
    let body = '';
    req.on('data', (c) => { body += c; });
    req.on('end', () => {
      const entry = { method: req.method, url: req.url, headers: req.headers, body };
      requests.push(entry);

      // 本机"文件主机"：默认兑现 Range（206）；rangeSupport=false 时假装不支持（200 整份返回）
      if (req.url.startsWith('/file/')) {
        if (req.headers.range && opts.rangeSupport !== false) {
          res.writeHead(206, {
            'Content-Type': 'application/octet-stream',
            'Content-Range': 'bytes 0-1/4',
            'Content-Length': '2',
          });
          return res.end('ab');
        }
        res.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Content-Length': '4' });
        return res.end('abcd');
      }

      const send = (code, obj) => {
        res.writeHead(code, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify(obj));
      };

      if (req.url === '/api/ping') return send(200, { ok: true, version: '9.9.9' });
      if (req.url === '/api/stats') {
        const role = mode === 'unauthorized' ? 401 : 200;
        return send(role, role === 200 ? { ok: true } : { ok: false });
      }
      if (req.url === '/api/settings') {
        settingsCalls += 1;
        return send(200, { interceptBrowserDownloads: opts.intercept !== false });
      }
      if (req.url === '/api/download') {
        if (mode === 'unauthorized') return send(401, { ok: false });
        if (mode === 'busy') return send(423, { ok: false });
        if (mode === 'server-error') return send(500, { ok: false });
        const parsed = JSON.parse(body || '{}');
        return send(opts.duplicate ? 409 : 200, { ok: true, gid: 'g-' + requests.length, duplicate: !!opts.duplicate });
      }
      send(404, { ok: false });
    });
  });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  const port = server.address().port;
  return {
    port,
    requests,
    setMode: (m) => { mode = m; },
    downloads: () => requests.filter((r) => r.url === '/api/download'),
    settingsCalls: () => settingsCalls,
    close: () => new Promise((r) => server.close(r)),
  };
}

let seq = 0;
/** 每个用例一份干净的 SW：新 chrome 对象 + 唯一 import URL（绕开 ESM 缓存）。 */
async function bootWorker(chromeObj) {
  globalThis.chrome = chromeObj;
  seq += 1;
  return import('../sw/background.js?case=' + seq);
}

const wait = (ms) => new Promise((r) => setTimeout(r, ms));

async function waitFor(predicate, timeoutMs = 3000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (predicate()) return true;
    await wait(20);
  }
  return false;
}

function makeDownloadItem(overrides = {}) {
  return Object.assign({
    id: 42,
    url: 'https://example.com/a.zip',
    state: 'in_progress',
    filename: '',
    referrer: 'https://example.com/page',
    startTime: new Date().toISOString(),
  }, overrides);
}

/** 指向假「文件主机」的下载地址：接管链路要用它，这样 Range 探测结果是确定的。 */
function fileUrl(stub, name) {
  return `http://127.0.0.1:${stub.port}/file/${name}`;
}

function bootChrome(stub, extra = {}) {
  return makeChrome(Object.assign({
    local: { settings: { host: '127.0.0.1', port: stub.port, secret: 's3cret', autoCapture: true } },
    cookies: [{ name: 'sid', value: 'abc123' }, { name: 'uid', value: '1' }],
    hostAccess: true,
  }, extra));
}

test('自动接管：带上 Cookie/UA 转发，并取消浏览器下载', async () => {
  const stub = await startStub();
  const chrome = bootChrome(stub);
  try {
    await bootWorker(chrome);
    const url = fileUrl(stub, 'a.zip');
    chrome.downloads.onCreated.__fire(makeDownloadItem({ url }));

    assert.equal(await waitFor(() => stub.downloads().length === 1), true, '没有发出 /api/download');

    const req = stub.downloads()[0];
    assert.equal(req.method, 'POST');
    assert.equal(req.headers['x-kokona-secret'], 's3cret');
    const payload = JSON.parse(req.body);
    assert.deepEqual(payload.urls, [url]);
    assert.equal(payload.referer, 'https://example.com/page');
    assert.equal('filename' in payload, false, '不该猜文件名');
    assert.deepEqual(payload.headers.filter((h) => h.startsWith('Cookie: ')), ['Cookie: sid=abc123; uid=1']);
    assert.equal(payload.headers.some((h) => h.startsWith('User-Agent: ')), true);

    assert.equal(await waitFor(() => chrome.__callsOf('downloads.cancel').length === 1), true, '没有取消浏览器下载');
    assert.equal(chrome.__callsOf('downloads.erase').length, 1);
  } finally {
    await stub.close();
  }
});

test('失败不取消浏览器下载：401 与离线都要保留原生下载', async () => {
  const stub = await startStub({ mode: 'unauthorized' });
  const chrome = bootChrome(stub);
  try {
    await bootWorker(chrome);
    chrome.downloads.onCreated.__fire(makeDownloadItem({ url: fileUrl(stub, '401.zip') }));
    assert.equal(await waitFor(() => stub.downloads().length === 1), true);
    await wait(100);
    assert.equal(chrome.__callsOf('downloads.cancel').length, 0, '失败时绝不能取消浏览器下载');
    assert.equal(chrome.__callsOf('notifications.create').length >= 1, true);
  } finally {
    await stub.close();
  }

  // 离线：端口关掉再触发
  const chrome2 = bootChrome(stub); // stub 已关闭，端口不可达
  await bootWorker(chrome2);
  chrome2.downloads.onCreated.__fire(makeDownloadItem({ url: fileUrl(stub, 'offline.zip') }));
  await wait(400);
  assert.equal(chrome2.__callsOf('downloads.cancel').length, 0);
  const notes = chrome2.__callsOf('notifications.create');
  assert.equal(notes.length >= 1, true);
  assert.match(notes[0].args[0].title, /未运行/);
});

test('并发重复项只转发一次', async () => {
  const stub = await startStub();
  const chrome = bootChrome(stub);
  try {
    await bootWorker(chrome);
    const item = makeDownloadItem({ id: 1, url: fileUrl(stub, 'dup.zip') });
    chrome.downloads.onCreated.__fire({ ...item });
    chrome.downloads.onCreated.__fire({ ...item, id: 2 });
    assert.equal(await waitFor(() => stub.downloads().length >= 1), true);
    await wait(200);
    assert.equal(stub.downloads().length, 1, '同一链接被转发了多次');
    assert.equal(chrome.__callsOf('downloads.cancel').length, 2, '两个下载项都应被取消');
  } finally {
    await stub.close();
  }
});

test('SW 重启后仍能挡住同一链接的重复转发', async () => {
  const stub = await startStub();
  const chrome1 = bootChrome(stub);
  await bootWorker(chrome1);
  const keep = fileUrl(stub, 'keep.zip');
  chrome1.downloads.onCreated.__fire(makeDownloadItem({ id: 11, url: keep }));
  assert.equal(await waitFor(() => stub.downloads().length === 1), true);
  await wait(100);

  // 用同一份 storage.session 模拟 SW 被回收后重新拉起
  const chrome2 = bootChrome(stub, { session: chrome1.storage.session.data });
  await bootWorker(chrome2);
  chrome2.downloads.onCreated.__fire(makeDownloadItem({ id: 12, url: keep }));
  await wait(300);
  assert.equal(stub.downloads().length, 1, '重启后重复转发了');
  assert.equal(chrome2.__callsOf('downloads.cancel').length, 1, '重复项应被静默取消');
  await stub.close();
});

test('历史回放不接管（已完成 / 早于会话起点）', async () => {
  const stub = await startStub();
  const chrome = bootChrome(stub);
  try {
    await bootWorker(chrome);
    chrome.downloads.onCreated.__fire(makeDownloadItem({ id: 21, state: 'complete' }));
    chrome.downloads.onCreated.__fire(makeDownloadItem({ id: 23, startTime: '2020-01-01T00:00:00Z' }));
    await wait(400);
    assert.equal(stub.downloads().length, 0, '历史项不该被接管');
    assert.equal(chrome.__callsOf('downloads.cancel').length, 0, '历史项也不该被取消');
  } finally {
    await stub.close();
  }
});

test('关闭自动接管（客户端口径）后不拦截，但右键手动发送仍然可用', async () => {
  const stub = await startStub({ intercept: false });
  const chrome = bootChrome(stub, {
    local: { settings: { host: '127.0.0.1', port: stub.port, secret: 's3cret', autoCapture: false } },
  });
  try {
    await bootWorker(chrome);
    chrome.downloads.onCreated.__fire(makeDownloadItem({ id: 31 }));
    await wait(300);
    assert.equal(stub.downloads().length, 0, '客户端关闭拦截时不该自动接管');

    // 手动发送：走 runtime.onMessage
    const sent = await new Promise((resolve) => {
      chrome.runtime.onMessage.__fire(
        { type: 'manual-download', url: 'https://example.com/manual.zip', referrer: 'https://example.com/p' },
        {},
        resolve,
      );
    });
    assert.equal(sent.ok, true);
    assert.equal(stub.downloads().length, 1);
    const payload = JSON.parse(stub.downloads()[0].body);
    assert.deepEqual(payload.urls, ['https://example.com/manual.zip']);
  } finally {
    await stub.close();
  }
});

test('右键菜单创建三个入口（下载 / 不接管 / 页面级）', async () => {
  const stub = await startStub();
  const chrome = bootChrome(stub);
  try {
    await bootWorker(chrome);
    chrome.runtime.onInstalled.__fire({ reason: 'install' });
    await wait(150);
    const created = chrome.__callsOf('menus.create').map((c) => c.args[0]);
    assert.equal(created.length, 3);
    assert.deepEqual(created[0].contexts, ['link', 'image', 'audio', 'video']);
    assert.match(created[0].title, /海兔下载器/);
    assert.equal(created[1].id, 'kokona-browser');
    assert.match(created[1].title, /浏览器/);
    assert.deepEqual(created[1].contexts, ['link', 'image', 'audio', 'video']);
    assert.equal(created[2].contexts.includes('page'), true);
    // 安装后应打开引导页
    assert.equal(chrome.__callsOf('tabs.create').length, 1);
  } finally {
    await stub.close();
  }
});

test('已带建议文件名的新下载仍要接管（filename 不能当回放证据）', async () => {
  const stub = await startStub();
  const chrome = bootChrome(stub);
  try {
    await bootWorker(chrome);
    chrome.downloads.onCreated.__fire(makeDownloadItem({
      id: 51,
      url: fileUrl(stub, 'named.zip'),
      filename: 'C:////Users////x////Downloads////named.zip',
    }));
    assert.equal(await waitFor(() => stub.downloads().length === 1), true, '带文件名的正常下载被误杀了');
    assert.equal(await waitFor(() => chrome.__callsOf('downloads.cancel').length === 1), true);
  } finally {
    await stub.close();
  }
});

test('拦截开关以客户端为准：客户端关闭时不接管', async () => {
  const stub = await startStub({ intercept: false });
  const chrome = bootChrome(stub);           // 本地 autoCapture=true
  try {
    await bootWorker(chrome);
    chrome.downloads.onCreated.__fire(makeDownloadItem({ id: 61, url: 'https://example.com/c1.zip' }));
    await wait(600);
    assert.equal(stub.settingsCalls() >= 1, true, '应该向客户端查询过设置');
    assert.equal(stub.downloads().length, 0, '客户端说关闭，扩展仍接管了');
    assert.equal(chrome.__callsOf('downloads.cancel').length, 0, '不该取消浏览器下载');
  } finally {
    await stub.close();
  }
});

test('本地关闭但客户端开启：仍按客户端接管', async () => {
  const stub = await startStub({ intercept: true });
  const chrome = bootChrome(stub, {
    local: { settings: { host: '127.0.0.1', port: stub.port, secret: 's3cret', autoCapture: false } },
  });
  try {
    await bootWorker(chrome);
    chrome.downloads.onCreated.__fire(makeDownloadItem({ id: 62, url: fileUrl(stub, 'c2.zip') }));
    assert.equal(await waitFor(() => stub.downloads().length === 1), true, '客户端说开启，却没有接管');
    assert.equal(await waitFor(() => chrome.__callsOf('downloads.cancel').length === 1), true);
  } finally {
    await stub.close();
  }
});

test('设置查询有缓存：连续下载只查一次', async () => {
  const stub = await startStub({ intercept: true });
  const chrome = bootChrome(stub);
  try {
    await bootWorker(chrome);
    chrome.downloads.onCreated.__fire(makeDownloadItem({ id: 63, url: fileUrl(stub, 'k1.zip') }));
    await waitFor(() => stub.downloads().length === 1);
    chrome.downloads.onCreated.__fire(makeDownloadItem({ id: 64, url: fileUrl(stub, 'k2.zip') }));
    await waitFor(() => stub.downloads().length === 2);
    assert.equal(stub.settingsCalls(), 1, `设置被查询了 ${stub.settingsCalls()} 次，缓存没生效`);
  } finally {
    await stub.close();
  }
});

test('客户端不可达：退回本地设置，且不取消浏览器下载', async () => {
  const stub = await startStub();
  const chrome = bootChrome(stub);
  await bootWorker(chrome);
  await stub.close();   // 先关掉，制造离线

  chrome.downloads.onCreated.__fire(makeDownloadItem({ id: 65, url: fileUrl(stub, 'off.zip') }));
  await wait(800);
  assert.equal(chrome.__callsOf('downloads.cancel').length, 0, '离线时绝不能取消浏览器下载');
  const notes = chrome.__callsOf('notifications.create');
  assert.equal(notes.length >= 1, true);
});

test('409 视为已存在：取消浏览器下载但不提示失败', async () => {
  const stub = await startStub({ duplicate: true });
  const chrome = bootChrome(stub);
  try {
    await bootWorker(chrome);
    chrome.downloads.onCreated.__fire(makeDownloadItem({ id: 41, url: fileUrl(stub, 'dup409.zip') }));
    assert.equal(await waitFor(() => stub.downloads().length === 1), true);
    assert.equal(await waitFor(() => chrome.__callsOf('downloads.cancel').length === 1), true);
  } finally {
    await stub.close();
  }
});

/* ---------------- 不支持分段的链接 / 「不接管」菜单 ---------------- */

test('服务器忽略 Range（200）时不接管，保留浏览器下载并提示', async () => {
  const stub = await startStub({ rangeSupport: false });
  const chrome = bootChrome(stub);
  try {
    await bootWorker(chrome);
    chrome.downloads.onCreated.__fire(makeDownloadItem({ id: 71, url: fileUrl(stub, 'norange.zip') }));
    await wait(500);
    assert.equal(stub.downloads().length, 0, '不支持分段的链接不该转发给客户端');
    assert.equal(chrome.__callsOf('downloads.cancel').length, 0, '必须保留浏览器自己的下载');
    const notes = chrome.__callsOf('notifications.create');
    assert.equal(notes.length >= 1, true);
    assert.match(notes[0].args[0].message, /不支持多线程/);
  } finally {
    await stub.close();
  }
});

test('Range 探测无法判断时照常接管（不误伤）', async () => {
  const stub = await startStub();
  const chrome = bootChrome(stub);
  try {
    await bootWorker(chrome);
    // 端口 1 没人监听：探测失败 → unknown → 维持接管
    chrome.downloads.onCreated.__fire(makeDownloadItem({ id: 72, url: 'http://127.0.0.1:1/unknown.zip' }));
    assert.equal(await waitFor(() => stub.downloads().length === 1), true, '探测失败时不该放弃接管');
    assert.equal(await waitFor(() => chrome.__callsOf('downloads.cancel').length === 1), true);
  } finally {
    await stub.close();
  }
});

test('右键「用浏览器下载（不接管）」：交给浏览器且不转发', async () => {
  const stub = await startStub();
  const chrome = bootChrome(stub);
  try {
    await bootWorker(chrome);
    const url = fileUrl(stub, 'browser.zip');
    chrome.contextMenus.onClicked.__fire({ menuItemId: 'kokona-browser', linkUrl: url }, { url: 'https://example.com/p' });

    assert.equal(await waitFor(() => chrome.__callsOf('downloads.download').length === 1), true, '没有调用浏览器下载');
    assert.equal(chrome.__callsOf('downloads.download')[0].args[0].url, url);

    // 浏览器随后才会触发 onCreated：一次性放行，既不转发也不取消
    chrome.downloads.onCreated.__fire(makeDownloadItem({ id: 81, url }));
    await wait(500);
    assert.equal(stub.downloads().length, 0, '「不接管」的链接被转发了');
    assert.equal(chrome.__callsOf('downloads.cancel').length, 0, '「不接管」的链接被取消了');
  } finally {
    await stub.close();
  }
});

test('「不接管」只放行一次：同链接的新下载仍会被接管', async () => {
  const stub = await startStub();
  const chrome = bootChrome(stub);
  try {
    await bootWorker(chrome);
    const url = fileUrl(stub, 'once.zip');
    chrome.contextMenus.onClicked.__fire({ menuItemId: 'kokona-browser', linkUrl: url }, { url: 'https://example.com/p' });
    assert.equal(await waitFor(() => chrome.__callsOf('downloads.download').length === 1), true);

    chrome.downloads.onCreated.__fire(makeDownloadItem({ id: 82, url }));
    await wait(400);
    assert.equal(stub.downloads().length, 0, '放行项被转发了');

    // 用户之后又下了一次同一个链接：这次应该正常接管
    chrome.downloads.onCreated.__fire(makeDownloadItem({ id: 83, url }));
    assert.equal(await waitFor(() => stub.downloads().length === 1), true, '放行名单没有被消费掉');
    assert.equal(await waitFor(() => chrome.__callsOf('downloads.cancel').length === 1), true);
  } finally {
    await stub.close();
  }
});

/* ---------------- EXT-403：DNR 强制附件 ---------------- */

/** forceAttachment 开的本地设置（其余用默认）。 */
function bootChromeWithForce(stub, extra = {}) {
  return bootChrome(stub, Object.assign({
    local: {
      settings: {
        host: '127.0.0.1', port: stub.port, secret: 's3cret',
        autoCapture: true, forceAttachment: true,
      },
    },
  }, extra));
}

test('强制附件：开关打开 + 已授权 + 接管开着 → 下发一条 modifyHeaders 规则', async () => {
  __resetForceAttachmentCache();
  const stub = await startStub({ intercept: true });
  const chrome = bootChromeWithForce(stub);
  try {
    await bootWorker(chrome);
    chrome.runtime.onInstalled.__fire({ reason: 'update' });

    const want = FORCE_ATTACHMENT_EXTENSIONS.length;
    assert.equal(await waitFor(() => chrome.__dnrRules().length === want, 2000), true,
      `规则没有下发（期望 ${want} 条，实际 ${chrome.__dnrRules().length}）`);

    const rule = chrome.__dnrRules().find((r) => r.condition.regexFilter.includes('\\.pdf'));
    assert.ok(rule, '缺少 pdf 规则');
    assert.equal(FORCE_ATTACHMENT_RULE_IDS.includes(rule.id), true);
    assert.equal(rule.action.type, 'modifyHeaders');
    assert.deepEqual(rule.action.responseHeaders, [
      { header: 'content-disposition', operation: 'set', value: 'attachment' },
    ]);
    assert.deepEqual(rule.condition.resourceTypes, ['main_frame']);
    assert.equal(new RegExp(rule.condition.regexFilter, 'i').test('https://a.com/x.pdf'), true);
    assert.equal(new RegExp(rule.condition.regexFilter, 'i').test('https://a.com/x.zip'), false);
  } finally {
    await stub.close();
  }
});

test('强制附件：没有站点权限时不下发（DNR 改响应头必须有 host 权限）', async () => {
  __resetForceAttachmentCache();
  const stub = await startStub({ intercept: true });
  const chrome = bootChromeWithForce(stub, { hostAccess: false });
  try {
    await bootWorker(chrome);
    chrome.runtime.onInstalled.__fire({ reason: 'update' });
    await wait(300);
    assert.equal(chrome.__dnrRules().length, 0, '无权限时不该注册规则');
  } finally {
    await stub.close();
  }
});

test('强制附件：客户端关闭自动接管时不下发（避免只强制下载却不接管）', async () => {
  __resetForceAttachmentCache();
  const stub = await startStub({ intercept: false });
  const chrome = bootChromeWithForce(stub);
  try {
    await bootWorker(chrome);
    chrome.runtime.onInstalled.__fire({ reason: 'update' });
    await wait(400);
    assert.equal(chrome.__dnrRules().length, 0, '接管关闭时强制下载只有副作用');
  } finally {
    await stub.close();
  }
});

test('强制附件：关掉开关后清掉已下发的规则', async () => {
  __resetForceAttachmentCache();
  const stub = await startStub({ intercept: true });
  const chrome = bootChromeWithForce(stub);
  try {
    await bootWorker(chrome);
    chrome.runtime.onInstalled.__fire({ reason: 'update' });
    assert.equal(await waitFor(() => chrome.__dnrRules().length > 0), true);

    const saved = await new Promise((resolve) => {
      chrome.runtime.onMessage.__fire({ type: 'save-settings', patch: { forceAttachment: false } }, {}, resolve);
    });
    assert.equal(saved.ok, true);
    assert.equal(await waitFor(() => chrome.__dnrRules().length === 0, 2000), true, '规则没有被清除');
  } finally {
    await stub.close();
  }
});

test('强制附件：用户授权站点后自动补下规则', async () => {
  __resetForceAttachmentCache();
  const stub = await startStub({ intercept: true });
  const chrome = bootChromeWithForce(stub, { hostAccess: false });
  try {
    await bootWorker(chrome);
    chrome.runtime.onInstalled.__fire({ reason: 'update' });
    await wait(200);
    assert.equal(chrome.__dnrRules().length, 0);

    // 用户在弹窗里点了「授权访问网站」：权限变了要立刻补规则
    chrome.permissions.contains = async () => true;
    chrome.permissions.onAdded.__fire({ origins: ['https://*/*'] });
    assert.equal(await waitFor(() => chrome.__dnrRules().length > 0, 2000), true, '授权后没有补下规则');
  } finally {
    await stub.close();
  }
});
