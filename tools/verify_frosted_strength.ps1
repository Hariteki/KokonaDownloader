# 磨砂浓度滑块验证：
# 阶段 A：白底遮挡下，FrostedStrength = 0.0 / 0.5 / 1.0 三档采样窗口像素亮度，
#         期望单调递减（更透明→更亮，更不透明→更暗），且相邻档位差 >= 25。
# 阶段 B：UIA 打开透明度菜单，确认 Flyout 内含 Slider 且磨砂模式下为启用状态。
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Diag2 {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
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

function Set-Settings {
    param([string]$mode, [double]$strength)
    $json = [System.IO.File]::ReadAllText($settings)
    $json = [regex]::Replace($json, '"Transparency":\s*"[^"]*"', ('"Transparency": "{0}"' -f $mode))
    if ($json -match '"FrostedStrength"') {
        $json = [regex]::Replace($json, '"FrostedStrength":\s*[0-9.]+', ('"FrostedStrength": {0:F2}' -f $strength))
    } else {
        $json = $json -replace ('"Transparency": "{0}"' -f $mode), ('"Transparency": "{0}", "FrostedStrength": {1:F2}' -f $mode, $strength)
    }
    [System.IO.File]::WriteAllText($settings, $json)
}

function Stop-App {
    Get-Process KokonaDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 1000
}

# ════ 阶段 A：三档浓度白底采样 ════
Write-Host "=== phase A: white-backdrop sampling at 3 strengths ==="
$offsets = @(@(300,300), @(400,300), @(500,250), @(250,250), @(350,380), @(450,350))
$lumByStrength = @{}

foreach ($s in @(0.0, 0.5, 1.0)) {
    Set-Settings -mode "Frosted" -strength $s
    Stop-App
    $p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
    $appPid = [uint32]$p.Id
    Start-Sleep -Seconds 9
    if ($p.HasExited) { Write-Host "FAIL: process exited at strength $s"; exit 1 }

    $hwnd = [Diag2]::LargestVisible($appPid)
    $r = [Diag2]::GetRect($hwnd)

    $white = New-Object System.Windows.Forms.Form
    $white.FormBorderStyle = 'None'
    $white.BackColor = [System.Drawing.Color]::White
    $white.TopMost = $false
    $white.ShowInTaskbar = $false
    $white.Size = [System.Drawing.Size]::new($r[2] + 80, $r[3] + 80)
    $white.Location = [System.Drawing.Point]::new($r[0] - 40, $r[1] - 40)
    $white.Show()
    [System.Windows.Forms.Application]::DoEvents()
    [Diag2]::SetWindowPos($hwnd, [IntPtr](-1), -2, -2, 0, 0, 0x0013) | Out-Null
    [Diag2]::SetWindowPos($white.Handle, $hwnd, -2, -2, 0, 0, 0x0013) | Out-Null
    Start-Sleep -Milliseconds 600

    $owned = 0
    foreach ($o in $offsets) {
        if ([Diag2]::PidOf([Diag2]::RootAt($r[0] + $o[0], $r[1] + $o[1])) -eq $appPid) { $owned++ }
    }

    $b = [System.Drawing.Bitmap]::new([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width, [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.CopyFromScreen([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Location, [System.Drawing.Point]::Empty, $b.Size)
    $g.Dispose()

    $sum = 0
    foreach ($o in $offsets) {
        $c = $b.GetPixel($r[0] + $o[0], $r[1] + $o[1])
        $lum = [int](($c.R + $c.G + $c.B) / 3)
        $sum += $lum
        Write-Host ("  s={0:F1} sample({1},{2}) = {3},{4},{5} lum={6}" -f $s, $o[0], $o[1], $c.R, $c.G, $c.B, $lum)
    }
    $b.Dispose()
    $white.Close()
    $avg = [Math]::Round($sum / $offsets.Count, 1)
    $lumByStrength[$s] = $avg
    Write-Host ("strength={0:F1} avgLum={1} (app owns {2}/{3} points)" -f $s, $avg, $owned, $offsets.Count)
    Stop-App
}

$l0 = $lumByStrength[0.0]; $l5 = $lumByStrength[0.5]; $l1 = $lumByStrength[1.0]
Write-Host ""
if ($l0 -gt $l5 -and $l5 -gt $l1 -and ($l0 - $l5) -ge 25 -and ($l5 - $l1) -ge 25) {
    Write-Host ("PASS: luminance monotonic 0.0={0} > 0.5={1} > 1.0={2} (deltas >= 25)" -f $l0, $l5, $l1)
} else {
    Write-Host ("FAIL: expected L(0.0)>{0} > L(0.5)>{1} > L(1.0) with deltas>=25, got {2}/{3}/{4}" -f $l5, $l1, $l0, $l5, $l1)
}

# ════ 阶段 B：UIA 检查菜单里的滑块 ════
Write-Host ""
Write-Host "=== phase B: UIA flyout slider check ==="
Set-Settings -mode "Frosted" -strength 0.5
Stop-App
$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
$appPid = [uint32]$p.Id
Start-Sleep -Seconds 9

$root = [System.Windows.Automation.AutomationElement]::RootElement
# 注意：ProcessIdProperty 条件值必须是 Int32，传 uint32 会抛 "must be 'Int32'"
$procCond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$appPid)
$main = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $procCond)
if ($null -eq $main) { Write-Host "FAIL: main window not found via UIA"; Stop-App; exit 1 }

$nameCond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, "窗口透明度")
$btn = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond)
if ($null -eq $btn) { Write-Host "FAIL: transparency button not found via UIA"; Stop-App; exit 1 }

$invoke = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$invoke.Invoke()
Start-Sleep -Milliseconds 1000

$sliderFound = $false
$sliderEnabled = $null
foreach ($child in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $procCond)) {
    $sliders = $child.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Slider))
    if ($sliders.Count -gt 0) {
        $sliderFound = $true
        $sliderEnabled = $sliders[0].Current.IsEnabled
        Write-Host ("flyout window found: {0} slider(s), enabled={1}" -f $sliders.Count, $sliderEnabled)
        break
    }
}
if ($sliderFound -and $sliderEnabled) {
    Write-Host "PASS: slider present in transparency flyout and enabled (Frosted mode)"
} else {
    Write-Host ("FAIL: slider found={0} enabled={1}" -f $sliderFound, $sliderEnabled)
}

Stop-App
Write-Host "done"
