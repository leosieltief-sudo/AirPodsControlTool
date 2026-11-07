# Generates src/AirPodsControl.App/Assets/app.ico (multi-size, PNG entries) with System.Drawing.
param([string]$Out = "$PSScriptRoot\..\src\AirPodsControl.App\Assets\app.ico")
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $f = $s / 256.0
    # rounded dark tile
    $r = 56 * $f; $rect = New-Object System.Drawing.RectangleF (8*$f), (8*$f), (240*$f), (240*$f)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $r*2, $r*2, 180, 90)
    $path.AddArc($rect.Right - $r*2, $rect.Y, $r*2, $r*2, 270, 90)
    $path.AddArc($rect.Right - $r*2, $rect.Bottom - $r*2, $r*2, $r*2, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $r*2, $r*2, $r*2, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255,30,40,58)), ([System.Drawing.Color]::FromArgb(255,14,19,30)), 90
    $g.FillPath($bg, $path)
    # two pods: head + stem
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,240,244,250))
    $tip = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,150,160,175))
    foreach ($side in -1, 1) {
        $cx = 128 + $side * 46
        $g.FillEllipse($white, ($cx - 34)*$f, 52*$f, 68*$f, 62*$f)
        $g.FillEllipse($tip, ($cx - 15 - $side*30)*$f, 68*$f, 30*$f, 28*$f)
        $g.FillRectangle($white, ($cx - 12 + $side*10)*$f, 100*$f, 24*$f, 96*$f)
        $g.FillEllipse($white, ($cx - 12 + $side*10)*$f, 184*$f, 24*$f, 20*$f)
    }
    # green status dot
    $green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,126,224,160))
    $g.FillEllipse($green, 116*$f, 206*$f, 24*$f, 24*$f)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,$ms.ToArray()
    $bmp.Dispose()
}
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($(if ($s -ge 256) {0} else {$s}))); $w.Write([byte]($(if ($s -ge 256) {0} else {$s})))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset); $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
Write-Host "Icon written to $Out"
