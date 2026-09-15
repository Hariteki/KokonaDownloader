# Compare the three mode screenshots at identical WINDOW-RELATIVE offsets.
# The crops are window-relative, so the same offsets describe the same UI spot in each mode.
Add-Type -AssemblyName System.Drawing

$dir = "D:\Project\kokonaDown\artifacts\transparency"
$offsets = @(@(30,12), @(200,12), @(600,12), @(30,40), @(30,90), @(200,140), @(700,90), @(60,420), @(450,500), @(820,450))

$modes = @{}
foreach ($m in @("Opaque", "Frosted", "BlackTransparent")) {
    $path = Join-Path $dir "mode-$m.png"
    if (-not (Test-Path $path)) { Write-Host "missing: $path"; continue }
    $bmp = [System.Drawing.Bitmap]::FromFile($path)
    $vals = @()
    foreach ($o in $offsets) {
        $c = $bmp.GetPixel($o[0], $o[1])
        $vals += ("{0,3},{1,3},{2,3}" -f $c.R, $c.G, $c.B)
    }
    $modes[$m] = $vals
    Write-Host ("{0,-17} {1}" -f $m, ($vals -join ' | '))
    $bmp.Dispose()
}

Write-Host ""
Write-Host "Hue check (R-B spread) per mode: a themed tint keeps R>B for a warm theme, black tint stays neutral."
foreach ($m in @("Opaque", "Frosted", "BlackTransparent")) {
    if (-not $modes.ContainsKey($m)) { continue }
    $spread = 0
    foreach ($v in $modes[$m]) {
        $p = $v -split ','
        $spread += ([int]$p[0] - [int]$p[2])
    }
    Write-Host ("  {0,-17} mean(R-B) = {1}" -f $m, [math]::Round($spread / $modes[$m].Count, 1))
}
