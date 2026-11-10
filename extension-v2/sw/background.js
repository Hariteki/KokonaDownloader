/**
 * Service Worker：只做事件路由与副作用（网络、存储、UI 反馈）。
 * 业务判定全部在 lib/logic.js（纯函数，可单测）。
 */

import {
  buildPayload, mapDownloadResponse, shouldCapture, normalizeSettings, urlKey, baseUrl,
  probeNotice,
} from '../lib/logic.js';
import { ping, verifyKey, postDownload } from '../lib/api.js';
import {
  loadSettings, saveSettings, getSessionStart, recentForwards, markForwarded,
  addBypass, consumeBypass,
} from '../lib/settings.js';
import { collectSession, hasHostAccess, requestHostAccess } from '../lib/cookies.js';
import { resolveAutoCapture, invalidateInterceptCache } from '../lib/intercept.js';
import { syncForceAttachment } from '../lib/dnr.js';
import { probeRangeSupport } from '../lib/probe.js';

const MENU_DOWNLOAD = 'kokona-download';
const MENU_BROWSER = 'kokona-browser';
const MENU_PAGE = 'kokona-download-page';
const RECENT_WINDOW_MS = 5 * 60 * 1000;

/** 正在转发中的键（内存锁，挡同一 SW 内的并发重复项）。 */
const inFlight = new Set();

/* ---------------- 生命周期 ---------------- */

chrome.runtime.onInstalled.addListener(async (details) => {
  await setupMenus();
  await getSessionStart();
  await syncForceAttachment({ force: true });
  await refreshBadge();
  if (details && details.reason === 'install') {
    chrome.tabs.create({ url: chrome.runtime.getURL('ui/welcome.html') });
  }
});

chrome.runtime.onStartup.addListener(async () => {
  await setupMenus();
  await syncForceAttachment({ force: true });
  await refreshBadge();
});

// 站点权限变了，DNR 规则能不能生效也随之变化（改响应头必须有对应 host 权限）
chrome.permissions.onAdded.addListener(() => { syncForceAttachment({ force: true }).catch(() => { }); });
chrome.permissions.onRemoved.addListener(() => { syncForceAttachment({ force: true }).catch(() => { }); });

/* ---------------- 右键菜单 ---------------- */

async function setupMenus() {
  try {
    await chrome.contextMenus.removeAll();
    chrome.contextMenus.create({
      id: MENU_DOWNLOAD,
      title: '使用海兔下载器下载',
      contexts: ['link', 'image', 'audio', 'video'],
    });
    chrome.contextMenus.create({
      id: MENU_BROWSER,
      title: '用浏览器下载（不接管）',
      contexts: ['link', 'image', 'audio', 'video'],
    });
    chrome.contextMenus.create({
      id: MENU_PAGE,
      title: '用海兔下载本页所有链接',
      contexts: ['page', 'selection'],
    });
  } catch { /* 菜单重复创建时忽略 */ }
}

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId === MENU_DOWNLOAD) {
    const url = info.linkUrl || info.srcUrl;
    if (url) manualSend(url, tab && tab.url);
  } else if (info.menuItemId === MENU_BROWSER) {
    const url = info.linkUrl || info.srcUrl;
    if (url) browserDownload(url);
  } else if (info.menuItemId === MENU_PAGE) {
    collectPageLinks(tab);
  }
});

/**
 * 「不接管」：先把这个链接记入一次性放行名单，再交给浏览器下载。
 * 必须"先记再下"——onCreated 可能同步于 download() 的 Promise 之前到达。
 */
async function browserDownload(url) {
  await addBypass(urlKey(url));
  try {
    await chrome.downloads.download({ url, conflictAction: 'uniquify' });
  } catch (e) {
    notify('海兔下载器', '浏览器无法下载该链接：' + (e && e.message ? e.message : e));
  }
}

