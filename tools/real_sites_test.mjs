/**
 * 真实站点下载测试：覆盖不同类型的下载服务器。
 *
 * 先用 Range: bytes=0-1023 探一次，把每个站点归类（重定向 / 长度 / Range / Content-Disposition /
 * HTTP 版本）；再用客户端 API 真下，校验字节数与哈希。
 *
 * 用法：
 *   node tools/real_sites_test.mjs --probe     # 只探测，不下载
 *   node tools/real_sites_test.mjs             # 探测 + 真下
 *   node tools/real_sites_test.mjs --only=github,cloudflare
 */

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import dns from 'node:dns';

// 本机到部分国内站点的 IPv6 路由不通，而 Node 默认优先 IPv6 → 探测会"假不可达"。
// 客户端（.NET）有自己的地址轮换逻辑，这里只影响本脚本的探测与参照下载。
dns.setDefaultResultOrder('ipv4first');

const arg = (name, dflt) => {
  const hit = process.argv.find((a) => a.startsWith(name + '='));
  return hit ? hit.slice(name.length + 1) : dflt;
};

const API = `http://127.0.0.1:${arg('--port', '16800')}`;
const DIR = path.resolve(arg('--dir', 'C:/Users/Nahida/AppData/Local/Temp/kokona-real'));
const ONLY = arg('--only', '') ? arg('--only', '').split(',') : null;
const PROBE_ONLY = process.argv.includes('--probe');

const SECRET = JSON.parse(fs.readFileSync(
  path.join(process.env.APPDATA, 'KokonaDownloader', 'settings.json'), 'utf8')).ApiSecret;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const MB = (n) => (n / 1024 / 1024).toFixed(2);

/**
 * 站点清单。刻意覆盖不同"下载服务器类型"：
 *  - 带 Content-Length 的静态直链（CDN / 镜像）
 *  - 302 到签名 URL（GitHub Release / S3）
 *  - chunked、没有 Content-Length
 *  - 文件名只在 Content-Disposition 里（URL 无扩展名）
 *  - 纯 HTTP（非 TLS）
 *  - FTP
 *  - 中文文件名（RFC5987）
 */
const SITES = [
  {
    id: 'cloudflare',
    name: 'Cloudflare 测速端点（CDN 直链）',
    url: 'https://speed.cloudflare.com/__down?bytes=6000000',
    verify: 'size',      // 内容每次随机生成，没法比哈希
    note: '不声明 Accept-Ranges，会退回单连接',
  },
  {
    id: 'ovh',
    name: 'OVH 公共测速文件（静态 10MB）',
    url: 'https://proof.ovh.net/files/10Mb.dat',
    verify: 'hash',
  },
  {
    id: 'plainhttp',
    name: 'nginx 源码包（纯 HTTP，非 TLS）',
    url: 'http://nginx.org/download/nginx-1.26.0.tar.gz',
    verify: 'hash',
  },
  {
    id: 'github',
    name: 'GitHub Release（302 到签名 CDN）',
    url: 'https://github.com/cli/cli/releases/download/v2.62.0/gh_2.62.0_windows_386.msi',
    verify: 'hash',
  },
  {
    id: 'chunked',
    name: 'httpbingo /stream（chunked，无长度）',
    url: 'https://httpbingo.org/stream/5',
    verify: 'size',
  },
  {
    id: 'disposition',
    name: 'Content-Disposition 决定文件名（URL 无扩展名）',
    url: 'https://httpbingo.org/response-headers?Content-Disposition=attachment%3B%20filename%3D%22quarterly-report.pdf%22',
    verify: 'hash',
    expectName: 'quarterly-report.pdf',
  },
  {
    id: 'chinese',
    name: '中文文件名（RFC5987 filename*）',
    url: 'https://httpbingo.org/response-headers?Content-Disposition=attachment%3B%20filename%2A%3DUTF-8%27%27%25E5%25AD%25A3%25E5%25BA%25A6%25E6%258A%25A5%25E5%2591%258A.pdf',
    verify: 'hash',
    expectName: '季度报告.pdf',
  },
  {
    id: 'tiny',
    name: 'raw.githubusercontent（极小文件）',
    url: 'https://raw.githubusercontent.com/git/git/master/README.md',
    verify: 'hash',
  },
  {
    id: 'tuna',
    name: '清华镜像（拒绝浏览器 UA → 触发回退）',
    url: 'https://mirrors.tuna.tsinghua.edu.cn/ubuntu/dists/noble/Release',
    verify: 'hash',
    browserUa: true,
    note: '浏览器 UA 会 403，靠"去掉自定义 UA 重试"救回来',
  },
  {
    id: 'ftp',
    name: 'FTP（非 HTTP 协议）',
    url: 'ftp://ftp.gnu.org/gnu/gzip/gzip-1.13.tar.gz',
    verify: 'hash',
    ftp: true,
  },
];

