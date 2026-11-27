const dot = document.getElementById('dot');
const pasteBtn = document.getElementById('paste');
const grantBtn = document.getElementById('grant');
const optionsLink = document.getElementById('options');

/** 弹窗不再显示小字说明：状态只落在圆点颜色（title 里保留文字，悬停可见）。 */
function setStatus(state, text) {
  dot.className = 'dot' + (state === 'on' ? ' on' : state === 'err' ? ' err' : '');
  dot.title = text || '';
}

function describe(status) {
  if (!status) return ['', '未知状态'];
  if (!status.online) return ['off', '客户端未运行'];
  if (!status.keyValid) return ['err', '密钥错误，请重新粘贴'];
  return ['on', '已连接'];
}

const captureToggle = document.getElementById('captureToggle');
const siteBtn = document.getElementById('siteBypass');

/** 当前标签页地址。「本网站不接管」靠它取站点；浏览器内部页面取不到就禁用按钮。 */
let tabUrl = '';

async function getActiveTabUrl() {
  try {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    return (tab && tab.url) || '';
  } catch {
    return '';
  }
}

function applySiteUI(site, bypassed) {
  siteBtn.classList.toggle('bypassed', !!(site && bypassed));
  if (!site) {
    siteBtn.disabled = true;
    siteBtn.textContent = '本网站不接管下载';
    siteBtn.title = '当前页面不支持（浏览器内部页面）';
    return;
  }
  siteBtn.disabled = false;
  siteBtn.textContent = `${bypassed ? '恢复接管本网站' : '本网站不接管下载'} · ${site}`;
  siteBtn.title = bypassed
    ? `${site} 已整站不接管（含子域），点击恢复`
    : `点击后 ${site} 全部页面都不接管`;
}

async function refreshSite() {
  tabUrl = await getActiveTabUrl();
  const r = await chrome.runtime.sendMessage({ type: 'get-site-state', url: tabUrl });
  applySiteUI(r && r.ok ? r.site : '', !!(r && r.bypassed));
}

siteBtn.addEventListener('click', async () => {
  if (siteBtn.disabled) return;
  const next = !siteBtn.classList.contains('bypassed');
  siteBtn.disabled = true;
  const r = await chrome.runtime.sendMessage({ type: 'set-site-bypass', url: tabUrl, bypassed: next });
  siteBtn.disabled = false;
  if (r && r.ok) applySiteUI(r.site, r.bypassed);
  else applySiteUI('', false);
});

function setCaptureUI(enabled, source) {
  captureToggle.classList.toggle('off', !enabled);
  captureToggle.setAttribute('aria-checked', String(!!enabled));
  let text;
  if (source === 'client') text = enabled ? '接管中（以客户端为准）' : '已关闭（以客户端为准）';
  else if (source === 'offline') text = enabled ? '按本地设置（客户端未连接）' : '已关闭（客户端未连接，上线后自动同步）';
  else if (source === 'pending') text = enabled ? '已开启，等客户端上线后同步' : '已关闭，等客户端上线后同步';
  else text = enabled ? '接管中' : '已关闭';
  captureToggle.title = text;
}

let toggling = false;

async function doToggle() {
  if (toggling) return;
  toggling = true;
  // 目标状态 = 当前是否处于"关"：开着点一下→关(false)，关着点一下→开(true)
  const next = captureToggle.classList.contains('off');
  captureToggle.style.opacity = '0.6';
  const r = await chrome.runtime.sendMessage({ type: 'toggle-intercept', enabled: next });
  captureToggle.style.opacity = '';
  if (r && r.ok) {
    setCaptureUI(r.enabled, r.source);
    const [state, text] = describe(r.status);
    setStatus(state, text);
  }
  toggling = false;
}

captureToggle.addEventListener('click', doToggle);
captureToggle.addEventListener('keydown', (e) => {
  if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); doToggle(); }
});

async function refresh() {
  await refreshSite().catch(() => applySiteUI('', false));
  const resp = await chrome.runtime.sendMessage({ type: 'get-status' });
  const [state, text] = describe(resp && resp.status);
  setStatus(state, text);
  const it = resp && resp.intercept;
  setCaptureUI(!!(it && it.enabled), it && it.source);
  grantBtn.title = resp && resp.hostAccess
    ? '已授权：下载会带上登录 Cookie'
    : '未授权：只下公开文件，需要登录的站点会失败';
}

/** 小字提示已移除，操作结果临时写在按钮文字上，过几秒自动还原。 */
const PASTE_LABEL = '粘贴密钥并连接';
let pasteTimer = 0;

function flashPaste(text, ms = 4000) {
  clearTimeout(pasteTimer);
  pasteBtn.textContent = text;
  pasteTimer = setTimeout(() => { pasteBtn.textContent = PASTE_LABEL; }, ms);
}

/**
 * 读剪贴板里的密钥。客户端复制的是一串 32 位十六进制，这里顺手去掉所有空白
 * （从聊天工具/文档里复制常带换行和空格）。
 * 读不到（未授权剪贴板 / 页面没焦点）时返回 null，调用方要能区分"读失败"和"空的"。
 */
async function readClipboardSecret() {
  try {
    const text = await navigator.clipboard.readText();
    return (text || '').replace(/\s+/g, '');
  } catch {
    return null;
  }
}

pasteBtn.addEventListener('click', async () => {
  pasteBtn.disabled = true;
  clearTimeout(pasteTimer);
  pasteBtn.textContent = '正在读取剪贴板…';
  const secret = await readClipboardSecret();

  if (secret === null) {
    setStatus('err', '无法读取剪贴板');
    flashPaste('无法读取剪贴板');
    pasteBtn.disabled = false;
    return;
  }
  if (!secret) {
    setStatus('err', '剪贴板没有内容');
    flashPaste('剪贴板没有密钥');
    pasteBtn.disabled = false;
    return;
  }

  // 先验证再保存：密钥错了就不写进 storage，避免把"错的"当成"已配对"
  const test = await chrome.runtime.sendMessage({ type: 'test-connection', settings: { secret } });
  if (test && test.ok) {
    await chrome.runtime.sendMessage({ type: 'save-settings', patch: { secret } });
    setStatus('on', '已连接');
    flashPaste('已连接');
  } else if (test && test.code === 'unauthorized') {
    setStatus('err', '密钥错误');
    flashPaste('密钥错误，请重新复制');
  } else if (test && test.code === 'offline') {
    // 客户端没运行时也保存：等它启动就自动连上
    await chrome.runtime.sendMessage({ type: 'save-settings', patch: { secret } });
    setStatus('off', '客户端未运行（密钥已保存）');
    flashPaste('密钥已保存（客户端未运行）');
  } else {
    setStatus('err', '连接失败：' + ((test && test.code) || '未知'));
    flashPaste('连接失败，请重试');
  }
  pasteBtn.disabled = false;
});

grantBtn.addEventListener('click', async () => {
  const r = await chrome.runtime.sendMessage({ type: 'request-host-access' });
  grantBtn.title = r && r.ok ? '已授权：下载会带上登录 Cookie' : '未授权：需要登录的站点会失败';
  if (!(r && r.ok)) {
    const original = grantBtn.textContent;
    grantBtn.textContent = '未授权 · 点击重试';
    setTimeout(() => { grantBtn.textContent = original; }, 3000);
  }
});

optionsLink.addEventListener('click', (e) => {
  e.preventDefault();
  chrome.runtime.openOptionsPage();
});

refresh().catch(() => setStatus('err', '无法读取状态'));
