/**
 * "拦截开关以客户端为准"。
 *
 * 客户端设置里已有 interceptBrowserDownloads，扩展自己又存了一份 autoCapture，
 * 两份会打架（客户端关了、扩展照拦）。这里统一：以客户端返回值为准，
 * 客户端不可达时退回本地设置，并做短缓存避免每次下载都多打一次 HTTP。
 *
 * 「一键开关接管」：扩展 popup 的开关直接写客户端（POST /api/settings）；
 * 客户端不在线时先落到本地 autoCapture 并记一份 pending，等客户端上线后自动追平，
 * 保证"关了就真的不接、开了就接"，不会因为客户端恰好没开而点了没反应。
 */

import { getSettings, patchSettings } from './api.js';
import { baseUrl } from './logic.js';
import { saveSettings } from './settings.js';

const CACHE_KEY = 'interceptCache';
const PENDING_KEY = 'pendingIntercept';
const TTL_OK = 60 * 1000;      // 客户端可达：60 秒内复用
const TTL_OFFLINE = 15 * 1000; // 客户端不可达：15 秒内不要反复重试

async function readCache() {
  try {
    const obj = await chrome.storage.session.get(CACHE_KEY);
    const c = obj && obj[CACHE_KEY];
    if (!c || typeof c.at !== 'number') return null;
    return Date.now() - c.at < c.ttl ? c.value : null;
  } catch {
    return null;
  }
}

async function writeCache(value, ttl) {
  try {
    await chrome.storage.session.set({ [CACHE_KEY]: { value, at: Date.now(), ttl } });
  } catch { /* 忽略 */ }
}

/** 设置变更后调用：下一次判定重新问客户端。 */
export async function invalidateInterceptCache() {
  try { await chrome.storage.session.remove(CACHE_KEY); } catch { /* 忽略 */ }
}

/* ---------------- pending：离线时点的开关，等客户端上线后追平 ---------------- */

export async function getPendingIntercept() {
  try {
    const obj = await chrome.storage.session.get(PENDING_KEY);
    const p = obj && obj[PENDING_KEY];
    return typeof p === 'boolean' ? p : null;
  } catch {
    return null;
  }
}

export async function setPendingIntercept(value) {
  try { await chrome.storage.session.set({ [PENDING_KEY]: value }); } catch { /* 忽略 */ }
}

export async function clearPendingIntercept() {
  try { await chrome.storage.session.remove(PENDING_KEY); } catch { /* 忽略 */ }
}

/**
 * 解析"当前是否应该自动接管"。
 * 优先级：pending（离线时刚点过、最贴近用户意图）> 客户端 > 本地兜底。
 * @param {object} settings 本地设置（含 autoCapture 兜底值）
 * @returns {Promise<{enabled: boolean, source: 'client'|'local'|'offline'|'pending'}>}
 */
export async function resolveAutoCapture(settings) {
  const pending = await getPendingIntercept();
  if (pending !== null) return { enabled: pending, source: 'pending' };

  const cached = await readCache();
  if (cached) return cached;

  try {
    const remote = await getSettings(baseUrl(settings), settings.secret);
    const flag = remote && remote.interceptBrowserDownloads;
    if (typeof flag === 'boolean') {
      const value = { enabled: flag, source: 'client' };
      await writeCache(value, TTL_OK);
      return value;
    }
    // 客户端没给这个字段：退回本地
    const value = { enabled: settings.autoCapture, source: 'local' };
    await writeCache(value, TTL_OK);
    return value;
  } catch {
    const value = { enabled: settings.autoCapture, source: 'offline' };
    await writeCache(value, TTL_OFFLINE);
    return value;
  }
}

/**
 * 一键切换接管。
 * 在线：写客户端（source=client，立即生效）+ 清 pending + 刷缓存。
 * 离线：写本地 autoCapture 兜底（source=local）+ 记 pending，等上线后追平。
 * @param {object} settings 本地设置
 * @param {boolean} enabled 目标状态
 * @returns {Promise<{enabled: boolean, source: 'client'|'local'}>}
 */
export async function writeIntercept(settings, enabled) {
  try {
    await patchSettings(baseUrl(settings), settings.secret, { interceptBrowserDownloads: enabled });
    await invalidateInterceptCache();
    await clearPendingIntercept();
    await writeCache({ enabled, source: 'client' }, TTL_OK);
    return { enabled, source: 'client' };
  } catch {
    await saveSettings({ autoCapture: enabled });
    await setPendingIntercept(enabled);
    await writeCache({ enabled, source: 'local' }, TTL_OFFLINE);
    return { enabled, source: 'local' };
  }
}

/**
 * 客户端上线后追平离线时点的开关。无 pending 或仍离线时原样保留，不报错。
 * @param {object} settings 本地设置
 * @returns {Promise<{reconciled: boolean}>}
 */
export async function reconcileIntercept(settings) {
  const pending = await getPendingIntercept();
  if (pending === null) return { reconciled: false };
  try {
    await patchSettings(baseUrl(settings), settings.secret, { interceptBrowserDownloads: pending });
    await invalidateInterceptCache();
    await clearPendingIntercept();
    await writeCache({ enabled: pending, source: 'client' }, TTL_OK);
    return { reconciled: true };
  } catch {
    return { reconciled: false }; // 还离线：保留 pending，下次再试
  }
}
