# C.1/C.2 验证：GET/POST /api/settings 的 transparency 与 minimizeToTrayOnClose 字段，
# 以及"未知字段不再 500"（线程安全修复）。
$exe = "D:\Project\kokonaDown\src\KokonaDownloader.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\KokonaDownloader.exe"
$settings = "$env:APPDATA\KokonaDownloader\settings.json"
$secret = (Get-Content $settings -Raw | ConvertFrom-Json).ApiSecret
$base = "http://127.0.0.1:16800"

# 记录原始值，测完恢复
$orig = Get-Content $settings -Raw | ConvertFrom-Json
$origTransparency = $orig.Transparency
$origMinimize = $orig.MinimizeToTrayOnClose

Get-Process KokonaDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 1200
$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
Start-Sleep -Seconds 9
if ($p.HasExited) { Write-Host "FAIL: process exited"; exit 1 }

function Invoke-Api([string]$method, [string]$path, $body = $null) {
    $headers = @{ 'X-Kokona-Secret' = $script:secret }
    if ($body -ne $null) {
        return Invoke-RestMethod -Uri "$script:base$path" -Method $method -Headers $headers -Body ($body | ConvertTo-Json -Depth 5) -ContentType 'application/json'
    }
    return Invoke-RestMethod -Uri "$script:base$path" -Method $method -Headers $headers
}

$fail = 0

# 1. GET 应包含新字段
$get = Invoke-Api GET '/api/settings'
Write-Host ("GET fields: " + (($get.PSObject.Properties.Name) -join ', '))
if ($get.PSObject.Properties.Name -contains 'transparency') { Write-Host "PASS: GET 返回 transparency=$($get.transparency)" } else { Write-Host "FAIL: GET 缺 transparency"; $fail++ }
if ($get.PSObject.Properties.Name -contains 'minimizeToTrayOnClose') { Write-Host "PASS: GET 返回 minimizeToTrayOnClose=$($get.minimizeToTrayOnClose)" } else { Write-Host "FAIL: GET 缺 minimizeToTrayOnClose"; $fail++ }

# 2. POST transparency（原 500 场景）
try {
    $r = Invoke-Api POST '/api/settings' @{ transparency = 'frosted' }
    if ($r.ok) { Write-Host "PASS: POST transparency=frosted -> ok" } else { Write-Host "FAIL: POST transparency 未 ok"; $fail++ }
} catch { Write-Host ("FAIL: POST transparency 异常: " + $_.Exception.Message); $fail++ }
Start-Sleep -Milliseconds 800
$now = Get-Content $settings -Raw | ConvertFrom-Json
if ($now.Transparency -eq 'Frosted') { Write-Host "PASS: settings.json Transparency=Frosted 已持久化" } else { Write-Host ("FAIL: settings.json Transparency=" + $now.Transparency); $fail++ }

# 3. POST minimizeToTrayOnClose
try {
    $r = Invoke-Api POST '/api/settings' @{ minimizeToTrayOnClose = $false }
    if ($r.ok) { Write-Host "PASS: POST minimizeToTrayOnClose=false -> ok" } else { Write-Host "FAIL: POST minimize 未 ok"; $fail++ }
} catch { Write-Host ("FAIL: POST minimize 异常: " + $_.Exception.Message); $fail++ }
Start-Sleep -Milliseconds 800
$now = Get-Content $settings -Raw | ConvertFrom-Json
if ($now.MinimizeToTrayOnClose -eq $false) { Write-Host "PASS: settings.json MinimizeToTrayOnClose=false 已持久化" } else { Write-Host ("FAIL: settings.json MinimizeToTrayOnClose=" + $now.MinimizeToTrayOnClose); $fail++ }

# 4. 未知字段（文档记录曾返回 500）
try {
    $r = Invoke-Api POST '/api/settings' @{ someUnknownField = 'x' }
    if ($r.ok) { Write-Host "PASS: POST 未知字段 -> ok（不再 500）" } else { Write-Host "FAIL: POST 未知字段未 ok"; $fail++ }
} catch { Write-Host ("FAIL: POST 未知字段异常: " + $_.Exception.Message); $fail++ }

# 5. 应用仍存活且 GET 反映新值
Start-Sleep -Milliseconds 500
if ($p.HasExited) { Write-Host "FAIL: 应用在 API 调用后退出"; $fail++ } else {
    $get2 = Invoke-Api GET '/api/settings'
    if ($get2.transparency -eq 'frosted') { Write-Host "PASS: GET 反映 transparency=frosted" } else { Write-Host ("FAIL: GET transparency=" + $get2.transparency); $fail++ }
}

# 恢复原值
try {
    Invoke-Api POST '/api/settings' @{ transparency = $origTransparency; minimizeToTrayOnClose = $origMinimize } | Out-Null
    Start-Sleep -Milliseconds 500
    $restored = Get-Content $settings -Raw | ConvertFrom-Json
    Write-Host ("已恢复: Transparency=" + $restored.Transparency + " MinimizeToTrayOnClose=" + $restored.MinimizeToTrayOnClose)
} catch { Write-Host ("WARN: 恢复设置失败: " + $_.Exception.Message) }

Get-Process KokonaDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host ""
if ($fail -eq 0) { Write-Host "ALL PASS" } else { Write-Host ("FAILED: " + $fail + " 项") }
exit $fail
