/** 设置的读写与会话起点。唯一使用 chrome.storage 的地方（logic.js 保持纯函数）。 */

import { normalizeSettings, defaults } from './logic.js';

const KEY_SETTINGS = 'settings';
const KEY_SESSION_START = 'sessionStartMs';
const KEY_RECENT = 'recentForwards';

export async function loadSettings() {
  const obj = await chrome.storage.local.get(KEY_SETTINGS);
  return normalizeSettings(obj && obj[KEY_SETTINGS]);
}

export async function saveSettings(patch) {
  const current = await loadSettings();
  const next = normalizeSettings(Object.assign({}, current, patch || {}));
  await chrome.storage.local.set({ [KEY_SETTINGS]: next });
  return next;
}

/**
 * 本次浏览器会话的起点。放在 storage.session（随浏览器会话结束清空、SW 重启不丢），
 * 用来挡住 Edge 会话恢复时对历史下载项的 onCreated 回放。
 */
export async function getSessionStart() {
  const obj = await chrome.storage.session.get(KEY_SESSION_START);
  if (obj && obj[KEY_SESSION_START]) return obj[KEY_SESSION_START];
  const now = Date.now();
  await chrome.storage.session.set({ [KEY_SESSION_START]: now });
  return now;
}

/** 最近转发记录（SW 重启后仍能挡重复项），写入时顺带清理 5 分钟前的老记录。 */
export async function recentForwards() {
  const obj = await chrome.storage.session.get(KEY_RECENT);
  return (obj && obj[KEY_RECENT]) || {};
}

export async function markForwarded(key) {
  const now = Date.now();
  const map = await recentForwards();
  const next = {};
  for (const [k, at] of Object.entries(map)) {
    if (now - at < 5 * 60 * 1000) next[k] = at;
  }
  next[key] = now;
  await chrome.storage.session.set({ [KEY_RECENT]: next });
}

export function defaultsForUi() {
  return defaults();
}
