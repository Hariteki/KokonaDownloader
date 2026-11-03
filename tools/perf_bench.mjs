/**
 * 下载性能专项：连接数 → 吞吐 / CPU / 内存。
 *
 * 场地：本地 HTTP 服务对**每条连接**限速 4 MB/s，所以"多连接"的收益可以被干净地量出来：
 *   1 连接 ≈ 4 MB/s，8 连接 ≈ 32 MB/s。
 * 服务端也统计"同时在传的请求数"峰值——这是客户端到底开了几条连接的直接证据
 * （客户端 API 不返回连接数，只看任务列表会少一层验证）。
 *
 * 用法：node tools/perf_bench.mjs [--size=64] [--rate=4] [--cases=1,2,4,8,16]
 */

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

const arg = (name, dflt) => {
  const hit = process.argv.find((a) => a.startsWith(name + '='));
  return hit ? hit.slice(name.length + 1) : dflt;
};

const API = `http://127.0.0.1:${arg('--port', '16800')}`;
const SIZE_MB = Number(arg('--size', '64'));
const RATE_MB = Number(arg('--rate', '4'));
const CASES = arg('--cases', '1,2,4,8,16').split(',').map(Number);
const DIR = path.resolve(arg('--dir', 'D:/kokonaDownloader/artifacts/perf'));

const SIZE = SIZE_MB * 1024 * 1024;
const PER_CONN_RATE = RATE_MB * 1024 * 1024;
let connRate = PER_CONN_RATE;   // 0 = 不限速
const CHUNK = 64 * 1024;

const SECRET = JSON.parse(fs.readFileSync(
  path.join(process.env.APPDATA, 'KokonaDownloader', 'settings.json'), 'utf8')).ApiSecret;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const fmtMB = (b) => (b / 1024 / 1024).toFixed(1);

/* ---------------- 受控服务端 ---------------- */

const PAYLOAD = Buffer.alloc(SIZE);
for (let i = 0; i < SIZE; i += 4) PAYLOAD.writeUInt32LE((i * 2654435761) >>> 0, i);

let active = 0;
let peakActive = 0;
// 时间加权的平均并发：峰值会骗人（一次重叠就能到 5），平均值才能反映"整段下载里
// 到底有几条连接在同时传"。
let activeTrace = [];
const served = { bytes: 0 };

const server = http.createServer((req, res) => {
  const u = new URL(req.url, 'http://127.0.0.1');
  if (u.pathname !== '/payload.bin') { res.writeHead(404); res.end(); return; }

  const range = req.headers.range;
  let start = 0, end = SIZE - 1, code = 200;
  const m = range && /^bytes=(\d+)-(\d*)$/.exec(range.trim());
  if (m) {
    start = Number(m[1]);
    end = m[2] ? Math.min(Number(m[2]), SIZE - 1) : SIZE - 1;
    if (start > end) { res.writeHead(416); res.end(); return; }
    code = 206;
  }
  const len = end - start + 1;

  active += 1;
  peakActive = Math.max(peakActive, active);

  res.writeHead(code, {
    'Content-Type': 'application/octet-stream',
    'Content-Length': len,
    'Accept-Ranges': 'bytes',
    ...(code === 206 ? { 'Content-Range': `bytes ${start}-${end}/${SIZE}` } : {}),
  });

  sendPaced(res, start, len)
    .catch(() => { })
    .finally(() => { active -= 1; });
});

/**
 * 等内核缓冲腾空。必须同时监听 'close'：客户端取消/断开时 'drain' 永远不会来，
 * 只等 drain 会让这个 Promise 悬挂、active 计数泄漏（第一版就是这样，5 个用例后
 * "平均并发"虚高到 20+，看起来像客户端超开了连接，其实是测量端在漏水）。
 */
function waitDrain(res) {
  return new Promise((resolve) => {
    const done = () => { res.off('drain', done); res.off('close', done); resolve(); };
    res.once('drain', done);
    res.once('close', done);
  });
}

/** 按每连接速率匀速下发；写不动时等 drain，避免把数据全堆进内核缓冲。 */
async function sendPaced(res, offset, len) {
  const t0 = Date.now();
  let sent = 0;
  while (sent < len) {
    if (res.writableEnded || res.destroyed) throw new Error('客户端已断开');
    const n = Math.min(CHUNK, len - sent);
    if (!res.write(PAYLOAD.subarray(offset + sent, offset + sent + n))) {
      await waitDrain(res);
    }
    sent += n;
    served.bytes += n;
    if (connRate > 0) {
      const due = (sent / connRate) * 1000;
      const wait = due - (Date.now() - t0);
      if (wait > 0) await sleep(wait);
    }
  }
  res.end();
}

