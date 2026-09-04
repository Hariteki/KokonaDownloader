/* 设置页脚本：读写客户端地址与端口（连接密钥与自动接管开关在工具栏弹窗中设置）。 */
(function () {
  'use strict';

  const hostInput = document.getElementById('host');
  const portInput = document.getElementById('port');
  const saveBtn = document.getElementById('saveBtn');
  const result = document.getElementById('result');

  function show(text, ok) {
    result.textContent = text;
    result.className = 'result ' + (ok ? 'ok' : 'err');
  }

  function collect() {
    return {
      host: hostInput.value,
      port: portInput.value
    };
  }

  // 载入已保存设置（密钥与接管开关不在本页展示，在弹窗中管理）
  chrome.runtime.sendMessage({ type: 'get-status' }, (resp) => {
    if (chrome.runtime.lastError || !resp || !resp.settings) return;
    const s = resp.settings;
    hostInput.value = s.host;
    portInput.value = s.port;
  });

  saveBtn.addEventListener('click', () => {
    saveBtn.disabled = true;
    chrome.runtime.sendMessage({ type: 'save-settings', settings: collect() }, (resp) => {
      saveBtn.disabled = false;
      if (chrome.runtime.lastError || !resp) {
        show('保存失败：扩展后台通信异常', false);
        return;
      }
      const st = resp.status || {};
      if (st.online && st.keyValid) {
        show('已保存，客户端连接正常（v' + (st.version || '?') + '）', true);
      } else if (st.online) {
        show('已保存，客户端在线但密钥未通过，请点击工具栏图标粘贴密钥。', false);
      } else {
        show('已保存，但当前无法连接到客户端。请确认桌面客户端已启动、地址端口正确。', false);
      }
    });
  });
})();
