# Opaque 模式 alpha 精确校验（B.4）：
# 在应用窗口后面垫一块纯白窗口，采样"暂无任务"空白区像素。
#   A=0xFF（修复后）→ 像素应等于主题色 (18,31,34)
#   A=0xF2（旧行为）→ 像素 ≈ 0.95*(18,31,34)+0.05*白 = (30,42,45)
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Diag {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint uCmd);
    public static string ZOrderTop(int n) {
        var parts = new System.Collections.Generic.List<string>();
        IntPtr h = GetWindow(IntPtr.Zero, 1); // GW_HWNDFIRST
        int count = 0;
        while (h != IntPtr.Zero && count < n) {
            if (IsWindowVisible(h)) {
                uint pid; GetWindowThreadProcessId(h, out pid);
                parts.Add(pid + ":" + h.ToInt64());
            }
            h = GetWindow(h, 2); // GW_HWNDNEXT
            count++;
        }
        return string.Join(" -> ", parts);
    }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    public static IntPtr LargestVisible(uint pid) {
        IntPtr best = IntPtr.Zero; int bestArea = 0;
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
    public static int[] GetRect(IntPtr h) {
        RECT r; GetWindowRect(h, out r);
        return new int[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
    }
    public static IntPtr RootAt(int x, int y) {
        POINT p; p.X = x; p.Y = y;
        IntPtr h = WindowFromPoint(p);
        if (h == IntPtr.Zero) return IntPtr.Zero;
        IntPtr top = GetAncestor(h, 2);
        return top == IntPtr.Zero ? h : top;
    }
    public static uint PidOf(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return pid; }
}
'@

$exe = "D:\Project\kokonaDown\src\KokonaDownloader.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\KokonaDownloader.exe"
$settings = "$env:APPDATA\KokonaDownloader\settings.json"

# 确保 Opaque 模式
$json = [System.IO.File]::ReadAllText($settings)
$json = [regex]::Replace($json, '"Transparency":\s*"[^"]*"', '"Transparency": "Opaque"')
[System.IO.File]::WriteAllText($settings, $json)

Get-Process KokonaDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 1200
$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
$appPid = [uint32]$p.Id
Start-Sleep -Seconds 9
if ($p.HasExited) { Write-Host "FAIL: process exited"; exit 1 }

$hwnd = [Diag]::LargestVisible($appPid)
$r = [Diag]::GetRect($hwnd)
Write-Host ("main window rect=({0},{1}) size={2}x{3}" -f $r[0], $r[1], $r[2], $r[3])

# 白色垫底窗口：覆盖主窗口 + 边距
$white = New-Object System.Windows.Forms.Form
$white.FormBorderStyle = 'None'
$white.BackColor = [System.Drawing.Color]::White
$white.TopMost = $false
$white.ShowInTaskbar = $false
# 注意：PS 5.1 解析器对 New-Object Type(a+b, c+d) 多参数含算术会误判，统一用 [Type]::new(...)
$white.Size = [System.Drawing.Size]::new($r[2] + 80, $r[3] + 80)
$white.Location = [System.Drawing.Point]::new($r[0] - 40, $r[1] - 40)
$white.Show()
[System.Windows.Forms.Application]::DoEvents()

# 把应用窗口提到最前、白色垫底紧随其后（仅改 z-order，不抢焦点）
Write-Host ("z-order before: " + [Diag]::ZOrderTop(8))
$ok1 = [Diag]::SetWindowPos($hwnd, [IntPtr](-1), -2, -2, 0, 0, 0x0013)  # HWND_TOP + NOSIZE|NOMOVE|NOACTIVATE（本环境 [IntPtr]::MinusOne 返回 null，用强转）
Write-Host ("SetWindowPos(app->TOP) returned: " + $ok1)
$ok2 = [Diag]::SetWindowPos($white.Handle, $hwnd, -2, -2, 0, 0, 0x0013)  # 白色窗口插到应用窗口正下方
Write-Host ("SetWindowPos(white->below app) returned: " + $ok2)
Start-Sleep -Milliseconds 600
Write-Host ("z-order after:  " + [Diag]::ZOrderTop(8))

# 采样点：主窗口"暂无任务"空白区（相对窗口左上角）
$offsets = @(@(300,300), @(400,300), @(500,250), @(250,250), @(350,380), @(450,350))

# 确认采样点归应用所有
$owned = 0
foreach ($o in $offsets) {
    $cover = [Diag]::RootAt($r[0] + $o[0], $r[1] + $o[1])
    if ([Diag]::PidOf($cover) -eq $appPid) { $owned++ }
}
Write-Host ("app owns {0}/{1} sample points" -f $owned, $offsets.Count)
if ($owned -lt $offsets.Count) { Write-Host "WARN: 采样点未全部归应用（白色垫底可能盖住了窗口）"; }

$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = [System.Drawing.Bitmap]::new($b.Width, $b.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$g.Dispose()

# 期望值
$expectFF = @(18, 31, 34)      # A=0xFF：纯主题色
$expectF2 = @(30, 42, 45)     # A=0xF2：95% 混合白底

$maxDevFF = 0; $maxDevF2 = 0
foreach ($o in $offsets) {
    $c = $bmp.GetPixel($r[0] + $o[0], $r[1] + $o[1])
    $dFF = [Math]::Max([Math]::Abs($c.R - $expectFF[0]), [Math]::Max([Math]::Abs($c.G - $expectFF[1]), [Math]::Abs($c.B - $expectFF[2])))
    $dF2 = [Math]::Max([Math]::Abs($c.R - $expectF2[0]), [Math]::Max([Math]::Abs($c.G - $expectF2[1]), [Math]::Abs($c.B - $expectF2[2])))
    if ($dFF -gt $maxDevFF) { $maxDevFF = $dFF }
    if ($dF2 -gt $maxDevF2) { $maxDevF2 = $dF2 }
    Write-Host ("  sample({0},{1}) = {2},{3},{4}   devFF={5} devF2={6}" -f $o[0], $o[1], $c.R, $c.G, $c.B, $dFF, $dF2)
}
$bmp.Dispose()

Write-Host ""
if ($maxDevFF -le 6) {
    Write-Host "PASS: Opaque 像素与主题色 (18,31,34) 偏差 <= 6 → A=0xFF 实心不透明（B.4 修复生效）"
} elseif ($maxDevF2 -le $maxDevFF + 6) {
    Write-Host "FAIL: 像素更接近 95% 混合值 (30,42,45) → 仍是 A=0xF2，B.4 未生效"
} else {
    Write-Host ("UNKNOWN: devFF={0} devF2={1}（可能采样到 UI 内容或壁纸干扰，请人工看截图）" -f $maxDevFF, $maxDevF2)
}

# 清理：关掉白色垫底窗口、退出应用
$white.Close()
Get-Process KokonaDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host "done"
