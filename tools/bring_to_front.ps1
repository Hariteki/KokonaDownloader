# Bring the app's main window to the foreground.
# Foreground stealing is blocked for a non-foreground caller, so attach our input queue to
# the current foreground window's thread before calling SetForegroundWindow.
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Fg {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint attachTo, bool fAttach);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static IntPtr LargestVisible(uint pid) {
        IntPtr best = IntPtr.Zero;
        int bestArea = 0;
        EnumWindows((hw, l) => {
            uint p; GetWindowThreadProcessId(hw, out p);
            if (p != pid || !IsWindowVisible(hw)) return true;
            RECT r; GetWindowRect(hw, out r);
            int area = (r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = hw; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    public static string Title(IntPtr h) {
        var sb = new StringBuilder(256);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static bool ForceForeground(IntPtr target) {
        IntPtr fg = GetForegroundWindow();
        uint fgPid;
        // NOTE: PS 5.1 Add-Type compiles with the legacy C# compiler: no discard (out _)
        uint fgThread = GetWindowThreadProcessId(fg, out fgPid);
        uint myThread = GetCurrentThreadId();
        bool attached = false;
        if (fgThread != 0 && fgThread != myThread) attached = AttachThreadInput(myThread, fgThread, true);
        ShowWindow(target, 9);            // SW_RESTORE
        BringWindowToTop(target);
        bool ok = SetForegroundWindow(target);
        if (attached) AttachThreadInput(myThread, fgThread, false);
        return ok;
    }
}
'@

$p = Get-Process KokonaDownloader -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { Write-Host "app is not running"; exit 1 }
$hwnd = [Fg]::LargestVisible([uint32]$p.Id)
if ($hwnd -eq [IntPtr]::Zero) { Write-Host "no visible window"; exit 1 }
$r = New-Object Fg+RECT
[Fg]::GetWindowRect($hwnd, [ref]$r) | Out-Null
Write-Host ("pid={0} hwnd={1} title='{2}' rect=({3},{4})-({5},{6})" -f $p.Id, $hwnd, ([Fg]::Title($hwnd)), $r.Left, $r.Top, $r.Right, $r.Bottom)
$ok = [Fg]::ForceForeground($hwnd)
Write-Host ("SetForegroundWindow => {0}" -f $ok)
Start-Sleep -Milliseconds 600
$fg = [Fg]::GetForegroundWindow()
Write-Host ("foreground now: hwnd={0} title='{1}'" -f $fg, ([Fg]::Title($fg)))
