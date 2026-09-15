# End-to-end filename check through the real app: POST /api/download (extension-style, no filename)
# then report the task name the app shows and the file that actually lands on disk.
param(
    [Parameter(Mandatory = $true)][string[]]$Urls,
    [int]$WaitSeconds = 20,
    [string]$FileName = "",
    [string]$AppExe = "D:\Project\kokonaDown\src\KokonaDownloader.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\KokonaDownloader.exe"
)
$exe = $AppExe
$settings = "$env:APPDATA\KokonaDownloader\settings.json"
$dlDir = Join-Path $env:TEMP "kokona_nametest"
if (Test-Path $dlDir) { Remove-Item $dlDir -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path $dlDir | Out-Null

# 1) point downloads at the temp dir, and grab the secret
$s = Get-Content $settings -Raw | ConvertFrom-Json
$secret = $s.ApiSecret
$port = $s.ApiPort
$origDir = $s.DefaultDownloadDir
$json = [System.IO.File]::ReadAllText($settings)
$json = [regex]::Replace($json, '"DefaultDownloadDir":\s*"[^"]*"', ('"DefaultDownloadDir": "{0}"' -f ($dlDir -replace '\\', '\\')))
[System.IO.File]::WriteAllText($settings, $json)
Write-Host "download dir -> $dlDir (was $origDir)"

Get-Process KokonaDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 1200
$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
Start-Sleep -Seconds 9
if ($p.HasExited) { Write-Host "FAIL: app exited"; exit 1 }

$base = "http://127.0.0.1:$port"
$headers = @{ 'X-Kokona-Secret' = $secret }

foreach ($url in $Urls) {
    Write-Host ""
    Write-Host ("=== {0}" -f $url)
    try {
        $body = @{ urls = @($url) }
        if ($FileName -ne "") { $body.filename = $FileName }
        $body = $body | ConvertTo-Json -Compress
        $r = Invoke-RestMethod -Uri "$base/api/download" -Method Post -Headers $headers -Body $body -ContentType 'application/json' -TimeoutSec 30
        Write-Host ("  POST /api/download (filename='{0}') -> ok={1} gid={2}" -f $FileName, $r.ok, $r.gid)
        $gid = $r.gid
    } catch {
        Write-Host ("  POST failed: " + $_.Exception.Message)
        continue
    }

    Start-Sleep -Seconds $WaitSeconds

    # task name as the app reports it
    try {
        $tasks = Invoke-RestMethod -Uri "$base/api/tasks" -Headers $headers -TimeoutSec 30
        $t = $tasks | Where-Object { $_.gid -eq $gid }
        if ($t) { Write-Host ("  app task name = '{0}'  state={1}" -f $t.name, $t.state) }
        else { Write-Host "  task not found in /api/tasks" }
    } catch { Write-Host ("  /api/tasks failed: " + $_.Exception.Message) }

    Write-Host "  files on disk:"
    Get-ChildItem $dlDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike '*.aria2' } |
        ForEach-Object { Write-Host ("    {0}  ({1} bytes)" -f $_.Name, $_.Length) }

    # clean up: remove task + delete partial file
    try { Invoke-RestMethod -Uri "$base/api/tasks/$gid/remove" -Method Post -Headers $headers -TimeoutSec 30 | Out-Null } catch { }
    Start-Sleep -Milliseconds 800
    Get-ChildItem $dlDir -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}

Get-Process KokonaDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
# restore original download dir
$json = [System.IO.File]::ReadAllText($settings)
$json = [regex]::Replace($json, '"DefaultDownloadDir":\s*"[^"]*"', ('"DefaultDownloadDir": "{0}"' -f ($origDir -replace '\\', '\\')))
[System.IO.File]::WriteAllText($settings, $json)
Write-Host ""
Write-Host "download dir restored to $origDir"
