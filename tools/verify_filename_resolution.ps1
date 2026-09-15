# Multi-site download-filename verification against the real app.
#
# For each URL the script:
#   1. derives the "pseudo name" a naive client would send (URL's last path segment)
#   2. independently derives the EXPECTED name via HTTP headers (Content-Disposition first,
#      else the last redirect target's basename)
#   3. POSTs /api/download to the running app WITH that pseudo filename (the bug-triggering
#      case: only a fix that drops URL-derived pseudo names can pass)
#   4. reports the app task name + the file that actually landed on disk, and PASS/FAIL vs expected
#
# Downloads are throttled (speedLimit) and removed right after the check.
param(
    [string[]]$Urls = @(
        "https://down.wsyhn.com/23_377276",
        "https://down.wsyhn.com/23_377277",
        "https://down.wsyhn.com/23_377275",
        "https://down.wsyhn.com/23_377274",
        "https://codeload.github.com/Hariteki/KokonaDownloader/zip/refs/heads/main",
        "https://api.github.com/repos/Hariteki/KokonaDownloader/zipball/main",
        "https://www.7-zip.org/a/7z2409-x64.exe"
    ),
    [int]$WaitSeconds = 12,
    [long]$SpeedLimit = 262144
)
$exe = "D:\Project\kokonaDown\src\KokonaDownloader.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\KokonaDownloader.exe"
$settings = "$env:APPDATA\KokonaDownloader\settings.json"
$dlDir = Join-Path $env:TEMP "kokona_nametest"
$ua = "Transmission/2.92"

function Get-PseudoName([string]$url) {
    $p = $url
    $qi = $p.IndexOf('?'); if ($qi -ge 0) { $p = $p.Substring(0, $qi) }
    $p = $p.TrimEnd('/')
    $slash = $p.LastIndexOf('/')
    $name = if ($slash -ge 0) { $p.Substring($slash + 1) } else { $p }
    if ([string]::IsNullOrWhiteSpace($name)) { return "download" }
    return [Uri]::UnescapeDataString($name)
}

function Get-ExpectedName([string]$url) {
    $headers = & curl.exe -sS -I -L -A $ua --max-redirs 5 --max-time 30 $url 2>&1
    $cd = $null; $lastLoc = $null
    foreach ($line in $headers) {
        $s = $line.ToString().Trim()
        if ($s -match '^(?i)location:\s*(.+)$') { $lastLoc = $Matches[1].Trim() }
        if ($s -match '^(?i)content-disposition:.*filename\*?=(?:UTF-8'''')?"?([^";]+)"?') { $cd = $Matches[1].Trim() }
    }
    if ($cd) { return @{ Name = [Uri]::UnescapeDataString($cd); Source = "Content-Disposition" } }
    if ($lastLoc) {
        $clean = ($lastLoc -split '\?')[0].TrimEnd('/')
        $slash = $clean.LastIndexOf('/')
        if ($slash -ge 0) { return @{ Name = [Uri]::UnescapeDataString($clean.Substring($slash + 1)); Source = "redirect target" } }
    }
    return @{ Name = (Get-PseudoName $url); Source = "url segment" }
}

# --- app sandbox download dir ---
if (Test-Path $dlDir) { Remove-Item $dlDir -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path $dlDir | Out-Null
$s = Get-Content $settings -Raw | ConvertFrom-Json
$secret = $s.ApiSecret; $port = $s.ApiPort; $origDir = $s.DefaultDownloadDir
$json = [System.IO.File]::ReadAllText($settings)
$json = [regex]::Replace($json, '"DefaultDownloadDir":\s*"[^"]*"', ('"DefaultDownloadDir": "{0}"' -f ($dlDir -replace '\\', '\\')))
[System.IO.File]::WriteAllText($settings, $json)

Get-Process KokonaDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 1200
$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
Start-Sleep -Seconds 9
if ($p.HasExited) { Write-Host "FAIL: app exited"; exit 1 }
$base = "http://127.0.0.1:$port"
$headers = @{ 'X-Kokona-Secret' = $secret }

$pass = 0; $fail = 0
foreach ($url in $Urls) {
    $pseudo = Get-PseudoName $url
    $exp = Get-ExpectedName $url
    Write-Host ""
    Write-Host ("=== {0}" -f $url)
    Write-Host ("    pseudo(sent)='{0}'  expected='{1}' [{2}]" -f $pseudo, $exp.Name, $exp.Source)

    try {
        $body = @{ urls = @($url); filename = $pseudo; speedLimit = $SpeedLimit } | ConvertTo-Json -Compress
        $r = Invoke-RestMethod -Uri "$base/api/download" -Method Post -Headers $headers -Body $body -ContentType 'application/json' -TimeoutSec 30
        $gid = $r.gid
    } catch {
        Write-Host ("    FAIL: POST failed: " + $_.Exception.Message); $fail++; continue
    }

    Start-Sleep -Seconds $WaitSeconds

    $taskName = ""
    try {
        $tasks = Invoke-RestMethod -Uri "$base/api/tasks" -Headers $headers -TimeoutSec 30
        $t = $tasks | Where-Object { $_.gid -eq $gid }
        if ($t) { $taskName = $t.name }
    } catch { }

    $files = @(Get-ChildItem $dlDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -notlike '*.aria2' } | Select-Object -ExpandProperty Name)
    Write-Host ("    app task name='{0}'  on disk: {1}" -f $taskName, ($(if ($files.Count) { $files -join ', ' } else { '<none>' })))

    if ($files -contains $exp.Name) {
        Write-Host ("    PASS: landed '{0}' as expected" -f $exp.Name); $pass++
    } elseif ($files -contains $pseudo -and $exp.Name -ne $pseudo) {
        Write-Host ("    FAIL: landed pseudo name '{0}' instead of '{1}'" -f $pseudo, $exp.Name); $fail++
    } elseif ($files.Count -eq 0) {
        Write-Host ("    WARN: no file landed (server blocked / still connecting)"); $fail++
    } else {
        Write-Host ("    UNKNOWN: expected '{0}', got: {1}" -f $exp.Name, ($files -join ', ')); $fail++
    }

    try { Invoke-RestMethod -Uri "$base/api/tasks/$gid/remove" -Method Post -Headers $headers -TimeoutSec 30 | Out-Null } catch { }
    Start-Sleep -Milliseconds 700
    Get-ChildItem $dlDir -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}

Get-Process KokonaDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
$json = [System.IO.File]::ReadAllText($settings)
$json = [regex]::Replace($json, '"DefaultDownloadDir":\s*"[^"]*"', ('"DefaultDownloadDir": "{0}"' -f ($origDir -replace '\\', '\\')))
[System.IO.File]::WriteAllText($settings, $json)

Write-Host ""
Write-Host ("RESULT: pass={0} fail={1}" -f $pass, $fail)
Write-Host ("download dir restored to {0}" -f $origDir)
