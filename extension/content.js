/*
 * 海兔下载器页面内容脚本。
 * 拦截网页中磁力链接（<a href="magnet:...">）的左键点击：
 * 阻止浏览器默认的外部协议唤起（避免系统弹窗打断），把链接经后台
 * （background.js manualSend → /api/download）直接转交海兔客户端，
 * 结果以页面右下角 toast 反馈，不依赖系统通知。
 */
(function () {
  'use strict';

  function sendToBackground(url, referrer) {
    return new Promise((resolve) => {
      try {
        chrome.runtime.sendMessage({ type: 'send-url', url: url, referrer: referrer }, (resp) => {
          if (chrome.runtime.lastError) {
            resolve({ ok: false, message: '扩展后台通信失败，请重试' });
            return;
          }
          resolve(resp || { ok: false, message: '发送失败' });
        });
      } catch (e) {
        // 扩展上下文失效（扩展刚重载而页面未刷新等）
        resolve({ ok: false, message: '扩展未就绪，请刷新页面后重试' });
      }
    });
  }

  // 页面内轻提示：Shadow DOM 隔离样式，避免被站点 CSS 污染
  let toastHost = null;

  function showToast(message, level) {
    if (!toastHost || !toastHost.isConnected) {
      toastHost = document.createElement('div');
      toastHost.setAttribute('data-kokona-toast', '');
      const root = toastHost.attachShadow({ mode: 'open' });
      const style = document.createElement('style');
      style.textContent =
        '.toast{position:fixed;right:20px;bottom:24px;z-index:2147483647;display:flex;align-items:center;gap:8px;' +
        'padding:10px 16px;border-radius:8px;background:#202020;color:#eee;' +
        'font:13px/1.5 "Segoe UI","Microsoft YaHei UI",sans-serif;box-shadow:0 4px 16px rgba(0,0,0,.35);' +
        'opacity:0;transform:translateY(8px);transition:opacity .18s ease,transform .18s ease;max-width:420px}' +
        '.toast.show{opacity:1;transform:translateY(0)}' +
        '.dot{width:8px;height:8px;border-radius:50%;flex:none}' +
        '.ok{background:#2E9E5B}.busy{background:#FACC15}.err{background:#C42B1C}';
      const tip = document.createElement('div');
      tip.className = 'toast';
      const dot = document.createElement('span');
      dot.className = 'dot';
      const text = document.createElement('span');
      tip.appendChild(dot);
      tip.appendChild(text);
      root.appendChild(style);
      root.appendChild(tip);
      (document.body || document.documentElement).appendChild(toastHost);
    }
    const root = toastHost.shadowRoot;
    const tip = root.querySelector('.toast');
    root.querySelector('.dot').className = 'dot ' + level;
    root.querySelector('.toast span:last-child').textContent = message;
    tip.classList.add('show');
    clearTimeout(showToast._timer);
    showToast._timer = setTimeout(() => tip.classList.remove('show'), 2600);
  }

  async function handleClick(ev) {
    const target = ev.target;
    if (!target || !target.closest) return;
    const a = target.closest('a[href]');
    if (!a) return;
    // 用浏览器解析后的 protocol（自动小写）判断，比属性选择器对大小写更稳
    if (a.protocol !== 'magnet:') return;
    ev.preventDefault();
    ev.stopImmediatePropagation();
    showToast('正在发送磁力链接到海兔下载器…', 'busy');
    const r = await sendToBackground(a.href, location.href);
    showToast(r.message || (r.ok ? '已发送到海兔客户端' : '发送失败'), r.ok ? 'ok' : 'err');
  }

  document.addEventListener('click', handleClick, true);
})();
