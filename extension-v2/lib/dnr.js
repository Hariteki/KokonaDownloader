/**
 * DNR 规则的下发与同步（唯一碰 chrome.declarativeNetRequest 的地方）。
 * 判定逻辑在 lib/force-attachment.js（纯函数）。
 */

import { loadSettings } from './settings.js';
import { resolveAutoCapture } from './intercept.js';
import { hasHostAccess } from './cookies.js';
import {
  buildForceAttachmentRules, decideForceAttachment, FORCE_ATTACHMENT_RULE_IDS,
} from './force-attachment.js';

/** 上一次成功下发的状态。同一 SW 生命周期内避免重复调用 DNR（弹窗每开一次都会问 get-status）。 */
let lastApplied = null;

/** 只在测试里用：重置记忆，避免用例之间串味。 */
export function __resetForceAttachmentCache() {
  lastApplied = null;
}

/**
 * 按当前设置同步 DNR 规则。
 * @param {{force?: boolean}} opts force=true 时忽略记忆，强制下发（设置变更、权限变更后调用）
 * @returns {Promise<{apply: boolean, reason: string, applied: boolean, error?: string}>}
 */
export async function syncForceAttachment(opts = {}) {
  const settings = await loadSettings();
  const intercept = await resolveAutoCapture(settings);
  const hostAccess = await hasHostAccess();

  const decision = decideForceAttachment({
    forceAttachment: settings.forceAttachment,
    interceptEnabled: intercept.enabled,
    hostAccess,
  });

  const want = decision.apply;
  if (!opts.force && lastApplied === want) return { ...decision, applied: want };

  try {
    if (!chrome.declarativeNetRequest || !chrome.declarativeNetRequest.updateDynamicRules) {
      throw new Error('declarativeNetRequest 不可用');
    }
    await chrome.declarativeNetRequest.updateDynamicRules({
      removeRuleIds: FORCE_ATTACHMENT_RULE_IDS,
      addRules: buildForceAttachmentRules(want),
    });
    lastApplied = want;
    return { ...decision, applied: want };
  } catch (e) {
    // 下发失败不能影响主流程：下载接管本身与 DNR 无关
    return { ...decision, applied: false, error: String((e && e.message) || e) };
  }
}
