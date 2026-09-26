# 单文件 exe 验证（构建嵌入检查 + A 干净目录 + B dist 目录）
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class WinProbe3 {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static List<string> Snapshot(uint targetPid) {
        var list = new List<string>();
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != targetPid) return true;
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, sb.Capacity);
            RECT r; GetWindowRect(h, out r);
            bool vis = IsWindowVisible(h);
            list.Add(string.Format("hwnd={0} visible={1} size={2}x{3} pos=({4},{5}) title='{6}'",
                h, vis, r.Right - r.Left, r.Bottom - r.Top, r.Left, r.Top, sb.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
'@

$dist = "D:\Project\kokonaDown\dist\KokonaSingleFile"
$exeName = "KokonaDownloader.exe"
# 注意：/api/ping 是唯一免鉴权端点，这里不需要密钥。
# 早期版本把用户真实的 ApiSecret 硬编码在此并随仓库推送（已移除）；
# 需要鉴权的探测请从 %APPDATA%\KokonaDownloader\settings.json 读取 apiSecret，不要写死在脚本里。

function Stop-App {
    Get-Process KokonaDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

function Test-Ping {
    param([int]$tries = 60)
    for ($i = 0; $i -lt $tries; $i++) {
        try {
            $r = Invoke-WebRequest -Uri "http://127.0.0.1:16800/api/ping" -UseBasicParsing -TimeoutSec 2
            if ($r.StatusCode -eq 200) { return $r.Content }
        } catch {}
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Probe-Launch {
    param([string]$path, [string]$workdir, [int]$ms = 8000)
    $p = Start-Process -FilePath $path -WorkingDirectory $workdir -PassThru
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $seen = @{}
    $splashSeen = $false
    while ($sw.ElapsedMilliseconds -lt $ms) {
        foreach ($line in [WinProbe3]::Snapshot([uint32]$p.Id)) {
            if ($line -match "visible=True size=(336|420)x(336|420)") { $splashSeen = $true }
            if (-not $seen.ContainsKey($line)) {
                $seen[$line] = $sw.ElapsedMilliseconds
                Write-Host ("[{0,5} ms] {1}" -f $sw.ElapsedMilliseconds, $line)
            }
        }
        Start-Sleep -Milliseconds 120
    }
    return @{ Process = $p; SplashSeen = $splashSeen }
}

# ════ 构建验证：exe 内嵌 standalone/ 资源 ════
Write-Host "=== build embed check ==="
$bytes = [System.IO.File]::ReadAllBytes("$dist\$exeName")
$text = [System.Text.Encoding]::ASCII.GetString($bytes)
$names = @("standalone/App.xbf","standalone/BtPieceGrid.xbf","standalone/MagnetConfirmWindow.xbf",
          "standalone/MainWindow.xbf","standalone/NewDownloadDialog.xbf","standalone/ProgressWindow.xbf",
          "standalone/SettingsWindow.xbf",
          "standalone/Themes/ThemeSwatchPicker.xbf",
          "standalone/resources.pri","standalone/aria2c.exe","standalone/icons/tray.ico","standalone/icons/tray.png")
$missing = 0
foreach ($n in $names) {
    if ($text.Contains($n)) { Write-Host ("  OK   " + $n) } else { Write-Host ("  MISS " + $n); $missing++ }
}
# 启动动画 SplashWindow 已在 1.0.8 移除（项目文档 §12「移除启动动画」），
# 载荷是 8 个 xbf + resources.pri + aria2c.exe + 2 个图标 = 12 个文件
if ($missing -eq 0) { Write-Host "PASS: 12/12 embedded resources present" } else { Write-Host "FAIL: $missing missing" }

# ════ 测试 A：干净临时目录，只放 exe ════
# 产品语义（第九轮起，见 StandaloneBootstrap）：exe 目录必须保持干净 —— 载荷一律解压到
#   %LOCALAPPDATA%\KokonaDownloader\Standalone 并从那里重新拉起，exe 旁不留任何文件。
#   启动动画（SplashWindow）已在 1.0.8 移除，故不再断言 splash 窗口。
Write-Host ""
Write-Host "=== test A: clean temp dir, exe only ==="
Stop-App
$dirA = Join-Path $env:TEMP "kokona_test_A"
if (Test-Path $dirA) { Remove-Item $dirA -Recurse -Force }
New-Item -ItemType Directory -Path $dirA | Out-Null
Copy-Item "$dist\$exeName" $dirA
$resA = Probe-Launch -Path "$dirA\$exeName" -Workdir $dirA

$ping = Test-Ping
Write-Host ("ping: " + $ping)
if ($null -ne $ping -and $ping -match '"version":"1\.0\.9"') { Write-Host "PASS: ping 200 version 1.0.9" } else { Write-Host "FAIL: ping missing or wrong version" }

# exe 目录保持干净：除 exe 自己外不应有任何解压出来的文件
$leftover = @(Get-ChildItem $dirA -Recurse -File | Where-Object { $_.Name -ne $exeName })
if ($leftover.Count -eq 0) { Write-Host "PASS: exe dir stays clean (nothing extracted next to exe)" }
else { Write-Host ("FAIL: $($leftover.Count) file(s) leaked next to exe: " + (($leftover | ForEach-Object { $_.Name }) -join ', ')) }

# 运行时文件必须落在 %LOCALAPPDATA%\KokonaDownloader\Standalone，且跑的是**这一份** exe
$rt = Join-Path $env:LOCALAPPDATA "KokonaDownloader\Standalone"
$rtPayload = @(Get-ChildItem $rt -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne $exeName -and $_.Name -ne ".kokona_bootstrap.json" })
$rtExe = Join-Path $rt $exeName
$rtExeOk = (Test-Path $rtExe) -and ((Get-Item $rtExe).Length -eq (Get-Item "$dist\$exeName").Length)
Write-Host ("standalone runtime dir: payload=$($rtPayload.Count) exeSizeMatchesDist=$rtExeOk")
if ($rtPayload.Count -ge 12 -and $rtExeOk) { Write-Host "PASS: runtime payload extracted to LOCALAPPDATA (>=12 files, exe matches dist)" }
else { Write-Host "FAIL: standalone runtime dir incomplete" }

$log = Get-Content "$env:APPDATA\KokonaDownloader\app.log" -Tail 40 -ErrorAction SilentlyContinue
if ($log | Select-String "XamlParseException") { Write-Host "FAIL: XamlParseException in log" } else { Write-Host "PASS: no XamlParseException in recent log" }
Stop-App

# ════ 测试 B：dist 目录（resources.pri 已存在 → 跳过解压）════
Write-Host ""
Write-Host "=== test B: dist dir (loose files present) ==="
$resB = Probe-Launch -Path "$dist\$exeName" -Workdir $dist
$pingB = Test-Ping
Write-Host ("ping: " + $pingB)
if ($null -ne $pingB -and $pingB -match '"version":"1\.0\.9"') { Write-Host "PASS: dist dir launch works, ping 200 v1.0.9" } else { Write-Host "FAIL: dist dir launch broken" }
Stop-App

Write-Host ""
Write-Host "=== single-file main checks done ==="
