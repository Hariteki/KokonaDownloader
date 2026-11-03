import { describeForceAttachment } from '../lib/force-attachment.js';

const hostInput = document.getElementById('host');
const portInput = document.getElementById('port');
const secretInput = document.getElementById('secret');
const autoCaptureInput = document.getElementById('autoCapture');
const forceAttachmentInput = document.getElementById('forceAttachment');
const saveBtn = document.getElementById('save');
const hint = document.getElementById('hint');

async function load() {
  const resp = await chrome.runtime.sendMessage({ type: 'get-status' });
  const s = (resp && resp.settings) || {};
  hostInput.value = s.host || '127.0.0.1';
  portInput.value = s.port || 16800;
  secretInput.value = s.secret || '';
  autoCaptureInput.checked = s.autoCapture !== false;
  forceAttachmentInput.checked = s.forceAttachment === true;

  const hint = document.getElementById('interceptHint');
  const it = resp && resp.intercept;
  if (hint && it) {
    if (it.source === 'client') {
      hint.textContent = it.enabled
        ? '当前由客户端控制：客户端已开启自动拦截'
        : '当前由客户端控制：客户端已关闭自动拦截（此项不生效）';
      hint.style.color = it.enabled ? '#7fbf8f' : '#d8a657';
    } else if (it.source === 'offline') {
      hint.textContent = '客户端未连接：暂时按本地设置执行';
      hint.style.color = '#d8a657';
    }
  }

  const forceHint = document.getElementById('forceHint');
  const fa = resp && resp.forceAttachment;
  if (forceHint && fa) {
    forceHint.textContent = describeForceAttachment(fa)
      + (fa.error ? `（浏览器拒绝：${fa.error}）` : '');
    forceHint.style.color = fa.apply ? '#7fbf8f' : '#8f8f8f';
  }
}

saveBtn.addEventListener('click', async () => {
  saveBtn.disabled = true;
  hint.textContent = '';
  const patch = {
    host: hostInput.value.trim() || '127.0.0.1',
    port: parseInt(portInput.value, 10) || 16800,
    secret: secretInput.value.trim(),
    autoCapture: autoCaptureInput.checked,
    forceAttachment: forceAttachmentInput.checked,
  };
  const r = await chrome.runtime.sendMessage({ type: 'save-settings', patch });
  const ok = r && r.status && r.status.online && r.status.keyValid;
  hint.textContent = ok ? '已保存，连接正常' : '已保存（当前未能连接到客户端）';
  hint.style.color = ok ? '#7fbf8f' : '#d8a657';
  saveBtn.disabled = false;
  // 重读一次：规则是否真的下发成功，只有后台知道
  await load().catch(() => { });
});

load().catch(() => { hint.textContent = '读取设置失败'; });
