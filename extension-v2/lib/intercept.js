/**
 * "拦截开关以客户端为准"。
 *
 * 客户端设置里已有 interceptBrowserDownloads，扩展自己又存了一份 autoCapture，
 * 两份会打架（客户端关了、扩展照拦）。这里统一：以客户端返回值为准，
 * 客户端不可达时退回本地设置，并做短缓存避免每次下载都多打一次 HTTP。
 */

import { getSettings } from './api.js';
import { baseUrl } from './logic.js';

const CACHE_KEY = 'interceptCache';
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

/**
 * 解析"当前是否应该自动接管"。
 * @param {object} settings 本地设置（含 autoCapture 兜底值）
 * @returns {Promise<{enabled: boolean, source: 'client'|'local'|'offline'}>}
 */
export async function resolveAutoCapture(settings) {
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