await new Promise((r) => server.listen(0, '127.0.0.1', r));
const PORT = server.address().port;
const URL_ = `http://127.0.0.1:${PORT}/payload.bin`;

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

/** 读进程 CPU / 内存；PowerShell 一次性调用，别放在高频轮询里。 */
function readProc() {
  const script = '$p = Get-Process KokonaDownloader -ErrorAction SilentlyContinue;'
    + 'if ($p) { "$($p.TotalProcessorTime.TotalSeconds)|$($p.PeakWorkingSet64)|$($p.WorkingSet64)" }';
  const out = execFileSync('powershell', ['-NoProfile', '-Command', script], { encoding: 'utf8' }).trim();
  if (!out) throw new Error('客户端进程不在');
  const [cpu, peak, ws] = out.split('|').map(Number);
  return { cpu, peakWs: peak, ws };
}

/* ---------------- 单个用例 ---------------- */

async function runCase(conns) {
  const name = `perf-${conns}c.bin`;
  const target = path.join(DIR, name);
  fs.rmSync(target, { force: true });

  active = 0;      // 上一轮的请求应当都结束了；这里归零，避免任何残留污染本用例
  peakActive = 0;
  activeTrace = [];
  served.bytes = 0;
  const sampler = setInterval(() => { activeTrace.push({ t: Date.now() - t0, active }); }, 100);
  const before = readProc();

  const t0 = Date.now();
  const add = await api('POST', '/api/download', {
    urls: [URL_], dir: DIR, filename: name, connections: conns,
  });
  if (add.status !== 200) throw new Error(`添加任务失败 HTTP ${add.status}`);
  // 必须按 gid 认任务：按文件名认会撞上历史同名任务，读到的 completedLength 是上一轮的
  const gid = add.json && add.json.gid;

  const samples = [];
  let task = null;
  let settledAt = 0;
  let firstFullAt = 0;   // 最后一个字节落盘的时刻
  for (;;) {
    await sleep(150);
    const list = await api('GET', '/api/tasks');
    task = (list.json || []).find((t) => t.gid === gid);
    if (task) {
      samples.push({
        t: Date.now() - t0,
        done: task.completedLength,
        speed: task.downloadSpeed,
      });
      if (!firstFullAt && task.completedLength >= SIZE) firstFullAt = Date.now() - t0;
      if (task.state === 'completed' || task.state === 'failed') {
        // 再等一拍：状态可能先于 completedLength 刷新，否则会偶发判成 FAIL
        if (!settledAt) { settledAt = Date.now(); await sleep(300); continue; }
        break;
      }
    }
    if (Date.now() - t0 > 180000) throw new Error(`${name} 超时未完成`);
  }

  clearInterval(sampler);
  const elapsed = (Date.now() - t0) / 1000;
  // 传输与收尾分开算：跨卷落盘时"改名"其实是整份复制（C: 的断点 → D: 的目标），
  // 把它算进吞吐会把高连接数大文件的结果压得很难看，而那是另一个问题。
  const transferS = (firstFullAt || Date.now() - t0) / 1000;
  const finalizeS = Math.max(0, elapsed - transferS);
  const after = readProc();
  const cpuSec = after.cpu - before.cpu;

  // 稳定期：丢掉前 40% 的样本（连接需要逐个建立，启动段本来就慢）
  // 稳定期口径必须一致：速度取「进度到 40% 之后」的样本，并发也取同一时间窗，
  // 否则"单连接效率"会算出 >100% 这种自相矛盾的数字。
  const steadyStartMs = transferS * 1000 * 0.4;
  const steady = samples.filter((s) => s.done >= SIZE * 0.4);
  const steadyAvg = steady.length
    ? steady.reduce((a, s) => a + s.speed, 0) / steady.length
    : 0;
  const steadyConns = activeTrace.filter((x) => x.t >= steadyStartMs);
  const avgConns = steadyConns.length
    ? steadyConns.reduce((a, x) => a + x.active, 0) / steadyConns.length
    : 0;

  // 失败时要说清楚是哪一项不满足，否则只看到一个 FAIL 没法排查
  const why = [];
  if (task.state !== 'completed') why.push(`state=${task.state}`);
  if (task.completedLength !== SIZE) why.push(`len=${task.completedLength}/${SIZE}`);
  if (!fs.existsSync(target)) why.push('文件不存在');
  else if (fs.statSync(target).size !== SIZE) why.push(`size=${fs.statSync(target).size}`);

  if (process.argv.includes('--trace')) {
    const marks = [];
    for (let t = 0; t <= elapsed * 1000; t += 500) {
      const a = activeTrace.filter((x) => x.t <= t).pop();
      const sm = samples.filter((x) => x.t <= t).pop();
      marks.push(`${(t / 1000).toFixed(1)}s active=${a ? a.active : '-'} done=${sm ? (sm.done / 1024 / 1024).toFixed(0) : 0}MB`);
    }
    console.log(`\n  [trace ${conns}c] ${marks.join(' | ')}\n`);
  }

  const row = {
    连接数: conns,
    用时s: +elapsed.toFixed(2),
    传输s: +transferS.toFixed(2),
    收尾s: +finalizeS.toFixed(2),
    平均MBps: +(SIZE / transferS / 1024 / 1024).toFixed(2),
    稳定期MBps: +(steadyAvg / 1024 / 1024).toFixed(2),
    平均并发: +avgConns.toFixed(2),
    并发峰值: peakActive,
    CPU秒: +cpuSec.toFixed(2),
    平均CPU核: +(cpuSec / elapsed).toFixed(2),
    峰值内存MB: +(after.peakWs / 1024 / 1024).toFixed(0),
    结果: why.length ? 'FAIL:' + why.join(',') : 'OK',
  };

  if (gid) {
    await api('POST', `/api/tasks/${gid}/remove`, {}).catch(() => { });
  }
  fs.rmSync(target, { force: true });
  return row;
}

