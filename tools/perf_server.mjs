#!/usr/bin/env node
/**
 * 大文件/并发性能测试用的服务端：**每条连接**限速，支持 Range。
 *
 * 用法：
 *   SIZE=512 node tools/perf_server.mjs      # 启动后打印端口
 *   （配合 tools/perf_soak.mjs --port=<端口> --size=512 ...）
 *
 * 为什么不用公网文件：公网速度抖动能到 40%，只有"每连接固定速率 + 可复现"才能
 * 把"多连接能不能叠加"这件事量出来。
 */
// 性能测试服务：单连接限速 4MB/s，支持 Range
import http from 'node:http';

const SIZE = Number(process.env.SIZE || 512) * 1024 * 1024;
const RATE = Number(process.env.RATE || 4) * 1024 * 1024;   // 每连接字节/秒
const BUF = Buffer.alloc(64 * 1024, 5);

const server = http.createServer(async (req, res) => {
  const m = /^bytes=(\d+)-(\d*)$/.exec(req.headers.range || '');
  let start = 0, end = SIZE - 1;
  if (m) {
    start = Number(m[1]);
    if (m[2]) end = Math.min(Number(m[2]), SIZE - 1);
    res.writeHead(206, {
      'Content-Type': 'application/octet-stream',
      'Content-Length': end - start + 1,
      'Content-Range': `bytes ${start}-${end}/${SIZE}`,
      'Accept-Ranges': 'bytes',
    });
  } else {
    res.writeHead(200, {
      'Content-Type': 'application/octet-stream',
      'Content-Length': SIZE,
      'Accept-Ranges': 'bytes',
    });
  }

  let pos = start;
  // 按"每连接限速"节流：每 200ms 发 RATE/5 字节
  const tickBytes = Math.floor(RATE / 5);
  while (pos <= end) {
    if (res.destroyed) return;
    let sent = 0;
    while (sent < tickBytes && pos <= end) {
      const n = Math.min(BUF.length, end - pos + 1, tickBytes - sent);
      if (!res.write(BUF.subarray(0, n))) await new Promise((r) => res.once('drain', r));
      pos += n; sent += n;
    }
    await new Promise((r) => setTimeout(r, 200));
  }
  res.end();
});

server.listen(0, '127.0.0.1', () => console.log(server.address().port));