/** 右键手动发送：不判定 autoCapture，用户点了就是要下。 */
async function manualSend(url, referrer) {
  const settings = await loadSettings();
  const session = await collectSession(url);
  const payload = buildPayload({ url, referrer, session });
  try {
    const { status, body } = await postDownload(baseUrl(settings), settings.secret, payload);
    const mapped = mapDownloadResponse(status, body);
    if (!mapped.ok) {
      notify('海兔下载器', mapped.message);
      await refreshBadge();
      return mapped;
    }
    if (mapped.duplicate) notify('海兔下载器', '客户端已有该任务，无需重复添加');
    return mapped;
  } catch (e) {
    await handleOffline(e);
    return { ok: false, code: 'offline', message: e.message };
  }
}

/** 「下载本页所有链接」：activeTab 授权后按需注入收集器（不常驻 content script）。 */
async function collectPageLinks(tab) {
  if (!tab || !tab.id) return;
  let urls = [];
  try {
    const [result] = await chrome.scripting.executeScript({
      target: { tabId: tab.id, allFrames: false },
      func: () => Array.from(document.querySelectorAll('a[href]'))
        .map((a) => a.href)
        .filter((h) => /^https?:|^ftp:/i.test(h)),
    });
    urls = Array.from(new Set((result && result.result) || []));
  } catch (e) {
    notify('海兔下载器', '无法读取该页面链接：' + (e && e.message ? e.message : e));
    return;
  }
  if (urls.length === 0) {
    notify('海兔下载器', '本页没有可下载的链接');
    return;
  }
  const settings = await loadSettings();
  let ok = 0;
  for (const url of urls.slice(0, 50)) {
    try {
      const session = await collectSession(url);
      const payload = buildPayload({ url, referrer: tab.url, session });
      const { status, body } = await postDownload(baseUrl(settings), settings.secret, payload);
      if (mapDownloadResponse(status, body).ok) ok++;
    } catch { /* 单条失败不影响其余 */ }
  }
  notify('海兔下载器', `已发送 ${ok} / ${urls.length} 个链接`);
}

/* ---------------- 自动接管 ---------------- */

chrome.downloads.onCreated.addListener((item) => {
  // 必须异步处理：onCreated 无法返回值拦截，拦截靠 cancel+erase
  handleCreated(item).catch(() => { });
});

async function handleCreated(item) {
  if (!item || !item.url) return;

  // 自己发起的请求不接管
  if (item.byExtensionId && item.byExtensionId === chrome.runtime.id) return;

  const settings = await loadSettings();
  const sessionStart = await getSessionStart();

  // 开关以客户端为准（客户端不可达时退回本地设置）
  const intercept = await resolveAutoCapture(settings);
  if (!shouldCapture(item, { ...settings, autoCapture: intercept.enabled }, sessionStart)) return;

  const key = urlKey(item.url);
  // 用户右键选了「用浏览器下载（不接管）」：一次性放行，既不转发也不取消
  if (await consumeBypass(key)) return;

  // has 与 add 必须在同一个微任务里（中间不能有 await），否则两个并发项会同时通过检查
  if (inFlight.has(key)) { await cancelAndErase(item.id); return; }
  inFlight.add(key);

  try {
    const recent = await recentForwards();
    if (recent[key] && Date.now() - recent[key] < RECENT_WINDOW_MS) {
      await cancelAndErase(item.id);
      return;
    }

    const session = await collectSession(item.url);

    // 明确不支持分段（忽略 Range / 416）就留给浏览器：客户端拿过去也只能单连接跑，
    // 反而丢掉浏览器的续传与断点提示。探测失败（'unknown'）时维持原有接管行为。
    const verdict = await probeRangeSupport(item.url);
    if (verdict === 'no') {
      notify('海兔下载器', probeNotice(verdict));
      return;
    }

    const payload = buildPayload({ url: item.url, referrer: item.referrer, session });
    const { status, body } = await postDownload(baseUrl(settings), settings.secret, payload);
    const mapped = mapDownloadResponse(status, body);

    if (!mapped.ok) {
      // 失败绝不动浏览器下载：宁可让浏览器下，也不能让用户两头落空
      notify('海兔下载器', mapped.message);
      await refreshBadge();
      return;
    }
    await markForwarded(key);
    await cancelAndErase(item.id);
  } catch (e) {
    await handleOffline(e);
  } finally {
    inFlight.delete(key);
  }
}

