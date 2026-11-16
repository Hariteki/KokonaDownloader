const dot = document.getElementById('dot');
const statusText = document.getElementById('status');
const pasteBtn = document.getElementById('paste');
const pairHint = document.getElementById('pairHint');
const grantBtn = document.getElementById('grant');
const grantHint = document.getElementById('grantHint');
const optionsLink = document.getElementById('options');

function setStatus(state, text) {
  dot.className = 'dot' + (state === 'on' ? ' on' : state === 'err' ? ' err' : '');
  statusText.textContent = text;
}

function describe(status) {
  if (!status) return ['', '未知状态'];
  if (!status.online) return ['off', '客户端未运行'];
  if (!status.keyValid) return ['err', '密钥错误，请重新粘贴'];
  return ['on', '已连接'];
}

const captureToggle = document.getElementById('captureToggle');
const captureState = document.getElementById('captureState');

function setCaptureUI(enabled, source) {
  captureToggle.classList.toggle('off', !enabled);
  captureToggle.setAttribute('aria-checked', String(!!enabled));
  const sub = captureToggle.querySelector('.sub');
  sub.textContent = enabled ? '关掉后浏览器下载不再交给海兔' : '当前已暂停：浏览器下载不再交给海兔';
  let text;
  if (source === 'client') text = enabled ? '接管中（以客户端为准）' : '已关闭（以客户端为准）';
  else if (source === 'offline') text = enabled ? '按本地设置（客户端未连接）' : '已关闭（客户端未连接，上线后自动同步）';
  else if (source === 'pending') text = enabled ? '已开启，等客户端上线后同步' : '已关闭，等客户端上线后同步';
  else text = enabled ? '接管中' : '已关闭';
  captureState.textContent = text;
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
  const resp = await chrome.runtime.sendMessage({ type: 'get-status' });
  const [state, text] = describe(resp && resp.status);
  setStatus(state, text);
  const it = resp && resp.intercept;
  setCaptureUI(!!(it && it.enabled), it && it.source);

  grantHint.textContent = resp && resp.hostAccess
    ? '已授权：下载会带上登录 Cookie'
    : '未授权：只下公开文件，需要登录的站点会失败';
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
  pairHint.textContent = '正在读取剪贴板…';
  const secret = await readClipboardSecret();

  if (secret === null) {
    setStatus('err', '无法读取剪贴板');
    pairHint.textContent = '浏览器拒绝了剪贴板访问，请重新加载扩展后再试';
    pasteBtn.disabled = false;
    return;
  }
  if (!secret) {
    setStatus('err', '剪贴板没有内容');
    pairHint.textContent = '先在客户端复制密钥，再点这个按钮';
    pasteBtn.disabled = false;
    return;
  }

  // 先验证再保存：密钥错了就不写进 storage，避免把"错的"当成"已配对"
  const test = await chrome.runtime.sendMessage({ type: 'test-connection', settings: { secret } });
  if (test && test.ok) {
    await chrome.runtime.sendMessage({ type: 'save-settings', patch: { secret } });
    setStatus('on', '已连接');
    pairHint.textContent = '密钥已保存，连接正常';
  } else if (test && test.code === 'unauthorized') {
    setStatus('err', '密钥错误');
    pairHint.textContent = '剪贴板里的不是有效密钥，请到客户端重新复制';
  } else if (test && test.code === 'offline') {
    // 客户端没运行时也保存：等它启动就自动连上
    await chrome.runtime.sendMessage({ type: 'save-settings', patch: { secret } });
    setStatus('off', '客户端未运行（密钥已保存）');
    pairHint.textContent = '密钥已保存，客户端启动后会自动连上';
  } else {
    setStatus('err', '连接失败：' + ((test && test.code) || '未知'));
    pairHint.textContent = '';
  }
  pasteBtn.disabled = false;
});

grantBtn.addEventListener('click', async () => {
  const r = await chrome.runtime.sendMessage({ type: 'request-host-access' });
  grantHint.textContent = r && r.ok ? '已授权：下载会带上登录 Cookie' : '未授权：需要登录的站点会失败';
});

optionsLink.addEventListener('click', (e) => {
  e.preventDefault();
  chrome.runtime.openOptionsPage();
});

refresh().catch(() => setStatus('err', '无法读取状态'));
