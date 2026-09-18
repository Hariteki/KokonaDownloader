# Test D: launch from the user's actual desktop location (single-file folder) and verify splash + API
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class WinProbe4 {
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

# /api/ping is the only unauthenticated endpoint, so no secret is needed here.
# (An earlier revision hardcoded the user's real ApiSecret in this file and shipped it
#  to the public repo; removed. Read apiSecret from settings.json if a call needs auth.)
$exe = "C:\Users\lumin\Desktop\KokonaSingleFile\KokonaDownloader.exe"

Get-Process KokonaDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

Write-Host "=== test D: launch from desktop folder ==="
$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$seen = @{}
$splashSeen = $false
while ($sw.ElapsedMilliseconds -lt 9000) {
    foreach ($line in [WinProbe4]::Snapshot([uint32]$p.Id)) {
        if ($line -match "visible=True size=(336|420)x(336|420)") { $splashSeen = $true }
        if (-not $seen.ContainsKey($line)) {
            $seen[$line] = $sw.ElapsedMilliseconds
            Write-Host ("[{0,5} ms] {1}" -f $sw.ElapsedMilliseconds, $line)
        }
    }
    Start-Sleep -Milliseconds 120
}
if ($splashSeen) { Write-Host "PASS: splash visible from desktop launch" } else { Write-Host "FAIL: splash not seen" }

$ping = $null
for ($i = 0; $i -lt 60; $i++) {
    try {
        $r = Invoke-WebRequest -Uri "http://127.0.0.1:16800/api/ping" -UseBasicParsing -TimeoutSec 2
        if ($r.StatusCode -eq 200) { $ping = $r.Content; break }
    } catch {}
    Start-Sleep -Milliseconds 500
}
Write-Host ("ping: " + $ping)
if ($null -ne $ping -and $ping -match '"version":"1\.0\.8"') { Write-Host "PASS: desktop launch works, ping 200 v1.0.8" } else { Write-Host "FAIL: desktop launch broken" }

Get-Process KokonaDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host "app stopped; test D done"
