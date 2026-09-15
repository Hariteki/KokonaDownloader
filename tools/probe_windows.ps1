# 启动瞬间轮询顶层窗口，记录启动动画窗口是否出现、尺寸与存续时间
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class WinProbe {
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

$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$seen = @{}
while ($sw.ElapsedMilliseconds -lt 8000) {
    $t = $sw.ElapsedMilliseconds
    foreach ($line in [WinProbe]::Snapshot([uint32]$p.Id)) {
        $key = $line
        if (-not $seen.ContainsKey($key)) {
            $seen[$key] = $t
            Write-Output ("[{0,5} ms] {1}" -f $t, $line)
        }
    }
    Start-Sleep -Milliseconds 120
}
Write-Output "--- probe done, process alive: $(-not $p.HasExited) ---"