/* ---------------- 跑 ---------------- */

fs.mkdirSync(DIR, { recursive: true });
console.log(`[perf] ${SIZE_MB} MB · 每连接限速 ${RATE_MB} MB/s · 用例 ${CASES.join('/')} 连接`);

const rows = [];
for (const c of CASES) {
  process.stdout.write(`[perf] 跑 ${c} 连接… `);
  const row = await runCase(c);
  rows.push(row);
  console.log(`${row.用时s}s  ${row.平均MBps} MB/s  CPU ${row.平均CPU核} 核`);
  await sleep(800);   // 让上一轮的连接彻底释放
}

server.close();

/* ---------------- 报告 ---------------- */

const base = rows.find((r) => r.连接数 === Math.min(...CASES));
const head = ['连接数', '传输s', '收尾s', '平均MBps', '稳定期MBps', '理论MBps', '达成率', '平均并发', '并发峰值', '加速比', 'CPU核', '峰值内存MB', '结果'];
const lines = rows.map((r) => [
  r.连接数, r.传输s, r.收尾s, r.平均MBps, r.稳定期MBps, (RATE_MB * r.连接数).toFixed(0),
  // 稳定期吞吐 / (连接数 × 每连接额定)。<100% 的差额来自连接逐个建立 + 服务端限速精度
  (r.稳定期MBps / (RATE_MB * r.连接数) * 100).toFixed(0) + '%',
  r.平均并发, r.并发峰值, (r.稳定期MBps / base.稳定期MBps).toFixed(2),
  r.平均CPU核, r.峰值内存MB, r.结果,
]);

const width = head.map((h, i) => Math.max(strWidth(h), ...lines.map((l) => strWidth(String(l[i])))));
const fmtRow = (cells) => '| ' + cells.map((c, i) => pad(String(c), width[i])).join(' | ') + ' |';
const sep = '|' + width.map((w) => '-'.repeat(w + 2)).join('|') + '|';

console.log('');
console.log(fmtRow(head));
console.log(sep);
for (const l of lines) console.log(fmtRow(l));

const md = [fmtRow(head), sep, ...lines.map((l) => fmtRow(l))].join('\n');
const report = [
  `# 下载性能专项（${new Date().toISOString().slice(0, 19).replace('T', ' ')}）`,
  '',
  `- 文件：${SIZE_MB} MB，服务端对**每条连接**限速 ${RATE_MB} MB/s`,
  `- 客户端：${API}，全局不限速，HTTP/2 ${process.env.KOKONA_H2 || '关'}`,
  `- 稳定期：丢弃前 40% 样本（连接逐个建立，启动段本来就慢）`,
  '',
  md,
  '',
].join('\n');
fs.writeFileSync(path.join(DIR, 'report.md'), report, 'utf8');
console.log('');
console.log(`[perf] 报告: ${path.join(DIR, 'report.md')}`);

/* 打印用的小工具 */
function strWidth(s) {
  let w = 0;
  for (const ch of s) w += /[\u4e00-\u9fa5]/.test(ch) ? 2 : 1;
  return w;
}
function pad(s, w) {
  return s + ' '.repeat(Math.max(0, w - strWidth(s)));
}
