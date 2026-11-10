/**
 * 纯逻辑层：不引用任何 chrome.* API，可被 Node 直接单测。
 * 所有对外可见文案保持 ASCII 转义还是原文都行，但**不要**在这里放平台调用。
 */

export const SUPPORTED_SCHEMES = ['http:', 'https:', 'ftp:'];

/** 是否是本扩展能处理的地址。 */
export function isSupportedUrl(url) {
  if (typeof url !== 'string' || url.trim() === '') return false;
  let u;
  try { u = new URL(url.trim()); } catch { return false; }
  return SUPPORTED_SCHEMES.includes(u.protocol);
}

/** 取末段文件名（仅用于"用户显式给了名字"的兜底，正常不从 URL 猜名）。 */
export function baseName(path) {
  if (!path) return '';
  const clean = String(path).split('?')[0].split('#')[0];
  const seg = clean.substring(clean.lastIndexOf('/') + 1);
  try { return decodeURIComponent(seg); } catch { return seg; }
}

/** 持久去重键：去掉锚点后做 djb2 散列，避免明文保存完整地址。 */
export function urlKey(url) {
  let u = String(url || '').trim();
  const hash = u.indexOf('#');
  if (hash >= 0) u = u.substring(0, hash);
  let h = 5381;
  for (let i = 0; i < u.length; i++) h = ((h << 5) + h + u.charCodeAt(i)) >>> 0;
  return h.toString(16);
}

export function defaults() {
  return {
    host: '127.0.0.1',
    port: 16800,
    secret: '',
    autoCapture: true,      // 实际以客户端 /api/settings 为准，这里只是本地兜底
    forceAttachment: false, // M4：DNR 强制附件，默认关
  };
}

/** 归一化用户输入（地址栏可能被粘成 http://host:port/path）。 */
export function normalizeSettings(raw) {
  const d = defaults();
  const src = raw || {};

  let host = String(src.host || '').trim().replace(/^https?:\/\//i, '');
  if (!host) host = d.host;
  const slash = host.indexOf('/');
  if (slash >= 0) host = host.substring(0, slash);
  const colon = host.lastIndexOf(':');
  if (colon > 0) host = host.substring(0, colon);
  if (!host) host = d.host;

  let port = parseInt(src.port, 10);
  if (!(port > 0 && port <= 65535)) port = d.port;

  const secret = String(src.secret || '').trim();
  const autoCapture = src.autoCapture !== false;
  const forceAttachment = src.forceAttachment === true;

  return { host, port, secret, autoCapture, forceAttachment };
}

export function baseUrl(settings) {
  return `http://${settings.host}:${settings.port}`;
}

/** 客户端自身的地址：这些请求绝不能被自己接管。 */
export function isOwnApiUrl(url, settings) {
  if (!url) return false;
  const base = baseUrl(settings);
  return url.startsWith(base + '/api/');
}

/**
 * 组装请求头。只放"能让客户端复现浏览器身份"的三项，
 * 空值不传，避免给客户端塞一个空 Cookie 头。
 */
export function buildHeaders(session) {
  const out = [];
  const s = session || {};
  const cookie = String(s.cookie || '').trim();
  const ua = String(s.userAgent || '').trim();
  const lang = String(s.acceptLanguage || '').trim();
  if (cookie) out.push('Cookie: ' + cookie);
  if (ua) out.push('User-Agent: ' + ua);
  if (lang) out.push('Accept-Language: ' + lang);
  return out;
}

/**
 * 组装 /api/download 的请求体。
 * 约定：filename 只在用户显式指定时传（浏览器在 onCreated 阶段给的名字不可信，
 * 真实名字由客户端按 Content-Disposition 解析）。
 */
export function buildPayload({ url, referrer, filename, session }) {
  const payload = { urls: [url] };
  const ref = String(referrer || '').trim();
  if (ref) payload.referer = ref;
  const name = String(filename || '').trim();
  if (name) payload.filename = name;
  const headers = buildHeaders(session);
  if (headers.length) payload.headers = headers;
  return payload;
}

/** 把 HTTP 状态码/响应体映射成扩展内部错误码（与控制台文案解耦，便于测试）。 */
export function mapDownloadResponse(status, body) {
  const b = body || {};
  if (status === 200 || status === 201) {
    return { ok: true, gid: b.gid || '', duplicate: b.duplicate === true, confirm: b.confirm === true };
  }
  if (status === 401) return { ok: false, code: 'unauthorized', message: '连接密钥错误，请点击工具栏图标重新粘贴密钥' };
  if (status === 409) return { ok: true, duplicate: true, gid: b.gid || '' };
  if (status === 423) return { ok: false, code: 'busy', message: '客户端正忙（并发请求已满），请稍后重试' };
  if (status >= 500) return { ok: false, code: 'server', message: '客户端返回错误 HTTP ' + status };
  return { ok: false, code: 'http', message: '客户端返回 HTTP ' + status };
}

/**
 * 是否属于"本次浏览器会话新发起"的下载。
 * 拦住 Edge 会话恢复/下载历史同步导致的 onCreated 回放：
 *  - 已完成的一定是回放（文件都在磁盘上了）
 *  - 开始时间早于本扩展启动时刻：历史项
 *
 * 刻意**不**用 item.filename 判断：浏览器可能在 onCreated 时就已经把建议文件名填好，
 * 若据此拒绝，正常下载会被全部误杀（旧版扩展就有这条，现象是"点了没反应"）。
 */
export function isFreshDownload(item, sessionStartMs) {
  if (!item) return false;
  if (item.state === 'complete') return false;
  if (item.startTime) {
    const started = Date.parse(item.startTime);
    if (!Number.isNaN(started) && sessionStartMs && started < sessionStartMs - 1000) return false;
  }
  return true;
}

/** 综合判定是否接管这个下载项（"是不是自己发起的"由调用方用 runtime.id 判断）。 */
export function shouldCapture(item, settings, sessionStartMs) {
  if (!item || !settings) return false;
  if (!settings.autoCapture) return false;
  if (!isSupportedUrl(item.url)) return false;
  if (isOwnApiUrl(item.url, settings)) return false;
  return isFreshDownload(item, sessionStartMs);
}

/**
 * 把"Range 探测"的 HTTP 状态码翻译成能力判定：
 *  - 206：真的兑现区间请求 → 支持多线程分段，值得接管
 *  - 200：忽略 Range 直接发整份（含"声明了 Accept-Ranges 但不兑现"的撒谎服务器）→ 不支持
 *  - 416：拒绝区间 → 不支持
 *  - 其它（401/403/404/5xx/网络错误）：无法判断，交回调用方按原逻辑处理
 */
export function classifyRangeProbe(status) {
  if (status === 206) return 'yes';
  if (status === 200 || status === 416) return 'no';
  return 'unknown';
}

/** 探测结论是否值得提示用户（明确不支持才提示，避免打扰）。 */
export function probeNotice(verdict) {
  return verdict === 'no' ? '该链接不支持多线程分段，已交给浏览器下载' : '';
}
