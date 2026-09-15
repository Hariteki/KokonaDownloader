# Dump UIA tree to locate the transparency MenuFlyout items (ASCII + BOM safe)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class W {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
'@

$exe = "D:\Project\kokonaDown\src\KokonaDownloader.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\KokonaDownloader.exe"
Get-Process KokonaDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
Start-Sleep -Seconds 7
$p.Refresh()
$hwnd = $p.MainWindowHandle
Write-Host "hwnd=$hwnd"
[W]::SetWindowPos($hwnd, [IntPtr]::Zero, 60, 60, 900, 560, 0x0040) | Out-Null
[W]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 1500

function FindByName([string]$name, $root) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

$win = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$btn = FindByName "窗口透明度" $win
Write-Host "button found: $($btn -ne $null)"
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 1500

Write-Host ""
Write-Host "=== descendants of MAIN WINDOW (name-bearing) ==="
$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
for ($i = 0; $i -lt $all.Count; $i++) {
    $e = $all.Item($i)
    $n = $e.Current.Name
    if ($n) { Write-Host ("  [{0}] {1} | name='{2}'" -f $i, $e.Current.ControlType.ProgrammaticName, $n) }
}

Write-Host ""
Write-Host "=== ROOT desktop: elements whose name is a known menu label ==="
$rootEl = [System.Windows.Automation.AutomationElement]::RootElement
foreach ($label in @("不透明", "磨砂透明", "黑色纯透明")) {
    $hit = FindByName $label $rootEl
    if ($hit) {
        $r = $hit.Current.BoundingRectangle
        $pats = $hit.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }
        Write-Host ("  '{0}' FOUND type={1} rect={2},{3} {4}x{5} patterns={6}" -f $label, $hit.Current.ControlType.ProgrammaticName, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height, ($pats -join ','))
    } else {
        Write-Host ("  '{0}' NOT FOUND on desktop root" -f $label)
    }
}
