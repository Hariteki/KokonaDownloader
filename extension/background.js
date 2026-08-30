/*
 * 海兔下载器扩展后台（MV3 Service Worker）。
 * 职责：
 *  1. 自动捕获浏览器下载（downloads.onCreated）→ 转发客户端 → 取消并清除浏览器下载项；
 *  2. 右键菜单「使用海兔下载器下载」手动发送链接/图片；
 *  3. 定时 ping + 密钥校验客户端，维护连接状态并反映到工具栏徽标与弹窗红绿灯；
 *  4. 客户端离线/密钥错误等场景给出友好通知，绝不静默失败。
 */
importScripts('logic.js');

const SETTINGS_KEY = 'kokona_settings';
const PING_ALARM = 'kokona-ping';

/** 内存缓存（service worker 可能随时被回收，持久数据一律走 storage）。 */
let cachedSettings = null;
let connState = { online: false, keyValid: false, checkedAt: 0, version: '', lastError: '', lastCode: '' };
const forwardingNow = new Set(); // 正在转发中的 urlKey，防止并发重复转发同一链接

// ---------- 设置 ----------

async function loadSettings() {
  if (cachedSettings) return cachedSettings;
  const data = await chrome.storage.local.get(SETTINGS_KEY);
  cachedSettings = KokonaLogic.normalizeSettings(data[SETTINGS_KEY]);
  return cachedSettings;
}

async function saveSettings(raw) {
  const normalized = KokonaLogic.normalizeSettings(raw);
  cachedSettings = normalized;
  await chrome.storage.local.set({ [SETTINGS_KEY]: normalized });
  return normalized;
}

// ---------- HTTP ----------

async function fetchWithTimeout(url, options, timeoutMs) {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    return await fetch(url, Object.assign({ signal: ctrl.signal }, options || {}));
  } finally {
    clearTimeout(timer);
  }
}

/** 探测客户端是否在线（/api/ping 免鉴权）。 */
async function ping(settings) {
  const url = KokonaLogic.baseUrl(settings) + '/api/ping';
  const resp = await fetchWithTimeout(url, { method: 'GET' }, 1500);
  if (!resp.ok) throw new Error('HTTP ' + resp.status);
  const body = await resp.json();
  if (!body || body.ok !== true) throw new Error('意外的 ping 响应');
  return body.version || '';
}

/** 校验连接密钥（/api/stats 需要正确密钥，401 即密钥错误）。 */
async function verifyKey(settings) {
  const url = KokonaLogic.baseUrl(settings) + '/api/stats';
  let resp;
  try {
    resp = await fetchWithTimeout(url, {
      method: 'GET',
      headers: { 'X-Kokona-Secret': settings.secret || '' }
    }, 2500);
  } catch (e) {
    const err = new Error('无法连接到海兔客户端');
    err.code = 'offline';
    throw err;
  }
  if (resp.status === 401) {
    const err = new Error('连接密钥错误');
    err.code = 'unauthorized';
    throw err;
  }
  if (!resp.ok) {
    const err = new Error('客户端返回 HTTP ' + resp.status);
    err.code = 'http';
    throw err;
  }
  return true;
}

/**
 * 转发下载任务到客户端。
 * 返回 { ok, gid, duplicate }（duplicate=客户端任务列表已存在同 URL 任务）；失败时抛出带 code 的错误：
 *   offline=无法连接 / unauthorized=密钥错误 / rejected=客户端拒绝 / http=其他错误
 */
async function forwardDownload(settings, payload) {
  const url = KokonaLogic.baseUrl(settings) + '/api/download';
  let resp;
  try {
    resp = await fetchWithTimeout(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-Kokona-Secret': settings.secret || ''
      },
      body: JSON.stringify(payload)
    }, 5000);
  } catch (e) {
    const err = new Error('无法连接到海兔客户端');
    err.code = 'offline';
    throw err;
  }
  let body = null;
  try { body = await resp.json(); } catch (e) { /* 忽略解析失败 */ }

  if (resp.status === 401) {
    const err = new Error('连接密钥错误，请点击工具栏图标重新粘贴密钥');
    err.code = 'unauthorized';
    throw err;
  }
  if (!resp.ok) {
    const err = new Error((body && body.message) || ('客户端返回错误 HTTP ' + resp.status));
    err.code = resp.status === 400 ? 'rejected' : 'http';
    throw err;
  }
  return { ok: true, gid: body && body.gid, duplicate: !!(body && body.duplicate) };
}

