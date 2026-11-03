const dot = document.getElementById('dot');
const statusText = document.getElementById('status');
const secretInput = document.getElementById('secret');
const saveBtn = document.getElementById('save');
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
  if (resp && resp.settings && !secretInput.value) secretInput.value = resp.settings.secret || '';
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

saveBtn.addEventListener('click', async () => {
  saveBtn.disabled = true;
  const secret = secretInput.value.trim();
  const test = await chrome.runtime.sendMessage({ type: 'test-connection', settings: { secret } });
  if (test && test.ok) {
    await chrome.runtime.sendMessage({ type: 'save-settings', patch: { secret } });
    setStatus('on', '已连接');
  } else if (test && test.code === 'unauthorized') {
    setStatus('err', '密钥错误');
  } else if (test && test.code === 'offline') {
    // 离线也保存：客户端启动后即可自动连上
    await chrome.runtime.sendMessage({ type: 'save-settings', patch: { secret } });
    setStatus('off', '客户端未运行（密钥已保存）');
  } else {
    setStatus('err', '连接失败：' + ((test && test.code) || '未知'));
  }
  saveBtn.disabled = false;
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
