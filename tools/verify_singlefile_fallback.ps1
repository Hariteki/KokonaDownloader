# 单文件 exe 只读目录回退验证（测试 C）：icacls 拒绝写 → 应回退 %LOCALAPPDATA%\KokonaDownloader\Standalone 并重新拉起
$dist = "D:\Project\kokonaDown\dist\KokonaSingleFile"
$exeName = "KokonaDownloader.exe"
# /api/ping 免鉴权，无需密钥；需要鉴权的探测请从 settings.json 读 apiSecret（不要写死在脚本里）
$fallback = Join-Path $env:LOCALAPPDATA "KokonaDownloader\Standalone"
if (-not (Test-Path "$dist\$exeName")) { Write-Host "FAIL: dist exe missing: $dist"; exit 1 }

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

Write-Host "=== test C: read-only dir fallback ==="
Stop-App
$dirC = Join-Path $env:TEMP "kokona_test_C"
if (Test-Path $dirC) {
    icacls $dirC /remove /deny "Everyone:(W)" 2>$null | Out-Null
    Remove-Item $dirC -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $dirC | Out-Null
Copy-Item "$dist\$exeName" $dirC
icacls $dirC /deny "Everyone:(W)" | Out-Null
Write-Host "write denied on: $dirC"

# 记录回退目录原有文件数（用于判断是否重新解压）
$before = if (Test-Path $fallback) { (Get-ChildItem $fallback -Recurse -File).Count } else { 0 }
Write-Host ("fallback dir files before (recursive): " + $before)

$p = Start-Process -FilePath "$dirC\$exeName" -WorkingDirectory $dirC -PassThru
Start-Sleep -Seconds 5
# 原进程应已 Exit(0)，新进程从回退目录拉起；用进程 exe 路径判定实际运行位置
$procs = Get-Process KokonaDownloader -ErrorAction SilentlyContinue
foreach ($proc in $procs) {
    Write-Host ("live process " + $proc.Id + " (original=" + $p.Id + ") path: " + $proc.Path)
}

$ping = Test-Ping
Write-Host ("ping: " + $ping)
if ($null -ne $ping -and $ping -match '"version":"1\.0\.9"') { Write-Host "PASS: fallback relaunch works, ping 200 v1.0.9" } else { Write-Host "FAIL: fallback relaunch broken" }

$after = if (Test-Path $fallback) { (Get-ChildItem $fallback -Recurse -File).Count } else { 0 }
Write-Host ("fallback dir files after (recursive, incl. exe copy): " + $after)
if ($after -ge 13) { Write-Host "PASS: fallback payload present (>=13 files)" } else { Write-Host "FAIL: fallback payload incomplete" }

# 清理：恢复写权限、删除测试目录（保留回退目录——它是应用正常回退位置）
Stop-App
icacls $dirC /remove /deny "Everyone:(W)" 2>$null | Out-Null
Start-Sleep -Milliseconds 500
Remove-Item $dirC -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "test dir cleaned"
Write-Host "=== test C done ==="