// ---------- 连接状态 ----------

/** 完整连接检测：在线探测 + 密钥校验，两者都通过才算「已连接」。 */
async function checkConnection(settings) {
  let online = false, keyValid = false, version = '', code = '';
  try {
    version = await ping(settings);
    online = true;
  } catch (e) {
    code = 'offline';
  }
  if (online) {
    try {
      await verifyKey(settings);
      keyValid = true;
    } catch (e) {
      code = e.code || 'http';
    }
  }
  return { online: online, keyValid: keyValid, version: version, code: code };
}

function buildConnState(r) {
  return {
    online: r.online,
    keyValid: r.keyValid,
    checkedAt: Date.now(),
    version: r.version,
    lastError: r.keyValid ? '' : (r.code === 'unauthorized' ? '连接密钥错误' : '无法连接到客户端'),
    lastCode: r.keyValid ? '' : r.code
  };
}

async function refreshStatus() {
  const s = await loadSettings();
  const r = await checkConnection(s);
  connState = buildConnState(r);
  await applyBadge();
  return connState;
}

async function applyBadge() {
  try {
    if (connState.online && connState.keyValid) {
      await chrome.action.setBadgeBackgroundColor({ color: '#2E9E5B' });
      await chrome.action.setBadgeText({ text: '✓' });
      await chrome.action.setTitle({ title: '海兔下载器：已连接客户端' });
    } else {
      await chrome.action.setBadgeBackgroundColor({ color: '#C42B1C' });
      await chrome.action.setBadgeText({ text: '!' });
      await chrome.action.setTitle({
        title: connState.online
          ? '海兔下载器：连接密钥错误，请点击图标重新粘贴密钥'
          : '海兔下载器：客户端未连接'
      });
    }
  } catch (e) { /* 徽标失败不影响主流程 */ }
}

// ---------- 通知 ----------

function notify(title, message) {
  try {
    chrome.notifications.create({
      type: 'basic',
      iconUrl: 'icons/icon128.png',
      title: title,
      message: message,
      priority: 2
    });
  } catch (e) { /* 忽略 */ }
}

// ---------- 捕获与转发 ----------

// 旧版本遗留的 7 天转发历史键：新版重复判定已改为客户端任务列表，启动时清除
const LEGACY_FORWARDED_KEY = 'kokona_forwarded';

function clearLegacyForwarded() {
  try { chrome.storage.local.remove(LEGACY_FORWARDED_KEY); } catch (e) { /* 忽略 */ }
}

/**
 * 自动捕获入口：浏览器创建下载项时调用。
 * 策略：先转发（确认客户端接收成功）→ 再取消并清除浏览器下载项；
 * 重复判定以客户端任务列表为准（duplicate=true 表示任务已存在，同样拦截），
 * 转发失败则不干预浏览器下载，并通知用户原因。
 */
async function handleDownloadCreated(item) {
  const key = KokonaLogic.urlKey(item.url);
  // 同步占位去重：会话恢复可能并发触发同一链接的多个下载项
  if (forwardingNow.has(key)) {
    await cancelAndErase(item.id);
    return;
  }
  forwardingNow.add(key);
  try {
    const s = await loadSettings();
    if (!KokonaLogic.shouldCapture(item, s)) return;

    const payload = KokonaLogic.buildDownloadPayload(item, s);
    try {
      // 新建或客户端已有任务（duplicate）都算接收成功：拦截浏览器下载，交给客户端管理
      await forwardDownload(s, payload);
      await cancelAndErase(item.id);
    } catch (e) {
      // 转发失败：保留浏览器原生下载，友好提示
      if (e.code === 'offline') {
        connState.online = false;
        connState.keyValid = false;
        await applyBadge();
        notify('海兔客户端未运行', '已改用浏览器自带下载。请启动桌面客户端后重试。');
      } else if (e.code === 'unauthorized') {
        notify('海兔连接密钥错误', '下载已改用浏览器自带下载。请点击工具栏图标重新粘贴密钥。');
      } else {
        notify('海兔转发失败', (e.message || '未知错误') + '。已改用浏览器自带下载。');
      }
    }
  } finally {
    forwardingNow.delete(key);
  }
}

async function cancelAndErase(downloadId) {
  try { await chrome.downloads.cancel(downloadId); } catch (e) { /* 可能已结束 */ }
  try { await chrome.downloads.erase({ id: downloadId }); } catch (e) { /* 忽略 */ }
}

