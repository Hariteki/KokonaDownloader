#!/usr/bin/env node
/**
 * 单文件 exe 打包：发布 → 只留那一个 exe → 校验嵌入载荷。
 *
 * 为什么需要这个脚本：`dotnet publish` 出来的目录里**始终带一份散文件**
 * （aria2c.exe / *.xbf / resources.pri / icons）。这些文件同时被 EmbedStandalonePayload
 * 嵌进了 exe，所以它们只是构建副产物。但只要散文件还在 exe 旁边，StandaloneBootstrap
 * 就会走"目录完整，直接跑"的分支——**发出去的东西就不是单文件了**。
 * 手工挑容易挑错，所以固化下来。
 *
 * 用法：
 *   node tools/pack_singlefile.mjs            # 打包到 dist/KokonaSingleFile
 *   node tools/pack_singlefile.mjs --keep-pdb # 连 pdb 一起留（排查崩溃用）
 */

import { execFileSync } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..');
const PROJ = path.join(ROOT, 'src', 'KokonaDownloader.App', 'KokonaDownloader.App.csproj');
const OUT = path.join(ROOT, 'dist', 'KokonaSingleFile');
const STAGE = path.join(ROOT, 'artifacts', '.pack-single');
const EXE = 'KokonaDownloader.exe';

// 载荷清单（与 csproj 的 EmbedStandalonePayload 对齐：8 个 xbf + pri + aria2c + 2 图标）
const PAYLOAD = [
  'standalone/App.xbf',
  'standalone/BtPieceGrid.xbf',
  'standalone/MagnetConfirmWindow.xbf',
  'standalone/MainWindow.xbf',
  'standalone/NewDownloadDialog.xbf',
  'standalone/ProgressWindow.xbf',
  'standalone/SettingsWindow.xbf',
  'standalone/Themes/ThemeSwatchPicker.xbf',
  'standalone/resources.pri',
  'standalone/aria2c.exe',
  'standalone/icons/tray.ico',
  'standalone/icons/tray.png',
];

const keepPdb = process.argv.includes('--keep-pdb');

function fail(msg) {
  console.error(`\n✗ ${msg}`);
  process.exit(1);
}

// 1) 只有"正在运行的正是 dist 里这一份"才会锁文件：直接试着以写方式打开
//    （用 tasklist 按进程名判断会误伤——从 %LOCALAPPDATA% 跑的实例并不锁 dist）
const outExe = path.join(OUT, EXE);
if (fs.existsSync(outExe)) {
  try {
    fs.closeSync(fs.openSync(outExe, 'r+'));
  } catch {
    fail('dist 里的 exe 正被占用（客户端还在跑），先退出客户端再打包');
  }
}

// 2) 版本号（唯一来源 Directory.Build.props）
const props = fs.readFileSync(path.join(ROOT, 'src', 'Directory.Build.props'), 'utf8');
const version = /<Version>([^<]+)<\/Version>/.exec(props)?.[1] ?? 'unknown';
console.log(`[pack] 版本 ${version}，开始发布…`);

// 3) 发布到暂存目录（不直接写 dist，避免上一次的残留混进来）
fs.rmSync(STAGE, { recursive: true, force: true });
fs.mkdirSync(STAGE, { recursive: true });
execFileSync('dotnet', [
  'publish', PROJ,
  '-c', 'Release', '-r', 'win-x64',
  '-p:PublishSingleFile=true',
  '-p:IncludeNativeLibrariesForSelfExtract=true',
  '-p:WindowsAppSDKSingleFileVerifyConfiguration=false',
  '-o', STAGE,
], { stdio: 'inherit', cwd: ROOT });

// 4) 只把 exe（和可选 pdb）搬到 dist
fs.rmSync(OUT, { recursive: true, force: true });
fs.mkdirSync(OUT, { recursive: true });
const staged = path.join(STAGE, EXE);
if (!fs.existsSync(staged)) fail(`发布结果里没有 ${EXE}`);
fs.copyFileSync(staged, path.join(OUT, EXE));
if (keepPdb) {
  for (const f of fs.readdirSync(STAGE)) {
    if (f.endsWith('.pdb')) fs.copyFileSync(path.join(STAGE, f), path.join(OUT, f));
  }
}

// 5) 校验嵌入载荷：资源名以 ASCII 形式留在 exe 的清单里
const bytes = fs.readFileSync(path.join(OUT, EXE));
const ascii = bytes.toString('latin1');
const utf16 = bytes.toString('utf16le');
const missing = PAYLOAD.filter((n) => !ascii.includes(n) && !utf16.includes(n));
if (missing.length > 0) {
  console.error('缺失的嵌入资源：');
  for (const m of missing) console.error('  MISS ' + m);
  fail(`嵌入载荷不完整（${PAYLOAD.length - missing.length}/${PAYLOAD.length}）`);
}

// 6) 打印结果
const size = fs.statSync(path.join(OUT, EXE)).size;
const sha = crypto.createHash('sha256').update(bytes).digest('hex').toUpperCase();
console.log(`\n✓ dist/KokonaSingleFile/${EXE}`);
console.log(`  版本   ${version}`);
console.log(`  大小   ${size.toLocaleString()} 字节（${(size / 1024 / 1024).toFixed(1)} MB）`);
console.log(`  SHA256 ${sha}`);
console.log(`  载荷   ${PAYLOAD.length}/${PAYLOAD.length} 嵌入资源`);
console.log('\n注意：分发只发这一个 exe。目录里若混入 aria2c.exe / *.xbf / resources.pri，');
console.log('      StandaloneBootstrap 会走"目录完整"分支，那就不是单文件了。');

// 7) 清掉暂存目录（里面那一堆散文件就是构建副产物）
fs.rmSync(STAGE, { recursive: true, force: true });
