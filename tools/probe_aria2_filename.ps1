# Ask aria2 (with the app's own options) what filename it resolves for a URL.
# Shows the resolved files[0].path, so we can see whether a redirect target or the
# URL's last segment wins. Downloads are throttled and removed immediately.
param(
    [Parameter(Mandatory = $true)][string[]]$Urls,
    [string]$Aria2 = "D:\Project\kokonaDown\src\KokonaDownloader.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\aria2c.exe",
    [string]$UserAgent = "Transmission/2.92",
    [switch]$WithContentDisposition,
    [string]$Out = ""
)

$rpcPort = 16999
$secret = "nameprobe"
$dir = Join-Path $env:TEMP "aria2_nameprobe"
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path $dir | Out-Null

Get-Process aria2c -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$extra = @()
if ($WithContentDisposition) { $extra += "--content-disposition=true" }
if ($Out -ne "") { $extra += "--out=$Out" }

$args = @(
    "--enable-rpc=true", "--rpc-listen-port=$rpcPort", "--rpc-secret=$secret",
    "--rpc-listen-all=false", "--dir=$dir", "--user-agent=$UserAgent",
    "--file-allocation=none", "--max-download-limit=20K", "--max-tries=1",
    "--auto-file-renaming=false", "--allow-overwrite=true",
    "--console-log-level=warn", "--quiet=true", "--no-conf=true"
) + $extra

$p = Start-Process -FilePath $Aria2 -ArgumentList $args -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2

function Rpc([string]$method, $params) {
    $body = @{ jsonrpc = "2.0"; id = "probe"; method = $method; params = @("token:$secret") + $params } | ConvertTo-Json -Depth 8 -Compress
    $r = Invoke-RestMethod -Uri "http://127.0.0.1:$rpcPort/jsonrpc" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 30
    return $r
}

Write-Host ("aria2 options: user-agent={0} content-disposition={1}" -f $UserAgent, $WithContentDisposition.IsPresent)
Write-Host ""

foreach ($url in $Urls) {
    try {
        $add = Rpc "aria2.addUri" @(, @($url))
        if ($add.error) { Write-Host ("{0}`n    RPC ERROR: {1}" -f $url, ($add.error | ConvertTo-Json -Compress)); continue }
        $gid = $add.result
        Start-Sleep -Milliseconds 2500
        $st = Rpc "aria2.tellStatus" @($gid, @("status", "errorCode", "errorMessage", "files", "dir"))
        if ($st.error) { Write-Host ("{0}`n    RPC ERROR: {1}" -f $url, ($st.error | ConvertTo-Json -Compress)); continue }
        $path = $null
        if ($st.result.files -and $st.result.files.Count -gt 0) { $path = $st.result.files[0].path }
        $name = if ($path) { Split-Path $path -Leaf } else { "<none>" }
        Write-Host ("{0}" -f $url)
        Write-Host ("    status={0} errorCode={1} -> resolved name = '{2}'" -f $st.result.status, $st.result.errorCode, $name)
        if ($st.result.errorMessage) { Write-Host ("    errorMessage={0}" -f $st.result.errorMessage) }
        Rpc "aria2.remove" @($gid) | Out-Null
        Rpc "aria2.removeDownloadResult" @($gid) | Out-Null
    } catch {
        Write-Host ("{0}`n    EXCEPTION: {1}" -f $url, $_.Exception.Message)
    }
}

Write-Host ""
Write-Host "--- files in download dir ---"
Get-ChildItem $dir -File -ErrorAction SilentlyContinue | Select-Object Name, Length | Format-Table -AutoSize

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Get-Process aria2c -ErrorAction SilentlyContinue | Stop-Process -Force
