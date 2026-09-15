# Transparency verification v8 (final).
# The IDE window (TRAE SOLO CN, maximized) covers the whole screen, and foreground stealing
# is blocked, so the app window can never come to the front by itself. This script minimizes
# whatever covers the sample points, verifies the app owns them, captures, then restores.
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class Diag {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

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

    public static uint PidOf(IntPtr h) {
        uint pid; GetWindowThreadProcessId(h, out pid);
        return pid;
    }
}
'@

$exe = "D:\Project\kokonaDown\src\KokonaDownloader.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\KokonaDownloader.exe"
$outDir = "D:\Project\kokonaDown\artifacts\transparency"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$settings = "$env:APPDATA\KokonaDownloader\settings.json"

$offsets = @(@(30,12), @(200,12), @(600,12), @(30,40), @(30,90), @(200,140), @(700,90), @(60,420), @(450,500), @(820,450))
$SW_MINIMIZE = 6; $SW_RESTORE = 9

function Grab() {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $g.Dispose()
    return $bmp
}

function SampleAt($bmp, [int]$x, [int]$y) {
    $vals = @()
    foreach ($o in $offsets) {
        $c = $bmp.GetPixel($x + $o[0], $y + $o[1])
        $vals += ("{0},{1},{2}" -f $c.R, $c.G, $c.B)
    }
    return $vals
}

function Lum($vals, [int]$i) {
    $p = $vals[$i] -split ','
    return [int](([int]$p[0] + [int]$p[1] + [int]$p[2]) / 3)
}

function SetMode([string]$mode) {
    $json = [System.IO.File]::ReadAllText($settings)
    $json = [regex]::Replace($json, '"Transparency":\s*"[^"]*"', '"Transparency": "' + $mode + '"')
    [System.IO.File]::WriteAllText($settings, $json)
}

$window = @{}
$desktop = @{}
$minimized = New-Object System.Collections.Generic.List[IntPtr]

foreach ($mode in @("Opaque", "Frosted", "BlackTransparent")) {
    Get-Process KokonaDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 1200
    SetMode $mode
    $p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
    $appPid = [uint32]$p.Id
    Start-Sleep -Seconds 9
    if ($p.HasExited) { Write-Host "$mode : process exited"; continue }
    $hwnd = [Diag]::LargestVisible($appPid)
    if ($hwnd -eq [IntPtr]::Zero) { Write-Host "$mode : no visible window"; continue }
    $r = [Diag]::GetRect($hwnd)

    # move aside whatever covers the app window, then confirm the app owns the sample points
    $owned = 0
    for ($pass = 0; $pass -lt 6; $pass++) {
        $owned = 0
        foreach ($o in $offsets) {
            $cover = [Diag]::RootAt($r[0] + $o[0], $r[1] + $o[1])
            $cpid = [Diag]::PidOf($cover)
            if ($cpid -eq $appPid) { $owned++; continue }
            if ($cover -ne [IntPtr]::Zero -and $cpid -ne 0) {
                if (-not $minimized.Contains($cover)) { $minimized.Add($cover) }
                [Diag]::ShowWindow($cover, $SW_MINIMIZE) | Out-Null
            }
        }
        if ($owned -eq $offsets.Count) { break }
        Start-Sleep -Milliseconds 700
    }
    Write-Host ("{0,-17} rect=({1},{2}) app owns {3}/{4} sample points" -f $mode, $r[0], $r[1], $owned, $offsets.Count)

    $bmp = Grab
    $crop = New-Object System.Drawing.Rectangle($r[0], $r[1], $r[2], $r[3])
    $win = $bmp.Clone($crop, $bmp.PixelFormat)
    $win.Save("$outDir\mode-$mode.png")
    $win.Dispose()
    $window[$mode] = SampleAt $bmp $r[0] $r[1]
    $bmp.Dispose()

    Get-Process KokonaDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 1500
    $b2 = Grab
    $desktop[$mode] = SampleAt $b2 $r[0] $r[1]
    $b2.Dispose()

    $lums = (0..($offsets.Count - 1) | ForEach-Object { Lum $window[$mode] $_ }) -join ' '
    $dlums = (0..($offsets.Count - 1) | ForEach-Object { Lum $desktop[$mode] $_ }) -join ' '
    Write-Host ("                  window  lum: {0}" -f $lums)
    Write-Host ("                  desktop lum: {0}" -f $dlums)
}

# restore everything we minimized
foreach ($h in $minimized) { [Diag]::ShowWindow($h, $SW_RESTORE) | Out-Null }

Write-Host ""
Write-Host "point |  desktop |  Opaque | Frosted | BlackTransparent"
for ($i = 0; $i -lt $offsets.Count; $i++) {
    Write-Host ("  {0,3} | {1,8} | {2,7} | {3,7} | {4,16}" -f $i,
        (Lum $desktop["Opaque"] $i), (Lum $window["Opaque"] $i), (Lum $window["Frosted"] $i), (Lum $window["BlackTransparent"] $i))
}
Write-Host ""
Write-Host "shots: $outDir"
