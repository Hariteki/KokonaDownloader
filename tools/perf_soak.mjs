#!/usr/bin/env node
/**
 * 大文件持续下载 / 多任务并发的测量：轮询 /api/tasks，按 tasklist 采样内存。
 *
 * 用法（客户端需在运行）：
 *   node tools/perf_server.mjs &                       # 拿到端口
 *   node tools/perf_soak.mjs --port=PORT --size=512 --tasks=1 --conns=16 --label=sustained
 *   node tools/perf_soak.mjs --port=PORT --size=64  --tasks=3 --conns=8  --label=conc3
 *   加 TRACE=1 环境变量可打印每秒的连接数/速度/进度。
 *
 * 与 perf_bench.mjs 的分工：perf_bench 做"连接数—吞吐"的定长快测（128MB/多档位）；
 * 这个脚本做"大文件是否长尾""多任务并发能否叠加""内存是否随文件增长"。
 */
// 持续/并发下载测量：轮询 /api/tasks，按 tasklist 采样内存
import { execSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const arg = (k, d) => {
  const hit = process.argv.find((a) => a.startsWith(`--${k}=`));
  return hit ? hit.slice(k.length + 3) : d;
};
const PORT = Number(arg('port', '0'));
const SIZE_MB = Number(arg('size', '512'));
const TASKS = Number(arg('tasks', '1'));
const CONNS = Number(arg('conns', '16'));
const LABEL = arg('label', 'bench');

const SECRET = JSON.parse(
  fs.readFileSync(path.join(process.env.APPDATA, 'KokonaDownloader', 'settings.json'), 'utf8').replace(/^\uFEFF/, ''),
).ApiSecret;
const DIR = path.join(process.env.LOCALAPPDATA, 'Temp', 'kokona-perf');
fs.mkdirSync(DIR, { recursive: true });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const api = async (m, p, b) => {
  const r = await fetch(`http://127.0.0.1:16800${p}`, {
    method: m,
    headers: { 'X-Kokona-Secret': SECRET, 'Content-Type': 'application/json' },
    body: b === undefined ? undefined : JSON.stringify(b),
  });
  return { status: r.status, json: await r.json().catch(() => null) };
};

function memMB() {
  try {
    const out = execSync('tasklist /FI "IMAGENAME eq KokonaDownloader.exe" /FO CSV /NH', { encoding: 'utf8' });
    const line = out.split('\n').find((l) => l.includes('KokonaDownloader'));
    if (!line) return 0;
    const cells = line.split('","');
    const mem = cells[cells.length - 1].replace(/[^\d]/g, '');   // "198,432 K"
    return Number(mem) / 1024;
  } catch { return 0; }
}

const gids = [];
const t0 = Date.now();
for (let i = 1; i <= TASKS; i++) {
  const r = await api('POST', '/api/download', {
    urls: [`http://127.0.0.1:${PORT}/b-${i}.bin`],
    filename: `${LABEL}-${i}.bin`,
    connections: CONNS,
  });
  if (r.status !== 200) { console.log('添加失败', r.status, JSON.stringify(r.json)); process.exit(1); }
  gids.push(r.json.gid);
}
console.log(`任务 ${gids.join(',')}  (${TASKS} × ${SIZE_MB}MB × ${CONNS} 连接)`);

let peak = 0, lastMemAt = 0;
const speeds = [];
let done = 0, failed = 0;
while (Date.now() - t0 < 900_000) {
  const list = (await api('GET', '/api/tasks')).json || [];
  const mine = list.filter((t) => gids.includes(t.gid));
  done = mine.filter((t) => t.state === 'completed').length;
  failed = mine.filter((t) => t.state === 'failed').length;
  const now = Date.now();
  if (now - lastMemAt > 800) { peak = Math.max(peak, memMB()); lastMemAt = now; }
  const activeSpeed = mine.reduce((s, t) => s + (t.downloadSpeed || 0), 0);
  if (process.env.TRACE && now - lastMemAt <= 800) {
    const conns = mine.reduce((s, t) => s + (t.connections || 0), 0);
    const pct = mine.reduce((s, t) => s + (t.completedLength || 0), 0) / (SIZE_MB * 1048576 * TASKS) * 100;
    console.log(`  ${((now - t0) / 1000).toFixed(1)}s  连接=${conns}  ${(activeSpeed / 1048576).toFixed(1)} MB/s  ${pct.toFixed(0)}%`);
  }
  if (done < TASKS) speeds.push(activeSpeed / 1048576);
  if (done === TASKS || failed > 0) break;
  await sleep(200);
}
const secs = (Date.now() - t0) / 1000;

// 稳定期：去掉前 15% 的爬坡样本
const steady = speeds.length > 6 ? speeds.slice(Math.floor(speeds.length * 0.15)) : speeds;
const avgSteady = steady.length ? steady.reduce((a, b) => a + b, 0) / steady.length : 0;

console.log(
  `完成 ${done}/${TASKS}  ${secs.toFixed(2)}s  ` +
  `聚合 ${((SIZE_MB * TASKS) / secs).toFixed(1)} MB/s  ` +
  `稳定期 ${avgSteady.toFixed(1)} MB/s  ` +
  `峰值内存 ${Math.round(peak)} MB`,
);

await api('POST', '/api/tasks/clear', {});
