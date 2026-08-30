# Kokona Downloader one-click build script (Windows PowerShell)
# Output:
#   dist\KokonaDownloader  - desktop client (self-contained, run KokonaDownloader.exe directly)
#   dist\KokonaExtension   - browser extension folder (load unpacked in Edge)
# Usage: powershell -ExecutionPolicy Bypass -File .\build.ps1 [-Configuration Release]

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root "dist"

# 优先使用项目本地 SDK（tools\dotnet），不污染系统；其次用系统 PATH 里的 dotnet
$localDotnet = Join-Path $root "tools\dotnet\dotnet.exe"
if (Test-Path $localDotnet) {
    $env:DOTNET_ROOT = Split-Path $localDotnet
    $env:Path = "$env:DOTNET_ROOT;$env:Path"
    if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $root "tools\nuget-cache" }
}

Write-Host "==> [1/3] Building desktop client ($Configuration)..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "src\KokonaDownloader.App\KokonaDownloader.App.csproj") `
    -c $Configuration -r win-x64 --self-contained true `
    -o (Join-Path $dist "KokonaDownloader")
if ($LASTEXITCODE -ne 0) { throw "Client build failed" }
# 运行时图标（icons\tray.ico、icons\tray.png）由 csproj 的 None Include 随发布输出，无需手动复制

Write-Host "==> [2/3] Copying browser extension..." -ForegroundColor Cyan
$extOut = Join-Path $dist "KokonaExtension"
if (Test-Path $extOut) { Remove-Item $extOut -Recurse -Force }
Copy-Item (Join-Path $root "extension") $extOut -Recurse

Write-Host "==> [3/3] Done" -ForegroundColor Green
Write-Host "Client:   $dist\KokonaDownloader\KokonaDownloader.exe"
Write-Host "Extension: $extOut"
