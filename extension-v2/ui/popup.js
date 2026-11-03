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

async function refresh() {
  const resp = await chrome.runtime.sendMessage({ type: 'get-status' });
  const [state, text] = describe(resp && resp.status);
  setStatus(state, text);
  const iLine = document.getElementById('intercept');
  const it = resp && resp.intercept;
  if (iLine && it) {
    iLine.textContent = it.source === 'client'
      ? (it.enabled ? '自动接管：开启（客户端控制）' : '自动接管：已在客户端关闭')
      : (it.source === 'offline' ? '自动接管：按本地设置（客户端未连接）' : '自动接管：按本地设置');
  }

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
