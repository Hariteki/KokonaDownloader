/** EXT-403 纯逻辑：正则形状、规则形状、生效条件矩阵。 */

import test from 'node:test';
import assert from 'node:assert/strict';

import {
  buildForceAttachmentPattern, buildForceAttachmentRules, buildSingleExtensionPattern,
  decideForceAttachment, describeForceAttachment, FORCE_ATTACHMENT_EXTENSIONS,
  FORCE_ATTACHMENT_RULE_IDS,
} from '../lib/force-attachment.js';

const match = (url, pattern = buildForceAttachmentPattern()) =>
  new RegExp(pattern, 'i').test(url);

test('扩展名正则：命中常见文件，且后缀必须完整', () => {
  assert.equal(match('https://a.com/x.pdf'), true);
  assert.equal(match('https://a.com/x.PDF'), true, '大小写不敏感');
  assert.equal(match('https://a.com/x.pdf?dl=1'), true);
  assert.equal(match('https://a.com/x.pdf#page=2'), true);
  assert.equal(match('https://a.com/d/报告.zip'), true);
  assert.equal(match('http://a.com/x.7z'), true);

  // 不该命中的：后缀不完整、只是路径里带关键词、以及刻意排除的类型
  assert.equal(match('https://a.com/x.pdfx'), false);
  assert.equal(match('https://a.com/pdf/x'), false);
  assert.equal(match('https://a.com/api/download?file=a.pdf'), false, '扩展名在查询串里不算');
  assert.equal(match('https://a.com/x.txt'), false, 'txt 不强制，避免把接口响应变成下载');
  assert.equal(match('https://a.com/x.json'), false);
  assert.equal(match('https://a.com/x.svg'), false);
  assert.equal(match('https://a.com/page.html'), false);
  assert.equal(match('ftp://a.com/x.pdf'), false, 'FTP 不走 DNR（也不是 http/https 导航）');
});

test('单扩展名正则：命中与边界', () => {
  const p = buildSingleExtensionPattern('pdf');
  assert.equal(match('https://a.com/x.pdf', p), true);
  assert.equal(match('https://a.com/x.PDF', p), true);
  assert.equal(match('https://a.com/x.pdf?x=1', p), true);
  assert.equal(match('https://a.com/x.pdfx', p), false);
  assert.equal(match('https://a.com/xXpdf', p), false, '点号必须转义');
});

test('扩展名清单里没有危险项', () => {
  const banned = ['html', 'htm', 'json', 'xml', 'txt', 'svg', 'js', 'css'];
  for (const b of banned) {
    assert.equal(FORCE_ATTACHMENT_EXTENSIONS.includes(b), false, `${b} 不该在强制下载清单里`);
  }
});

test('规则形状：每个扩展名一条 modifyHeaders，只作用于 main_frame', () => {
  const rules = buildForceAttachmentRules(true);
  assert.equal(rules.length, FORCE_ATTACHMENT_EXTENSIONS.length);

  const ids = new Set(rules.map((r) => r.id));
  assert.equal(ids.size, rules.length, '规则 id 不能重复');

  for (const [i, r] of rules.entries()) {
    assert.equal(r.action.type, 'modifyHeaders');
    assert.deepEqual(r.action.responseHeaders, [
      { header: 'content-disposition', operation: 'set', value: 'attachment' },
    ]);
    assert.deepEqual(r.condition.resourceTypes, ['main_frame']);
    assert.equal(r.condition.isUrlFilterCaseSensitive, false);

    // 一条规则只承载一个扩展名：合并成多分支会撞 Chrome 单条 regex 的 2KB 编译内存上限，
    // 超限时规则被**静默跳过**（updateDynamicRules 不报错，规则表里却没有）。
    const re = new RegExp(r.condition.regexFilter, 'i');
    const hit = FORCE_ATTACHMENT_EXTENSIONS.filter((e) => re.test(`https://a.com/x.${e}`));
    assert.deepEqual(hit, [FORCE_ATTACHMENT_EXTENSIONS[i]], '一条规则命中了不止一个扩展名');
    assert.ok(r.condition.regexFilter.length < 80, '单条正则过长，可能超编译内存限制');
  }

  const pdfRule = rules.find((r) => r.condition.regexFilter.includes('\\.pdf'));
  assert.ok(pdfRule, '缺少 pdf 规则');
  assert.equal(new RegExp(pdfRule.condition.regexFilter, 'i').test('https://a.com/x.pdf'), true);
  assert.equal(new RegExp(pdfRule.condition.regexFilter, 'i').test('https://a.com/x.zip'), false);
});

test('规则 id 落在预留区间内，且能一次清干净', () => {
  const rules = buildForceAttachmentRules(true);
  for (const r of rules) assert.ok(FORCE_ATTACHMENT_RULE_IDS.includes(r.id));
  assert.ok(FORCE_ATTACHMENT_RULE_IDS.length >= rules.length);
});

test('规则关闭时返回空数组（由调用方 removeRuleIds 清理）', () => {
  assert.deepEqual(buildForceAttachmentRules(false), []);
});

test('生效条件：开关 / 接管 / 站点权限 三者缺一不可', () => {
  assert.deepEqual(
    decideForceAttachment({ forceAttachment: true, interceptEnabled: true, hostAccess: true }),
    { apply: true, reason: 'on' });

  assert.equal(decideForceAttachment({ forceAttachment: false, interceptEnabled: true, hostAccess: true }).reason, 'disabled');
  assert.equal(decideForceAttachment({ forceAttachment: true, interceptEnabled: false, hostAccess: true }).reason, 'intercept-off');
  assert.equal(decideForceAttachment({ forceAttachment: true, interceptEnabled: true, hostAccess: false }).reason, 'no-host-access');

  // 默认值（字段缺失）必须是不生效
  assert.equal(decideForceAttachment({}).apply, false);
});

test('UI 文案覆盖四种状态', () => {
  assert.match(describeForceAttachment({ apply: true }), /已生效/);
  assert.match(describeForceAttachment({ apply: false, reason: 'disabled' }), /未开启/);
  assert.match(describeForceAttachment({ apply: false, reason: 'intercept-off' }), /自动接管已关闭/);
  assert.match(describeForceAttachment({ apply: false, reason: 'no-host-access' }), /授权访问网站/);
});
