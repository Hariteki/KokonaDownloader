/* 弹出窗口脚本：粘贴密钥立即测试连接，红绿灯显示连接状态，快速发送链接。 */
(function () {
  'use strict';

  const dot = document.getElementById('dot');
  const statusText = document.getElementById('statusText');
  const statusDetail = document.getElementById('statusDetail');
  const setupSection = document.getElementById('setupSection');
  const keyInput = document.getElementById('keyInput');
  const connectBtn = document.getElementById('connectBtn');
  const connectedRow = document.getElementById('connectedRow');
  const modifyBtn = document.getElementById('modifyBtn');
  const urlInput = document.getElementById('urlInput');
  const sendBtn = document.getElementById('sendBtn');
  const msg = document.getElementById('msg');

  let lastSettings = null;
  let connecting = false;

  function setDot(cls) { dot.className = 'dot ' + cls; }

  function showMsg(text, ok) {
    msg.textContent = text;
    msg.className = 'msg ' + (ok ? 'ok' : 'err');
  }

  function renderConnected(settings, version) {
    setDot('on');
    statusText.textContent = '已连接';
    statusDetail.textContent = settings.host + ':' + settings.port + (version ? ' · v' + version : '');
    setupSection.classList.add('hidden');
    connectedRow.classList.remove('hidden');
  }

  function renderDisconnected(settings, code) {
    setDot('off');
    statusText.textContent = '未连接';
    if (!settings.secret) {
      statusDetail.textContent = '请先粘贴连接密钥';
    } else if (code === 'unauthorized') {
      statusDetail.textContent = '密钥错误，请核对后重新粘贴';
    } else {
      statusDetail.textContent = '无法连接客户端（' + settings.host + ':' + settings.port + '），请确认桌面客户端已启动';
    }
    setupSection.classList.remove('hidden');
    connectedRow.classList.add('hidden');
  }

  function render(status, settings) {
    lastSettings = settings;
    if (status.online && status.keyValid) {
      renderConnected(settings, status.version);
    } else {
      renderDisconnected(settings, status.lastCode);
    }
  }

  function connect(secret) {
    if (connecting) return;
    const value = (secret || keyInput.value || '').trim();
    if (!value) { showMsg('请先粘贴连接密钥', false); return; }
    connecting = true;
    connectBtn.disabled = true;
    connectBtn.textContent = '正在连接…';
    setDot('check');
    statusText.textContent = '正在测试连接…';
    statusDetail.textContent = lastSettings ? lastSettings.host + ':' + lastSettings.port : '';
    showMsg('', true);
    chrome.runtime.sendMessage({ type: 'test-key', settings: { secret: value } }, (resp) => {
      connecting = false;
      connectBtn.disabled = false;
      connectBtn.textContent = '粘贴并连接';
      if (chrome.runtime.lastError || !resp) {
        setDot('off');
        statusText.textContent = '未连接';
        statusDetail.textContent = '扩展后台通信失败，请重试';
        return;
      }
      if (resp.ok) {
        lastSettings = resp.settings;
        renderConnected(resp.settings, resp.version);
        showMsg('连接成功，密钥已保存', true);
      } else if (resp.code === 'unauthorized') {
        setDot('off');
        statusText.textContent = '未连接';
        statusDetail.textContent = '密钥错误，请核对后重新粘贴';
        showMsg('密钥被客户端拒绝，未保存', false);
      } else {
        lastSettings = resp.settings || lastSettings;
        renderDisconnected(lastSettings, resp.code);
        showMsg('无法连接客户端，请确认桌面客户端已启动', false);
      }
    });
  }

  // 粘贴密钥后立即自动测试连接
  keyInput.addEventListener('paste', () => {
    setTimeout(() => { if (keyInput.value.trim()) connect(); }, 0);
  });
  keyInput.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') connect();
  });
  // 「粘贴并连接」：点击时自动读取剪贴板密钥填入，再立即测试连接；
  // 剪贴板读不到时退回输入框内容
  connectBtn.addEventListener('click', async () => {
    let text = '';
    try { text = (await navigator.clipboard.readText() || '').trim(); } catch (e) { /* 无权限/无焦点时读取失败 */ }
    text = text.split(/\r?\n/).map((s) => s.trim()).filter(Boolean)[0] || '';
    // 剪贴板里是链接等明显不是密钥的内容时忽略，避免误填
    if (/^(https?|ftp):/i.test(text)) text = '';
    if (text) keyInput.value = text;
    connect(text || undefined);
  });

  modifyBtn.addEventListener('click', () => {
    keyInput.value = '';
    connectedRow.classList.add('hidden');
    setupSection.classList.remove('hidden');
    keyInput.focus();
    showMsg('', true);
  });

  sendBtn.addEventListener('click', () => {
    const url = urlInput.value.trim();
    if (!url) { showMsg('请先粘贴下载链接', false); return; }
    if (!KokonaLogic.isSupportedUrl(url)) {
      showMsg('仅支持 http/https/ftp 链接', false);
      return;
    }
    sendBtn.disabled = true;
    chrome.runtime.sendMessage({ type: 'send-url', url: url }, (resp) => {
      sendBtn.disabled = false;
      if (chrome.runtime.lastError) { showMsg('后台通信失败，请重试', false); return; }
      if (resp && resp.ok) {
        showMsg('已发送到海兔客户端', true);
        urlInput.value = '';
      } else {
        showMsg((resp && resp.message) || '发送失败', false);
      }
    });
  });

  urlInput.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') sendBtn.click();
  });

  document.getElementById('openOptions').addEventListener('click', (e) => {
    e.preventDefault();
    chrome.runtime.openOptionsPage();
  });

  document.getElementById('helpLink').addEventListener('click', (e) => {
    e.preventDefault();
    showMsg('开启「自动接管」后浏览器下载会自动转给客户端；也可右键链接手动发送。', true);
  });

  // 初始状态
  chrome.runtime.sendMessage({ type: 'get-status' }, (resp) => {
    if (chrome.runtime.lastError || !resp) {
      setDot('off');
      statusText.textContent = '扩展后台未就绪，请重试';
      return;
    }
    render(resp.status, resp.settings);
  });
})();
