/**
 * 最小 chrome.* 模拟：只为让 sw/background.js 能在 Node 里跑真实代码路径。
 * 刻意不模拟"行为"，只提供状态与事件注册；断言由测试自己做。
 */

function makeEvent() {
  const listeners = [];
  return {
    addListener(fn) { listeners.push(fn); },
    removeListener(fn) {
      const i = listeners.indexOf(fn);
      if (i >= 0) listeners.splice(i, 1);
    },
    __fire(...args) { return listeners.map((fn) => fn(...args)); },
    __count() { return listeners.length; },
  };
}

function makeStorageArea(initial) {
  const data = Object.assign({}, initial || {});
  return {
    data,
    async get(key) {
      if (typeof key === 'string') {
        return key in data ? { [key]: data[key] } : {};
      }
      return Object.assign({}, data);
    },
    async set(obj) { Object.assign(data, obj); },
    async remove(key) { delete data[key]; },
    async clear() { for (const k of Object.keys(data)) delete data[k]; },
  };
}

export function makeChrome(options = {}) {
  const calls = [];
  const record = (name, args) => calls.push({ name, args });
  /** 模拟浏览器的动态规则表：updateDynamicRules 会真的增删，便于断言"最终态"。 */
  const dynamicRules = [];
  let downloadSeq = 100;

  const chrome = {
    runtime: {
      id: options.id || 'test-extension-id',
      lastError: undefined,
      getURL: (p) => 'chrome-extension://' + (options.id || 'test-extension-id') + '/' + p,
      onInstalled: makeEvent(),
      onStartup: makeEvent(),
      onMessage: makeEvent(),
      openOptionsPage: () => record('openOptionsPage', []),
    },
    storage: {
      local: makeStorageArea(options.local),
      session: makeStorageArea(options.session),
    },
    contextMenus: {
      removeAll: async () => record('menus.removeAll', []),
      create: (opts) => record('menus.create', [opts]),
      onClicked: makeEvent(),
    },
    downloads: {
      onCreated: makeEvent(),
      download: async (opts) => { record('downloads.download', [opts]); return ++downloadSeq; },
      cancel: async (id) => record('downloads.cancel', [id]),
      erase: async (q) => record('downloads.erase', [q]),
    },
    action: {
      setBadgeText: async (o) => record('action.setBadgeText', [o]),
      setBadgeBackgroundColor: async (o) => record('action.setBadgeBackgroundColor', [o]),
      setTitle: async (o) => record('action.setTitle', [o]),
    },
    notifications: {
      create: (opts, cb) => { record('notifications.create', [opts]); if (cb) cb(); },
    },
    permissions: {
      contains: async () => options.hostAccess === true,
      request: async () => options.grantHost === true,
      onAdded: makeEvent(),
      onRemoved: makeEvent(),
    },
    declarativeNetRequest: {
      updateDynamicRules: async (opts) => {
        record('dnr.updateDynamicRules', [opts]);
        const removeIds = new Set((opts && opts.removeRuleIds) || []);
        for (let i = dynamicRules.length - 1; i >= 0; i--) {
          if (removeIds.has(dynamicRules[i].id)) dynamicRules.splice(i, 1);
        }
        dynamicRules.push(...((opts && opts.addRules) || []));
      },
      getDynamicRules: async () => dynamicRules.slice(),
    },
    cookies: {
      getAll: async (q) => {
        record('cookies.getAll', [q]);
        return options.cookies || [];
      },
    },
    scripting: {
      executeScript: async (opts) => {
        record('scripting.executeScript', [opts]);
        return options.scriptResult || [{ result: [] }];
      },
    },
    tabs: { create: (o) => record('tabs.create', [o]) },
  };

  chrome.__calls = calls;
  chrome.__reset = () => { calls.length = 0; };
  chrome.__callsOf = (name) => calls.filter((c) => c.name === name);
  chrome.__dnrRules = () => dynamicRules.slice();
  return chrome;
}
