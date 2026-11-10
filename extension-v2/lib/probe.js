/**
 * Range 能力嗅探：判断目标站点是否真的支持分段下载。
 *
 * 只发一个 `Range: bytes=0-1` 的 GET，读到响应头就立刻取消，不下载正文。
 *
 * 注意：fetch 不允许手工设置 Cookie / User-Agent（禁用头，会被静默丢掉），
 * 所以带登录态只能靠 `credentials: 'include'`——有站点权限时浏览器会自己带上 Cookie。
 * 扩展在 Service Worker 里 fetch 跨域需要 host 权限：没授权时 fetch 会失败，
 * 此时返回 'unknown'（维持原有接管行为，不误伤）。
 */

import { classifyRangeProbe } from './logic.js';

/**
 * @returns {'yes'|'no'|'unknown'} yes=支持分段；no=明确不支持；unknown=探测失败/无法判断
 */
export async function probeRangeSupport(url, timeoutMs = 3000) {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    const res = await fetch(url, {
      method: 'GET',
      headers: { Range: 'bytes=0-1' },
      credentials: 'include',
      redirect: 'follow',
      cache: 'no-store',
      signal: ctrl.signal,
    });
    const verdict = classifyRangeProbe(res.status);
    // 只关心响应头，正文立刻丢弃，避免真的把文件拉下来
    try { await res.body?.cancel(); } catch { /* 已结束 */ }
    return verdict;
  } catch {
    return 'unknown';
  } finally {
    clearTimeout(timer);
  }
}
