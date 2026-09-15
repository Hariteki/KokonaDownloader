# Probe splash window on non-default launch paths. Usage: probe_splash_args.ps1 <arg1> [arg2] ...
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class WinProbe2 {
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

$exe = "D:\Project\kokonaDown\src\KokonaDownloader.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\KokonaDownloader.exe"
Get-Process KokonaDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$launchArgs = @($args)
Write-Output ("launch args: [" + ($launchArgs -join " | ") + "]")

$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList $launchArgs -PassThru
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$seen = @{}
while ($sw.ElapsedMilliseconds -lt 10000) {
    $t = $sw.ElapsedMilliseconds
    foreach ($line in [WinProbe2]::Snapshot([uint32]$p.Id)) {
        if (-not $seen.ContainsKey($line)) {
            $seen[$line] = $t
            Write-Output ("[{0,5} ms] {1}" -f $t, $line)
        }
    }
    Start-Sleep -Milliseconds 120
}
Write-Output "--- probe done, process alive: $(-not $p.HasExited) ---"
Get-Process KokonaDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output "app killed"
