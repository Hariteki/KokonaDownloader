import test from 'node:test';
import assert from 'node:assert/strict';

import {
  isSupportedUrl, urlKey, baseName, normalizeSettings, baseUrl, isOwnApiUrl,
  buildHeaders, buildPayload, mapDownloadResponse, isFreshDownload, shouldCapture,
  classifyRangeProbe, probeNotice,
} from '../lib/logic.js';

test('isSupportedUrl 只接受 http/https/ftp', () => {
  assert.equal(isSupportedUrl('http://a/b.zip'), true);
  assert.equal(isSupportedUrl('https://a/b.zip'), true);
  assert.equal(isSupportedUrl('ftp://a/b.zip'), true);
  assert.equal(isSupportedUrl('file:///c:/a.zip'), false);
  assert.equal(isSupportedUrl('blob:https://a/uuid'), false);
  assert.equal(isSupportedUrl('magnet:?xt=urn:btih:x'), false);
  assert.equal(isSupportedUrl(''), false);
  assert.equal(isSupportedUrl('不是地址'), false);
});

test('urlKey 忽略锚点且稳定', () => {
  assert.equal(urlKey('https://a/b.zip#x'), urlKey('https://a/b.zip#y'));
  assert.notEqual(urlKey('https://a/b.zip'), urlKey('https://a/c.zip'));
  assert.match(urlKey('https://a/b.zip'), /^[0-9a-f]+$/);
});

test('baseName 去掉查询串并解码', () => {
  assert.equal(baseName('/d/%E6%8A%A5%E5%91%8A.pdf?x=1'), '报告.pdf');
  assert.equal(baseName('/d/a.zip#frag'), 'a.zip');
  assert.equal(baseName(''), '');
});

test('normalizeSettings 容忍被粘成 URL 的地址', () => {
  const s = normalizeSettings({ host: 'http://192.168.1.5:16800/api', port: 'x', secret: ' abc ' });
  assert.equal(s.host, '192.168.1.5');
  assert.equal(s.port, 16800);
  assert.equal(s.secret, 'abc');
});

test('normalizeSettings 默认值', () => {
  const s = normalizeSettings(undefined);
  assert.equal(s.host, '127.0.0.1');
  assert.equal(s.port, 16800);
  assert.equal(s.autoCapture, true);
  assert.equal(s.forceAttachment, false);
});

test('baseUrl 与自身接口识别', () => {
  const s = normalizeSettings({ host: '127.0.0.1', port: 16800 });
  assert.equal(baseUrl(s), 'http://127.0.0.1:16800');
  assert.equal(isOwnApiUrl('http://127.0.0.1:16800/api/download', s), true);
  assert.equal(isOwnApiUrl('http://127.0.0.1:16800/file.zip', s), false);
  assert.equal(isOwnApiUrl('http://example.com/api/download', s), false);
});

test('buildHeaders 只保留非空项', () => {
  assert.deepEqual(buildHeaders({}), []);
  assert.deepEqual(buildHeaders({ cookie: ' a=1 ', userAgent: '', acceptLanguage: 'zh-CN' }),
    ['Cookie: a=1', 'Accept-Language: zh-CN']);
});

test('buildPayload 不猜文件名，带 referer 与 headers', () => {
  const p = buildPayload({
    url: 'https://a/b.zip',
    referrer: 'https://a/page',
    session: { cookie: 'sid=1', userAgent: 'UA/1' },
  });
  assert.deepEqual(p.urls, ['https://a/b.zip']);
  assert.equal(p.referer, 'https://a/page');
  assert.equal('filename' in p, false);
  assert.deepEqual(p.headers, ['Cookie: sid=1', 'User-Agent: UA/1']);
});

test('buildPayload 用户显式给文件名才带', () => {
  const p = buildPayload({ url: 'https://a/b', filename: '季度报告.pdf' });
  assert.equal(p.filename, '季度报告.pdf');
  assert.equal('headers' in p, false);
});

test('mapDownloadResponse 覆盖各状态码', () => {
  assert.deepEqual(mapDownloadResponse(200, { gid: 'g1' }), { ok: true, gid: 'g1', duplicate: false, confirm: false });
  assert.equal(mapDownloadResponse(401, null).code, 'unauthorized');
  assert.equal(mapDownloadResponse(409, null).duplicate, true);
  assert.equal(mapDownloadResponse(409, null).ok, true);
  assert.equal(mapDownloadResponse(423, null).code, 'busy');
  assert.equal(mapDownloadResponse(500, null).code, 'server');
  assert.equal(mapDownloadResponse(418, null).code, 'http');
});

test('isFreshDownload 挡住历史回放', () => {
  const sessionStart = Date.parse('2026-11-02T10:00:00Z');
  const fresh = { state: 'in_progress', filename: '', startTime: '2026-11-02T10:05:00Z' };
  assert.equal(isFreshDownload(fresh, sessionStart), true);

  assert.equal(isFreshDownload({ ...fresh, state: 'complete' }, sessionStart), false);
  // filename 已填不代表回放：浏览器可能在 onCreated 前就把建议名解析好了
  assert.equal(isFreshDownload({ ...fresh, filename: 'C:\\x.zip' }, sessionStart), true);
  assert.equal(isFreshDownload({ ...fresh, startTime: '2026-11-02T09:00:00Z' }, sessionStart), false);
});

test('shouldCapture 组合判定', () => {
  const settings = normalizeSettings({});
  const item = { state: 'in_progress', filename: '', url: 'https://a/b.zip', startTime: new Date().toISOString() };
  assert.equal(shouldCapture(item, settings, Date.now() - 60000), true);
  assert.equal(shouldCapture(item, { ...settings, autoCapture: false }, Date.now() - 60000), false);
  assert.equal(shouldCapture({ ...item, url: 'blob:x' }, settings, Date.now() - 60000), false);
  assert.equal(shouldCapture({ ...item, url: 'http://127.0.0.1:16800/api/download' }, settings, Date.now() - 60000), false);
});

test('classifyRangeProbe：206 支持，200/416 不支持，其余未知', () => {
  assert.equal(classifyRangeProbe(206), 'yes');
  assert.equal(classifyRangeProbe(200), 'no');
  assert.equal(classifyRangeProbe(416), 'no');
  assert.equal(classifyRangeProbe(403), 'unknown');
  assert.equal(classifyRangeProbe(404), 'unknown');
  assert.equal(classifyRangeProbe(503), 'unknown');
  assert.equal(classifyRangeProbe(0), 'unknown');
});

test('probeNotice 只对明确不支持给出提示', () => {
  assert.match(probeNotice('no'), /不支持多线程/);
  assert.equal(probeNotice('yes'), '');
  assert.equal(probeNotice('unknown'), '');
});
