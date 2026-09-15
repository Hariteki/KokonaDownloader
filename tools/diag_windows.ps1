# Diagnose: dump every top-level window of the app process (rect, visible, title),
# then move the largest one and dump again to confirm the move took effect.
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class Diag {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static string[] Dump(uint pid) {
        var list = new List<string>();
        EnumWindows((hw, l) => {
            uint p; GetWindowThreadProcessId(hw, out p);
            if (p != pid) return true;
            RECT r; GetWindowRect(hw, out r);
            var sb = new StringBuilder(256); GetWindowTextW(hw, sb, sb.Capacity);
            list.Add(string.Format("hwnd={0} vis={1} area={2} rect=({3},{4})-({5},{6}) title='{7}'",
                hw, IsWindowVisible(hw), (r.Right - r.Left) * (r.Bottom - r.Top), r.Left, r.Top, r.Right, r.Bottom, sb.ToString()));
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }

    public static IntPtr LargestVisible(uint pid, out int w, out int h) {
        IntPtr best = IntPtr.Zero; int bestArea = 0; w = 0; h = 0;
        EnumWindows((hw, l) => {
            uint p; GetWindowThreadProcessId(hw, out p);
            if (p != pid || !IsWindowVisible(hw)) return true;
            RECT r; GetWindowRect(hw, out r);
            int cw = r.Right - r.Left, ch = r.Bottom - r.Top;
            if (cw * ch > bestArea) { bestArea = cw * ch; best = hw; w = cw; h = ch; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    public static string Rect(IntPtr h) {
        RECT r; GetWindowRect(h, out r);
        return string.Format("({0},{1})-({2},{3}) vis={4}", r.Left, r.Top, r.Right, r.Bottom, IsWindowVisible(h));
    }
}
'@

$exe = "D:\Project\kokonaDown\src\KokonaDownloader.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\KokonaDownloader.exe"
Get-Process KokonaDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 1200
$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
Start-Sleep -Seconds 9
if ($p.HasExited) { Write-Host "process exited"; exit 1 }

Write-Host "=== windows after 9s ==="
[Diag]::Dump([uint32]$p.Id) | ForEach-Object { Write-Host "  $_" }

$w = 0; $h = 0
$hwnd = [Diag]::LargestVisible([uint32]$p.Id, [ref]$w, [ref]$h)
Write-Host "largest visible: hwnd=$hwnd size=${w}x${h} rect=$([Diag]::Rect($hwnd))"

[Diag]::SetWindowPos($hwnd, [IntPtr]::Zero, 60, 60, 900, 560, 0x0040) | Out-Null
Start-Sleep -Milliseconds 2000
Write-Host "=== windows 2s after SetWindowPos ==="
[Diag]::Dump([uint32]$p.Id) | ForEach-Object { Write-Host "  $_" }
