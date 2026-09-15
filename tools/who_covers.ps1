# Who covers the app window? Report, for a few screen points, the owning window's
# pid / process name / title, so we know what to move aside for a faithful screenshot.
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Cov {
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static string At(int x, int y) {
        POINT p; p.X = x; p.Y = y;
        IntPtr h = GetAncestor(WindowFromPoint(p), 2);
        if (h == IntPtr.Zero) return "none";
        uint pid; GetWindowThreadProcessId(h, out pid);
        var sb = new StringBuilder(256); GetWindowTextW(h, sb, sb.Capacity);
        RECT r; GetWindowRect(h, out r);
        return string.Format("hwnd={0} pid={1} rect=({2},{3})-({4},{5}) title='{6}'", h, pid, r.Left, r.Top, r.Right, r.Bottom, sb.ToString());
    }

    public static string Foreground() {
        IntPtr h = GetForegroundWindow();
        uint pid; GetWindowThreadProcessId(h, out pid);
        var sb = new StringBuilder(256); GetWindowTextW(h, sb, sb.Capacity);
        return string.Format("hwnd={0} pid={1} title='{2}'", h, pid, sb.ToString());
    }
}
'@

foreach ($pt in @(@(160,142), @(300,200), @(500,300), @(700,400), @(200,500))) {
    $info = [Cov]::At($pt[0], $pt[1])
    Write-Host ("point ({0},{1}): {2}" -f $pt[0], $pt[1], $info)
    $pid = ($info -split 'pid=')[1] -split ' ' | Select-Object -First 1
    $proc = Get-Process -Id ([int]$pid) -ErrorAction SilentlyContinue
    if ($proc) { Write-Host ("   process: {0}" -f $proc.ProcessName) }
}
Write-Host ""
Write-Host ("foreground: {0}" -f [Cov]::Foreground())
$fgPid = ([Cov]::Foreground() -split 'pid=')[1] -split ' ' | Select-Object -First 1
$fgProc = Get-Process -Id ([int]$fgPid) -ErrorAction SilentlyContinue
if ($fgProc) { Write-Host ("foreground process: {0}" -f $fgProc.ProcessName) }
