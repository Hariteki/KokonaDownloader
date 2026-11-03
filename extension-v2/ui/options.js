import { describeForceAttachment } from '../lib/force-attachment.js';

const hostInput = document.getElementById('host');
const portInput = document.getElementById('port');
const pasteBtn = document.getElementById('paste');
const secretState = document.getElementById('secretState');
const autoCaptureInput = document.getElementById('autoCapture');
const forceAttachmentInput = document.getElementById('forceAttachment');
const saveBtn = document.getElementById('save');
const hint = document.getElementById('hint');

async function load() {
  const resp = await chrome.runtime.sendMessage({ type: 'get-status' });
  const s = (resp && resp.settings) || {};
  hostInput.value = s.host || '127.0.0.1';
  portInput.value = s.port || 16800;
  autoCaptureInput.checked = s.autoCapture !== false;
  forceAttachmentInput.checked = s.forceAttachment === true;

  // 密钥以"是否已配对"呈现：不再回显明文，改完就只能靠"粘贴密钥并连接"重新覆盖
  const st = resp && resp.status;
  if (s.secret && st && st.online && !st.keyValid) {
    secretState.textContent = '已保存密钥，但客户端拒绝了（可能已轮换，请重新粘贴）';
    secretState.style.color = '#e05a4f';
  } else if (s.secret && st && st.online && st.keyValid) {
    secretState.textContent = '已配对，连接正常';
    secretState.style.color = '#7fbf8f';
  } else if (s.secret) {
    secretState.textContent = '已保存密钥（客户端未运行）';
    secretState.style.color = '#d8a657';
  } else {
    secretState.textContent = '尚未配对';
    secretState.style.color = '#d8a657';
  }

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

/**
 * 读剪贴板里的密钥。客户端复制的是一串 32 位十六进制，这里顺手去掉所有空白
 * （从聊天工具/文档里复制常带换行和空格）。读不到返回 null，与"空剪切板"区分开。
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
  secretState.textContent = '正在读取剪贴板…';
  secretState.style.color = '#8f8f8f';
  const secret = await readClipboardSecret();

  if (secret === null) {
    secretState.textContent = '浏览器拒绝了剪贴板访问，请重新加载扩展后再试';
    secretState.style.color = '#e05a4f';
    pasteBtn.disabled = false;
    return;
  }
  if (!secret) {
    secretState.textContent = '剪贴板没有内容：请先在客户端复制密钥';
    secretState.style.color = '#e05a4f';
    pasteBtn.disabled = false;
    return;
  }

  // 地址、端口与密钥一起提交：用户可能刚改了端口，单独存密钥会用旧地址去验证
  const conn = {
    host: hostInput.value.trim() || '127.0.0.1',
    port: parseInt(portInput.value, 10) || 16800,
    secret,
  };
  // 先验证再落盘：错的密钥不该被当成"已配对"
  const test = await chrome.runtime.sendMessage({ type: 'test-connection', settings: conn });
  if (test && test.ok) {
    await chrome.runtime.sendMessage({ type: 'save-settings', patch: conn });
    hint.textContent = '已保存，连接正常';
    hint.style.color = '#7fbf8f';
  } else if (test && test.code === 'unauthorized') {
    secretState.textContent = '密钥无效：请到客户端重新复制';
    secretState.style.color = '#e05a4f';
    pasteBtn.disabled = false;
    return;
  } else if (test && test.code === 'offline') {
    // 客户端没运行时也保存：等它启动就自动连上
    await chrome.runtime.sendMessage({ type: 'save-settings', patch: conn });
  } else {
    secretState.textContent = '连接失败：' + ((test && test.code) || '未知');
    secretState.style.color = '#e05a4f';
    pasteBtn.disabled = false;
    return;
  }

  await load().catch(() => { });
  pasteBtn.disabled = false;
});

load().catch(() => { hint.textContent = '读取设置失败'; });
