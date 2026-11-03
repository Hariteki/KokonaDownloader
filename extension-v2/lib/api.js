/** 与控制台通信的薄封装：只管超时、密钥头、错误归类，不做业务判断。 */

export const TIMEOUT_PING = 1500;
export const TIMEOUT_KEY = 2500;
export const TIMEOUT_DOWNLOAD = 5000;

class ApiError extends Error {
  constructor(code, message, status) {
    super(message);
    this.code = code;
    this.status = status;
  }
}

async function withTimeout(url, options, ms) {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), ms);
  try {
    return await fetch(url, Object.assign({}, options, { signal: ctrl.signal }));
  } finally {
    clearTimeout(timer);
  }
}

function offline() {
  return new ApiError('offline', '无法连接到海兔客户端');
}

/** 探活（免鉴权）。返回版本号字符串。 */
export async function ping(base) {
  let resp;
  try {
    resp = await withTimeout(base + '/api/ping', { method: 'GET' }, TIMEOUT_PING);
  } catch {
    throw offline();
  }
  if (!resp.ok) throw new ApiError('http', 'HTTP ' + resp.status, resp.status);
  let body;
  try { body = await resp.json(); } catch { throw new ApiError('bad-body', '意外的 ping 响应'); }
  if (!body || body.ok !== true) throw new ApiError('bad-body', '意外的 ping 响应');
  return String(body.version || '');
}

/** 校验密钥：/api/stats 需要正确密钥，401 即密钥错。 */
export async function verifyKey(base, secret) {
  let resp;
  try {
    resp = await withTimeout(base + '/api/stats', {
      method: 'GET',
      headers: { 'X-Kokona-Secret': secret || '' },
    }, TIMEOUT_KEY);
  } catch {
    throw offline();
  }
  if (resp.status === 401) throw new ApiError('unauthorized', '连接密钥错误', 401);
  if (!resp.ok) throw new ApiError('http', '客户端返回 HTTP ' + resp.status, resp.status);
  return true;
}

/** 读取客户端设置（用于"以客户端为准"的拦截开关）。 */
export async function getSettings(base, secret) {
  let resp;
  try {
    resp = await withTimeout(base + '/api/settings', {
      method: 'GET',
      headers: { 'X-Kokona-Secret': secret || '' },
    }, TIMEOUT_KEY);
  } catch {
    throw offline();
  }
  if (!resp.ok) throw new ApiError('http', 'HTTP ' + resp.status, resp.status);
  try { return await resp.json(); } catch { return null; }
}

/** 提交下载。返回 { status, body }，由 logic.mapDownloadResponse 统一判定。 */
export async function postDownload(base, secret, payload) {
  let resp;
  try {
    resp = await withTimeout(base + '/api/download', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-Kokona-Secret': secret || '',
      },
      body: JSON.stringify(payload),
    }, TIMEOUT_DOWNLOAD);
  } catch {
    throw offline();
  }
  let body = null;
  try { body = await resp.json(); } catch { /* 允许空响应体 */ }
  return { status: resp.status, body };
}