/** 手动发送（右键菜单 / 弹窗）。返回结果描述字符串。 */
async function manualSend(url, referrer) {
  const s = await loadSettings();
  if (!KokonaLogic.isSupportedUrl(url)) {
    return { ok: false, message: '不支持的链接类型（仅支持 http/https/ftp）' };
  }
  const key = KokonaLogic.urlKey(url);
  if (forwardingNow.has(key)) {
    return { ok: true, message: '该链接正在发送中，请稍候' };
  }
  forwardingNow.add(key);
  const payload = {
    urls: [url],
    filename: KokonaLogic.fileNameFromUrl(url)
  };
  if (referrer) payload.referer = referrer;
  try {
    const r = await forwardDownload(s, payload);
    return r.duplicate
      ? { ok: true, message: '客户端已有此任务，无需重复添加' }
      : { ok: true, message: '已发送到海兔客户端' };
  } catch (e) {
    if (e.code === 'offline') {
      connState.online = false;
      connState.keyValid = false;
      await applyBadge();
      return { ok: false, message: '海兔客户端未运行，请先启动桌面客户端' };
    }
    return { ok: false, message: e.message || '发送失败' };
  } finally {
    forwardingNow.delete(key);
  }
}

// ---------- 事件绑定 ----------

chrome.runtime.onInstalled.addListener(async () => {
  clearLegacyForwarded();
  await setupContextMenus();
  await chrome.alarms.create(PING_ALARM, { periodInMinutes: 1 });
  await refreshStatus();
});

chrome.runtime.onStartup.addListener(async () => {
  clearLegacyForwarded();
  await setupContextMenus();
  await refreshStatus();
});

async function setupContextMenus() {
  try {
    await chrome.contextMenus.removeAll();
    chrome.contextMenus.create({
      id: 'kokona-download-link',
      title: '使用海兔下载器下载',
      contexts: ['link', 'image', 'audio', 'video']
    });
  } catch (e) { /* 忽略 */ }
}

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId !== 'kokona-download-link') return;
  const url = info.linkUrl || info.srcUrl;
  if (!url) return;
  const result = await manualSend(url, tab && tab.url);
  if (!result.ok) {
    notify('海兔下载器', result.message);
  }
});

chrome.downloads.onCreated.addListener((item) => {
  // 异步处理，避免阻塞；onCreated 本身无法返回值拦截，拦截通过 cancel+erase 完成
  handleDownloadCreated(item).catch(() => { });
});

chrome.alarms.onAlarm.addListener(async (alarm) => {
  if (alarm.name === PING_ALARM) await refreshStatus();
});

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  (async () => {
    switch (msg && msg.type) {
      case 'get-status': {
        const s = await loadSettings();
        // 状态超过 30 秒则顺手刷新一次
        if (Date.now() - connState.checkedAt > 30000) await refreshStatus();
        sendResponse({ status: connState, settings: s });
        break;
      }
      case 'check-now': {
        const st = await refreshStatus();
        sendResponse({ status: st });
        break;
      }
      case 'test-key': {
        // 弹窗「粘贴密钥立即连接」：合并当前设置后先测，通过才提交保存
        const current = await loadSettings();
        const merged = KokonaLogic.normalizeSettings(Object.assign({}, current, msg.settings || {}));
        const r = await checkConnection(merged);
        let saved = null;
        if (r.keyValid || r.code === 'offline') {
          // 验证通过直接保存；客户端离线无法验证时也保存，客户端启动后即可自动连上
          saved = await saveSettings(merged);
        }
        connState = buildConnState(r);
        await applyBadge();
        sendResponse({
          ok: r.keyValid,
          code: r.code,
          version: r.version,
          status: connState,
          settings: saved || current
        });
        break;
      }
      case 'save-settings': {
        // 选项页只提交地址/端口/开关，密钥保留（在弹窗中管理）
        const cur = await loadSettings();
        const saved2 = await saveSettings(Object.assign({}, cur, msg.settings || {}));
        const st2 = await refreshStatus();
        sendResponse({ settings: saved2, status: st2 });
        break;
      }
      case 'send-url': {
        const result = await manualSend(msg.url, msg.referrer);
        sendResponse(result);
        break;
      }
      default:
        sendResponse({ ok: false, message: '未知消息类型' });
    }
  })().catch((e) => {
    try { sendResponse({ ok: false, message: e.message || String(e) }); } catch (_) { }
  });
  return true; // 异步响应
});
