# 透明度菜单视觉证据 + 滑块启用态检查：
#   Frosted 模式 → 截图 artifacts/transparency/flyout-frosted.png（滑块应可用）
#   Opaque  模式 → 截图 artifacts/transparency/flyout-opaque.png（滑块应禁用）
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$exe = "D:\Project\kokonaDown\src\KokonaDownloader.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\KokonaDownloader.exe"
$settings = "$env:APPDATA\KokonaDownloader\settings.json"
$outDir = "D:\Project\kokonaDown\artifacts\transparency"

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

$root = [System.Windows.Automation.AutomationElement]::RootElement

function Shoot-Flyout {
    param([string]$mode, [string]$file)
    Set-Settings -mode $mode -strength 0.5
    Stop-App
    $p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
    $appPid = [uint32]$p.Id
    Start-Sleep -Seconds 9
    if ($p.HasExited) { Write-Host "FAIL: process exited ($mode)"; return }

    $procCond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$appPid)
    $main = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $procCond)
    if ($null -eq $main) { Write-Host "FAIL: main window not found ($mode)"; Stop-App; return }

    $btn = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, "窗口透明度"))
    if ($null -eq $btn) { Write-Host "FAIL: button not found ($mode)"; Stop-App; return }

    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 1200

    # 找含 Slider 的顶层窗口（Flyout）；Flyout 的 popup 窗口可能覆盖整屏，
    # 故用"菜单内各元素矩形求并集"得到贴合内容的裁剪框
    $sliderCond = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Slider)
    # 注意：$Host 是 PowerShell 只读自动变量，不能当变量名
    $hostWin = $null; $sliderEnabled = $null; $sliderValue = $null
    foreach ($child in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $procCond)) {
        $sliders = $child.FindAll([System.Windows.Automation.TreeScope]::Descendants, $sliderCond)
        if ($sliders.Count -gt 0) {
            $hostWin = $child
            $sliderEnabled = $sliders[0].Current.IsEnabled
            try { $sliderValue = $sliders[0].GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value } catch { }
            break
        }
    }
    if ($null -eq $hostWin) { Write-Host "FAIL: flyout with slider not found ($mode)"; Stop-App; return }

    # UIA 转储：记录菜单实际渲染出的元素（文本/按钮/滑块）并求并集矩形
    Write-Host ("--- {0} flyout UIA dump ---" -f $mode)
    $all = $hostWin.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $minX = [double]::MaxValue; $minY = [double]::MaxValue; $maxX = 0.0; $maxY = 0.0
    foreach ($e in $all) {
        $r = $e.Current.BoundingRectangle
        $ct = $e.Current.ControlType.ProgrammaticName -replace '^ControlType\.', ''
        if (($ct -eq 'Text' -or $ct -eq 'Button' -or $ct -eq 'Slider') -and $r.Width -gt 0 -and $r.Height -gt 0) {
            Write-Host ("    [{0}] '{1}' rect=({2},{3}) {4}x{5} enabled={6}" -f $ct, $e.Current.Name, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height, $e.Current.IsEnabled)
            if ($r.X -lt $minX) { $minX = $r.X }
            if ($r.Y -lt $minY) { $minY = $r.Y }
            if (($r.X + $r.Width) -gt $maxX) { $maxX = $r.X + $r.Width }
            if (($r.Y + $r.Height) -gt $maxY) { $maxY = $r.Y + $r.Height }
        }
    }
    if ($maxX -le 0) { Write-Host "FAIL: no visible menu elements ($mode)"; Stop-App; return }

    $pad = 16
    $x = [int][Math]::Max(0, $minX - $pad)
    $y = [int][Math]::Max(0, $minY - $pad)
    $w = [int][Math]::Min(2000, ($maxX - $minX) + 2 * $pad)
    $h = [int][Math]::Min(2000, ($maxY - $minY) + 2 * $pad)
    $bmp = [System.Drawing.Bitmap]::new($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, [System.Drawing.Size]::new($w, $h))
    $g.Dispose()
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()

    Write-Host ("{0}: flyout rect=({1},{2}) size={3}x{4} sliderEnabled={5} sliderValue={6} -> {7}" -f $mode, $x, $y, $w, $h, $sliderEnabled, $sliderValue, (Split-Path $file -Leaf))
    Stop-App
}

Shoot-Flyout -mode "Frosted" -file (Join-Path $outDir "flyout-frosted.png")
Shoot-Flyout -mode "Opaque"  -file (Join-Path $outDir "flyout-opaque.png")

# 恢复默认：Frosted + 0.5
Set-Settings -mode "Frosted" -strength 0.5
Write-Host "done"