/* ---------------- 探测 ---------------- */

const CHROME_UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36';

async function probe(url, attempt = 1, ua = CHROME_UA) {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), 30000);
  try {
    const res = await fetch(url, {
      headers: { Range: 'bytes=0-1023', 'User-Agent': ua },
      redirect: 'follow',
      signal: ctrl.signal,
    });
    const h = res.headers;
    const body = Buffer.from(await res.arrayBuffer());
    const cr = h.get('content-range') || '';
    const total = cr.includes('/') ? Number(cr.split('/')[1]) : Number(h.get('content-length') || 0);
    return {
      ok: res.ok,
      status: res.status,
      redirected: res.redirected,
      finalHost: new URL(res.url).host,
      length: total || 0,
      acceptRanges: !!h.get('accept-ranges'),
      disposition: h.get('content-disposition') || '',
      contentType: h.get('content-type') || '',
      got: body.length,
    };
  } catch (e) {
    // 公网站点偶发抖动：给一次重试，否则会把"网络抖了一下"记成"站点不可用"
    if (attempt < 2) { clearTimeout(timer); return probe(url, attempt + 1, ua); }
    return { ok: false, error: String(e && e.message || e) };
  } finally {
    clearTimeout(timer);
  }
}

/* ---------------- 客户端 API ---------------- */

