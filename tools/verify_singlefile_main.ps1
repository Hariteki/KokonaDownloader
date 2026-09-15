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
$secret = "a938ca540c347dbdcaf263fe86250661"

function Stop-App {
    Get-Process KokonaDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

function Test-Ping {
    param([int]$tries = 60)
    for ($i = 0; $i -lt $tries; $i++) {
        try {
            $r = Invoke-WebRequest -Uri "http://127.0.0.1:16800/api/ping" -Headers @{ "X-Kokona-Secret" = $secret } -UseBasicParsing -TimeoutSec 2
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

# ════ 构建验证：exe 内嵌 13 个 standalone/ 资源（含 v1.0.6 新增的 SplashWindow.xbf）════
Write-Host "=== build embed check ==="
$bytes = [System.IO.File]::ReadAllBytes("$dist\$exeName")
$text = [System.Text.Encoding]::ASCII.GetString($bytes)
$names = @("standalone/App.xbf","standalone/BtPieceGrid.xbf","standalone/MagnetConfirmWindow.xbf",
          "standalone/MainWindow.xbf","standalone/NewDownloadDialog.xbf","standalone/ProgressWindow.xbf",
          "standalone/SettingsWindow.xbf","standalone/SplashWindow.xbf",
          "standalone/Themes/ThemeSwatchPicker.xbf",
          "standalone/resources.pri","standalone/aria2c.exe","standalone/icons/tray.ico","standalone/icons/tray.png")
$missing = 0
foreach ($n in $names) {
    if ($text.Contains($n)) { Write-Host ("  OK   " + $n) } else { Write-Host ("  MISS " + $n); $missing++ }
}
if ($missing -eq 0) { Write-Host "PASS: 13/13 embedded resources present" } else { Write-Host "FAIL: $missing missing" }

# ════ 测试 A：干净临时目录，只放 exe（含 B.1 splash 复测）════
Write-Host ""
Write-Host "=== test A: clean temp dir, exe only ==="
Stop-App
$dirA = Join-Path $env:TEMP "kokona_test_A"
if (Test-Path $dirA) { Remove-Item $dirA -Recurse -Force }
New-Item -ItemType Directory -Path $dirA | Out-Null
Copy-Item "$dist\$exeName" $dirA
$resA = Probe-Launch -Path "$dirA\$exeName" -Workdir $dirA
if ($resA.SplashSeen) { Write-Host "PASS: splash visible on single-file exe (B.1)" } else { Write-Host "FAIL: splash not seen in test A" }

$ping = Test-Ping
Write-Host ("ping: " + $ping)
if ($null -ne $ping -and $ping -match '"version":"1\.0\.6"') { Write-Host "PASS: ping 200 version 1.0.6" } else { Write-Host "FAIL: ping missing or wrong version" }

# 载荷含子目录（icons\、Themes\），必须递归统计；排除 exe 自身 → 期望 13 个载荷文件
$payload = @(Get-ChildItem $dirA -Recurse -File | Where-Object { $_.Name -ne $exeName })
Write-Host ("payload files next to exe (recursive, excl. exe): " + $payload.Count)
if ($payload.Count -ge 13) { Write-Host "PASS: payload extracted (>=13 files)" } else { Write-Host "FAIL: only $($payload.Count) files" }

$log = Get-Content "$env:APPDATA\KokonaDownloader\app.log" -Tail 40 -ErrorAction SilentlyContinue
if ($log | Select-String "XamlParseException") { Write-Host "FAIL: XamlParseException in log" } else { Write-Host "PASS: no XamlParseException in recent log" }
Stop-App

# ════ 测试 B：dist 目录（resources.pri 已存在 → 跳过解压）════
Write-Host ""
Write-Host "=== test B: dist dir (loose files present) ==="
$resB = Probe-Launch -Path "$dist\$exeName" -Workdir $dist
$pingB = Test-Ping
Write-Host ("ping: " + $pingB)
if ($null -ne $pingB -and $pingB -match '"version":"1\.0\.6"') { Write-Host "PASS: dist dir launch works, ping 200 v1.0.6" } else { Write-Host "FAIL: dist dir launch broken" }
Stop-App

Write-Host ""
Write-Host "=== single-file main checks done ==="
