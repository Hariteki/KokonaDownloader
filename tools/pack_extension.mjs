/**
 * 把 extension-v2 打成可分发的 zip。
 *
 * 为什么不用 PowerShell 的 Compress-Archive：它写出的条目分隔符在部分版本里是 `\`，
 * 浏览器解包时会当成一个超长文件名，表现为"扩展加载失败但不说原因"。
 * 这里自己写 zip（deflate），条目名一律 `/`，跨工具解包都正常。
 *
 * 用法：
 *   node tools/pack_extension.mjs                  # 产出 dist/kokona-extension-v2-<ver>.zip
 *   node tools/pack_extension.mjs --for-store      # 顺手去掉 manifest 的 key（商店上传用）
 */

import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(__dirname, '..');
const SRC = path.join(REPO, 'extension-v2');
const OUT_DIR = path.join(REPO, 'dist');
const FOR_STORE = process.argv.includes('--for-store');

/** 不打进包里的开发文件。 */
const EXCLUDE_DIRS = new Set(['test', 'node_modules', '.git']);
const EXCLUDE_FILES = new Set(['package.json', 'package-lock.json', '.ext-key.json', 'README.md']);

/* ---------------- zip 写入 ---------------- */

const CRC_TABLE = (() => {
  const t = new Int32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c;
  }
  return t;
})();

function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function dosTime(d) {
  const time = (d.getHours() << 11) | (d.getMinutes() << 5) | (d.getSeconds() >> 1);
  const date = ((d.getFullYear() - 1980) << 9) | ((d.getMonth() + 1) << 5) | d.getDate();
  return { time, date };
}

function zip(entries) {
  const { time, date } = dosTime(new Date());
  const locals = [];
  const centrals = [];
  let offset = 0;

  for (const { name, data } of entries) {
    const nameBuf = Buffer.from(name, 'utf8');
    const crc = crc32(data);
    const deflated = zlib.deflateRawSync(data, { level: 9 });
    // 压不下去就直接存，省得比原文件还大
    const useDeflate = deflated.length < data.length;
    const body = useDeflate ? deflated : data;
    const method = useDeflate ? 8 : 0;

    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0x0800, 6);   // UTF-8 文件名
    local.writeUInt16LE(method, 8);
    local.writeUInt16LE(time, 10);
    local.writeUInt16LE(date, 12);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(body.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(nameBuf.length, 26);
    local.writeUInt16LE(0, 28);

    locals.push(local, nameBuf, body);

    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(20, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0x0800, 8);
    central.writeUInt16LE(method, 10);
    central.writeUInt16LE(time, 12);
    central.writeUInt16LE(date, 14);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(body.length, 20);
    central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(nameBuf.length, 28);
    central.writeUInt16LE(0, 30);
    central.writeUInt16LE(0, 32);
    central.writeUInt16LE(0, 34);
    central.writeUInt16LE(0, 36);
    central.writeUInt32LE(0, 38);
    central.writeUInt32LE(offset, 42);
    centrals.push(central, nameBuf);

    offset += local.length + nameBuf.length + body.length;
  }

  const centralBuf = Buffer.concat(centrals);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(0, 4);
  end.writeUInt16LE(0, 6);
  end.writeUInt16LE(entries.length, 8);
  end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(centralBuf.length, 12);
  end.writeUInt32LE(offset, 16);
  end.writeUInt16LE(0, 20);

  return Buffer.concat([...locals, centralBuf, end]);
}

/* ---------------- 收集文件 ---------------- */

function walk(dir, base = '') {
  const out = [];
  for (const name of fs.readdirSync(dir).sort()) {
    const full = path.join(dir, name);
    const rel = base ? `${base}/${name}` : name;
    const st = fs.statSync(full);
    if (st.isDirectory()) {
      if (EXCLUDE_DIRS.has(name)) continue;
      out.push(...walk(full, rel));
    } else {
      if (EXCLUDE_FILES.has(name)) continue;
      out.push({ rel, full });
    }
  }
  return out;
}

/* ---------------- 扩展 id ---------------- */

function extensionIdOf(manifest) {
  if (!manifest.key) return null;
  const der = Buffer.from(manifest.key, 'base64');
  const hex = crypto.createHash('sha256').update(der).digest('hex').slice(0, 32);
  return [...hex].map((c) => String.fromCharCode(97 + parseInt(c, 16))).join('');
}

/* ---------------- 主流程 ---------------- */

const manifestPath = path.join(SRC, 'manifest.json');
const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));

const problems = [];
if (manifest.manifest_version !== 3) problems.push('manifest_version 必须是 3');
if (!manifest.background || !manifest.background.service_worker) problems.push('缺少 background.service_worker');
for (const f of ['sw/background.js', manifest.options_page, manifest.action && manifest.action.default_popup]) {
  if (f && !fs.existsSync(path.join(SRC, f))) problems.push(`manifest 引用了不存在的文件: ${f}`);
}
if (problems.length) {
  console.error('[pack] 校验失败:\n  - ' + problems.join('\n  - '));
  process.exit(1);
}

if (FOR_STORE) delete manifest.key;
const manifestBuf = Buffer.from(JSON.stringify(manifest, null, 2) + '\n', 'utf8');

const files = walk(SRC).map(({ rel, full }) => ({ name: rel, data: fs.readFileSync(full) }));
// manifest 用改过的版本替换掉原文件（--for-store 会删 key）
const idx = files.findIndex((f) => f.name === 'manifest.json');
if (idx >= 0) files[idx].data = manifestBuf;

const sorted = [files[idx], ...files.filter((_, i) => i !== idx)];
const zipBuf = zip(sorted);

fs.mkdirSync(OUT_DIR, { recursive: true });
const outName = `kokona-extension-v2-${manifest.version}${FOR_STORE ? '-store' : ''}.zip`;
const outPath = path.join(OUT_DIR, outName);
fs.writeFileSync(outPath, zipBuf);

console.log('[pack] 版本    ', manifest.version);
console.log('[pack] 扩展 id ', extensionIdOf(manifest) || '(无 key，随安装路径变化)');
console.log('[pack] 文件数  ', sorted.length);
console.log('[pack] 体积    ', (zipBuf.length / 1024).toFixed(1), 'KB');
console.log('[pack] 输出    ', outPath);
if (FOR_STORE) console.log('[pack] 已移除 manifest.key（商店上传用）');