async function api(method, pathname, body) {
  const res = await fetch(API + pathname, {
    method,
    headers: { 'X-Kokona-Secret': SECRET, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  return { status: res.status, json: text ? JSON.parse(text) : null };
}

async function waitTask(gid, timeoutMs = 180000) {
  const t0 = Date.now();
  let last = null;
  while (Date.now() - t0 < timeoutMs) {
    await sleep(300);
    const list = await api('GET', '/api/tasks');
    const t = (list.json || []).find((x) => x.gid === gid);
    if (!t) return { state: 'vanished', task: last };
    last = t;
    if (t.state === 'completed' || t.state === 'failed') {
      await sleep(400);
      const again = await api('GET', '/api/tasks');
      const t2 = (again.json || []).find((x) => x.gid === gid);
      return t2 || { state: t.state, task: t };
    }
  }
  return { state: 'timeout', task: last };
}

/** 用 Node 直接下一份作为参照（只对静态资源有意义）。 */
async function referenceHash(url, limit = 12 * 1024 * 1024) {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), 60000);
  try {
    const res = await fetch(url, { headers: { 'User-Agent': 'curl/8.9.1' }, signal: ctrl.signal });
    if (!res.ok) return null;
    const buf = Buffer.from(await res.arrayBuffer());
    if (buf.length > limit) return null;
    return { size: buf.length, sha: crypto.createHash('sha256').update(buf).digest('hex') };
  } catch {
    return null;
  } finally {
    clearTimeout(timer);
  }
}

function sha256File(p) {
  return new Promise((resolve, reject) => {
    const h = crypto.createHash('sha256');
    fs.createReadStream(p).on('data', (c) => h.update(c))
      .on('end', () => resolve(h.digest('hex')))
      .on('error', reject);
  });
}

/* ---------------- 主流程 ---------------- */

fs.mkdirSync(DIR, { recursive: true });
const sites = SITES.filter((s) => !ONLY || ONLY.includes(s.id));

console.log(`[real] 探测 ${sites.length} 个站点…\n`);
for (const s of sites) {
  s.probe = s.ftp ? { ok: true, note: 'FTP，探测跳过' } : await probe(s.url, 1, s.browserUa ? CHROME_UA : 'Mozilla/5.0');
}

const phead = ['站点', '类型', '状态', '长度', 'Range', 'C-D', '最终主机'];
const prows = sites.map((s) => {
  const p = s.probe;
  if (s.ftp) return [s.id, 'FTP', '-', '-', 'REST', '-', new URL(s.url).host];
  const type = !p.ok ? `拒绝(${p.status})`
    : p.redirected ? '302→CDN'
    : p.acceptRanges ? '静态+Range'
    : p.status === 206 ? 'Range(未声明)'
    : p.length ? '静态无Range' : 'chunked';
  return [
    s.id,
    p.ok ? type : '不可达',
    p.ok ? String(p.status) : '-',
    p.ok ? (p.length ? MB(p.length) + 'MB' : '未知') : '-',
    p.ok ? (p.acceptRanges ? 'Y' : 'N') : '-',
    p.ok ? (p.disposition ? (p.disposition.includes('filename*') ? 'RFC5987' : 'filename') : '-') : '-',
    p.ok ? p.finalHost : JSON.stringify(p).slice(0, 46),
  ];
});
printTable(phead, prows);

if (PROBE_ONLY) process.exit(0);

/* ---------------- 真下 ---------------- */

console.log('\n[real] 开始下载…\n');
const rows = [];

for (const s of sites) {
  // 只有"连请求都没发出去"才跳过；拿到状态码（哪怕 403）说明站点可达，照常下。
  if (!s.probe.ok && !s.probe.status) {
    rows.push([s.id, '跳过', '-', '-', '-', s.probe.error || '网络不可达']);
    continue;
  }

  const t0 = Date.now();
  const add = await api('POST', '/api/download', {
    urls: [s.url], dir: DIR, connections: s.ftp ? 1 : 8,
    ...(s.browserUa ? { headers: ['User-Agent: ' + CHROME_UA, 'Accept-Language: zh-CN'] } : {}),
  });
  if (add.status !== 200) {
    rows.push([s.id, '添加失败', '-', '-', '-', `HTTP ${add.status}`]);
    continue;
  }
  const gid = add.json.gid;
  const r = await waitTask(gid);
  const dt = ((Date.now() - t0) / 1000).toFixed(1);

  let verdict = r.state;
  let detail = '';
  let speed = '-';

  if (r.state === 'completed') {
    const file = r.filePath;
    const size = fs.existsSync(file) ? fs.statSync(file).size : -1;
    speed = size > 0 ? `${MB(size)}MB/${dt}s` : '-';

    if (s.verify === 'hash') {
      const ref = await referenceHash(s.url);
      if (ref) {
        const got = await sha256File(file);
        verdict = got === ref.sha && size === ref.size ? 'OK' : 'HASH-MISMATCH';
        detail = got === ref.sha ? '' : `${size}B vs ${ref.size}B`;
      } else {
        verdict = size > 0 ? 'OK(size)' : 'EMPTY';
      }
    } else {
      const expect = s.probe.length;
      verdict = expect > 0 && size === expect ? 'OK' : (size > 0 ? 'OK(size)' : 'EMPTY');
      detail = expect > 0 && size !== expect ? `${size} vs ${expect}` : '';
    }

    if (s.expectName && path.basename(file) !== s.expectName) {
      verdict = 'NAME-MISMATCH';
      detail = `期望 ${s.expectName}，实际 ${path.basename(file)}`;
    }
  } else if (r.state === 'failed') {
    detail = r.errorMessage || '';
  }

  rows.push([s.id, verdict, `${dt}s`, speed, path.basename(r.filePath || ''), detail.slice(0, 60)]);

  if (gid) await api('POST', `/api/tasks/${gid}/remove`, {}).catch(() => { });
  if (r.filePath && fs.existsSync(r.filePath)) fs.rmSync(r.filePath, { force: true });
  await sleep(500);
}

console.log('');
printTable(['站点', '结果', '用时', '体积/耗时', '落盘名', '备注'], rows);

const bad = rows.filter((r) => !String(r[1]).startsWith('OK') && r[1] !== '跳过');
console.log('');
console.log(bad.length === 0
  ? `[real] 全部通过（${rows.length} 个站点）`
  : `[real] ${bad.length} 个站点有问题：${bad.map((r) => r[0] + '=' + r[1]).join(', ')}`);

/* 打印用 */
function strWidth(s) {
  let w = 0;
  for (const ch of s) w += /[\u4e00-\u9fa5]/.test(ch) ? 2 : 1;
  return w;
}
// 用函数声明而不是 const：printTable 在本文件前半段就会被调用，const 会落进 TDZ
function pad(s, w) {
  return s + ' '.repeat(Math.max(0, w - strWidth(s)));
}
function printTable(head, rows) {
  const width = head.map((h, i) => Math.max(strWidth(h), ...rows.map((r) => strWidth(String(r[i] ?? '')))));
  console.log('| ' + head.map((h, i) => pad(h, width[i])).join(' | ') + ' |');
  console.log('|' + width.map((w) => '-'.repeat(w + 2)).join('|') + '|');
  for (const r of rows) console.log('| ' + r.map((c, i) => pad(String(c ?? ''), width[i])).join(' | ') + ' |');
}
