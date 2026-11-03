/**
 * 真浏览器端到端（Edge）：
 *  1) 先做环境自检——裸 Edge 能否下载文件；不能则判定"环境不支持"退出码 2
 *  2) 自检通过后，加载 extension-v2，访问测试页触发下载，断言扩展把任务转给了假客户端
 *
 * 用法：node tools/ext_e2e.mjs [--ext=<目录>] [--keep]
 * 退出码：0 通过；1 失败；2 环境不支持（无法在本机验证）
 */

import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import fs from 'node:fs';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const extArg = process.argv.find((a) => a.startsWith('--ext='));
const EXT_DIR = extArg ? path.resolve(extArg.slice(6)) : path.resolve(__dirname, '..', 'extension-v2');
const keep = process.argv.includes('--keep');

const CANDIDATES = [
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
];
const PORT = 16800;
const FILE_SIZE = 4 * 1024 * 1024;

const payload = Buffer.alloc(FILE_SIZE);
for (let i = 0; i < FILE_SIZE; i++) payload[i] = (i * 31 + 7) & 0xff;

const requests = [];

function startStub() {
  const server = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://127.0.0.1');
    let body = '';
    req.on('data', (c) => { body += c; });
    req.on('end', () => {
      if (url.pathname === '/api/download') {
        requests.push({ headers: req.headers, body });
        res.writeHead(200, { 'Content-Type': 'application/json' });
        return res.end(JSON.stringify({ ok: true, gid: 'e2e-gid' }));
      }
      if (url.pathname === '/api/ping') {
        res.writeHead(200, { 'Content-Type': 'application/json' });
        return res.end(JSON.stringify({ ok: true, version: 'e2e' }));
      }
      if (url.pathname === '/api/stats' || url.pathname === '/api/settings') {
        res.writeHead(200, { 'Content-Type': 'application/json' });
        return res.end(JSON.stringify({ ok: true, interceptBrowserDownloads: true }));
      }
      if (url.pathname === '/page') {
        res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
        return res.end(`<!doctype html><meta charset="utf-8"><title>e2e</title>
<a id="dl" href="/file.bin" download>download</a>
<script>setTimeout(function(){ document.getElementById('dl').click(); }, 800);</script>`);
      }
      if (url.pathname === '/file.bin') {
        res.writeHead(200, {
          'Content-Type': 'application/octet-stream',
          'Content-Disposition': 'attachment; filename="e2e-file.bin"',
          'Content-Length': String(payload.length),
        });
        let sent = 0;
        const step = 64 * 1024;
        const timer = setInterval(() => {
          if (sent >= payload.length) { clearInterval(timer); res.end(); return; }
          res.write(payload.subarray(sent, sent + step));
          sent += step;
        }, 25);
        req.on('close', () => clearInterval(timer));
        return;
      }
      res.writeHead(404);
      res.end('not found');
    });
  });
  return new Promise((resolve) => server.listen(PORT, '127.0.0.1', () => resolve(server)));
}

function newProfile(downloadDir) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'kokona-e2e-'));
  if (downloadDir) {
    fs.mkdirSync(downloadDir, { recursive: true });
    fs.mkdirSync(path.join(dir, 'Default'), { recursive: true });
    fs.writeFileSync(path.join(dir, 'Default', 'Preferences'), JSON.stringify({
      download: { default_directory: downloadDir, prompt_for_download: false, directory_upgrade: true },
      savefile: { default_directory: downloadDir },
    }));
  }
  return dir;
}

function edgeArgs(profile, extra) {
  return [
    `--user-data-dir=${profile}`,
    '--no-first-run',
    '--no-default-browser-check',
    '--disable-features=msEdgeFirstRunExperience,msImplicitSignin,DisableLoadExtensionCommandLineSwitch,InsecureDownloadWarnings',
    '--disable-sync',
    '--no-sandbox',
    ...extra,
  ];
}

const wait = (ms) => new Promise((r) => setTimeout(r, ms));

function waitFor(predicate, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  return new Promise((resolve) => {
    const tick = () => {
      if (predicate()) return resolve(true);
      if (Date.now() > deadline) return resolve(false);
      setTimeout(tick, 200);
    };
    tick();
  });
}

/** 裸 Edge 能不能下载？不能说明这台机器/这个环境屏蔽了浏览器下载。 */
async function browserDownloadsWork(exe) {
  // 先用全新的 profile 目录，再写 Preferences 指定下载目录（顺序反了会被 Edge 覆盖）
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'kokona-e2e-'));
  const dlDir = path.join(profile, 'dl');
  fs.mkdirSync(dlDir, { recursive: true });
  fs.mkdirSync(path.join(profile, 'Default'), { recursive: true });
  fs.writeFileSync(path.join(profile, 'Default', 'Preferences'), JSON.stringify({
    download: { default_directory: dlDir, prompt_for_download: false, directory_upgrade: true },
    savefile: { default_directory: dlDir },
  }));
  const proc = spawn(exe, edgeArgs(profile, [`http://127.0.0.1:${PORT}/page`]), { stdio: 'ignore' });
  const ok = await waitFor(() => {
    try { return fs.readdirSync(dlDir).some((f) => f.startsWith('e2e-file')); } catch { return false; }
  }, 15000);
  proc.kill();
  await wait(300);
  try { fs.rmSync(profile, { recursive: true, force: true }); } catch { }
  return ok;
}

const exe = CANDIDATES.find((p) => fs.existsSync(p));
if (!exe) {
  console.error('[e2e] 找不到 Edge，跳过');
  process.exit(2);
}

const server = await startStub();

const canDownload = await browserDownloadsWork(exe);
if (!canDownload) {
  console.error('[e2e] 环境自检失败：这台机器上的 Edge 无法下载文件（策略或沙箱限制）。');
  console.error('[e2e] 该用例在本环境不可执行；请在有下载能力的机器上跑，或按文档的人工清单验证。');
  server.close();
  process.exit(2);
}
console.log('[e2e] 环境自检通过：浏览器可以下载');

const profile = newProfile(null);
const proc = spawn(exe, edgeArgs(profile, [
  `--load-extension=${EXT_DIR}`,
  `--disable-extensions-except=${EXT_DIR}`,
  `http://127.0.0.1:${PORT}/page`,
]), { stdio: 'ignore' });

let ok = false;
try {
  ok = await waitFor(() => requests.length > 0, 35000);
  if (!ok) {
    console.error('[e2e] 失败：35 秒内没有收到 /api/download');
  } else {
    const req = requests[0];
    const body = JSON.parse(req.body);
    console.log('[e2e] 收到转发:', JSON.stringify(body, null, 2));
    const problems = [];
    if (!Array.isArray(body.urls) || !body.urls.some((u) => u.endsWith('/file.bin'))) problems.push('urls 不含目标地址');
    if (!Array.isArray(body.headers) || !body.headers.some((h) => /^User-Agent: /i.test(h))) problems.push('缺少 User-Agent');
    if (req.headers['x-kokona-secret'] === undefined) problems.push('缺少密钥头');
    if (problems.length) { console.error('[e2e] 断言失败:', problems.join('；')); ok = false; }
    else console.log('[e2e] 通过：真实浏览器 → 扩展 → 客户端链路可用');
  }
} finally {
  if (!keep) {
    proc.kill();
    await wait(500);
    try { fs.rmSync(profile, { recursive: true, force: true }); } catch { }
    server.close();
  }
}

process.exit(ok ? 0 : 1);
