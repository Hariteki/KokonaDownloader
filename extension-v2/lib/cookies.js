/**
 * 会话采集：Cookie / User-Agent / Accept-Language。
 * Cookie 需要对应站点的 host 权限（我们把它做成可选权限），
 * 没授权时静默降级为空——下载仍能进行，只是不带登录态。
 */

const OPTIONAL_ORIGINS = ['http://*/*', 'https://*/*'];

/** 当前是否已获得任意站点访问权限。 */
export async function hasHostAccess() {
  try {
    return await chrome.permissions.contains({ origins: OPTIONAL_ORIGINS });
  } catch {
    return false;
  }
}

/** 申请站点访问权限（需在用户手势里调用，例如 popup 的按钮点击）。 */
export async function requestHostAccess() {
  try {
    return await chrome.permissions.request({ origins: OPTIONAL_ORIGINS });
  } catch {
    return false;
  }
}

async function readCookieHeader(url) {
  try {
    const list = await chrome.cookies.getAll({ url });
    if (!Array.isArray(list) || list.length === 0) return '';
    return list.map((c) => `${c.name}=${c.value}`).join('; ');
  } catch {
    return ''; // 无权限或该 URL 不可读：降级
  }
}

/**
 * 采集一次请求需要的会话信息。
 * @param {string} url 目标下载地址
 * @returns {Promise<{cookie: string, userAgent: string, acceptLanguage: string}>}
 */
export async function collectSession(url) {
  const nav = (typeof globalThis !== 'undefined' && globalThis.navigator) || {};
  const session = {
    cookie: '',
    userAgent: nav.userAgent || '',
    acceptLanguage: nav.language || '',
  };
  if (url && /^https?:/i.test(url)) session.cookie = await readCookieHeader(url);
  return session;
}