async function cancelAndErase(id) {
  if (typeof id !== 'number') return;
  try { await chrome.downloads.cancel(id); } catch { /* 可能已结束 */ }
  try { await chrome.downloads.erase({ id }); } catch { /* 忽略 */ }
}

/* ---------------- 状态与通知 ---------------- */

async function handleOffline(e) {
  notify('海兔客户端未运行', '已改用浏览器自带下载。请启动桌面客户端后重试。');
  await setBadge('off', e && e.code === 'unauthorized' ? '密钥错误' : '未连接');
}

async function refreshBadge() {
  const settings = await loadSettings();
  try {
    await ping(baseUrl(settings));
  } catch {
    await setBadge('off', '客户端未运行');
    return { online: false, keyValid: false };
  }
  try {
    await verifyKey(baseUrl(settings), settings.secret);
    await setBadge('on');
    return { online: true, keyValid: true };
  } catch (e) {
    await setBadge('err', e && e.code === 'unauthorized' ? '密钥错误' : '连接异常');
    return { online: true, keyValid: false };
  }
}

async function setBadge(state, title) {
  const map = { on: '', off: 'OFF', err: '!' };
  try {
    await chrome.action.setBadgeText({ text: map[state] || '' });
    await chrome.action.setBadgeBackgroundColor({ color: state === 'err' ? '#C42B1C' : '#8A8A8A' });
    await chrome.action.setTitle({ title: title ? `海兔下载器 · ${title}` : '海兔下载器' });
  } catch { /* 忽略 */ }
}

function notify(title, message) {
  try {
    chrome.notifications.create({
      type: 'basic',
      iconUrl: chrome.runtime.getURL('icons/icon128.png'),
      title,
      message,
    }, () => { void chrome.runtime.lastError; });
  } catch { /* 忽略 */ }
}

/* ---------------- 与 UI 通信 ---------------- */

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  (async () => {
    try {
      switch (msg && msg.type) {
        case 'get-status': {
          const settings = await loadSettings();
          const status = await refreshBadge();
          const intercept = await resolveAutoCapture(settings);
          const forceAttachment = await syncForceAttachment();
          sendResponse({
            ok: true, settings, status, intercept, forceAttachment,
            hostAccess: await hasHostAccess(),
          });
          break;
        }
        case 'save-settings': {
          const saved = await saveSettings(msg.patch || {});
          await invalidateInterceptCache();
          await syncForceAttachment({ force: true });
          const status = await refreshBadge();
          sendResponse({ ok: true, settings: saved, status });
          break;
        }
        case 'test-connection': {
          const s = normalizeSettings(Object.assign(await loadSettings(), msg.settings || {}));
          try { await ping(baseUrl(s)); } catch { sendResponse({ ok: false, code: 'offline' }); break; }
          try {
            await verifyKey(baseUrl(s), s.secret);
            sendResponse({ ok: true, code: 'ok' });
          } catch (e) {
            sendResponse({ ok: false, code: (e && e.code) || 'http' });
          }
          break;
        }
        case 'request-host-access': {
          sendResponse({ ok: await requestHostAccess() });
          break;
        }
        case 'manual-download': {
          const r = await manualSend(msg.url, msg.referrer);
          sendResponse(r);
          break;
        }
        default:
          sendResponse({ ok: false, code: 'unknown' });
      }
    } catch (e) {
      sendResponse({ ok: false, code: 'internal', message: String(e && e.message ? e.message : e) });
    }
  })();
  return true; // 异步响应
});
