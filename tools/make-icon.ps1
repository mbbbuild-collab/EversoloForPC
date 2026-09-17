# Renders the EversoloForPC icon (vinyl disc with a play mark) at every size Windows uses
# and writes a multi-resolution app.ico plus PNGs for docs / other platforms.
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root "assets"
New-Item -ItemType Directory -Force $assets | Out-Null

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 128.0

    # rounded square background
    $r = 28 * $s; $d = 2 * $r
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml("#26215C"))
    $g.FillPath($bg, $path)

    function Circle($cx, $cy, $rad, $fill, $stroke, $w) {
        $x = ($cx - $rad) * $s; $y = ($cy - $rad) * $s; $dd = 2 * $rad * $s
        if ($fill)   { $b = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($fill)); $g.FillEllipse($b, $x, $y, $dd, $dd); $b.Dispose() }
        if ($stroke) { $p = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml($stroke)), ($w * $s); $g.DrawEllipse($p, $x, $y, $dd, $dd); $p.Dispose() }
    }
    Circle 64 64 46 "#0E0E12" $null 0
    if ($size -ge 32) {                       # grooves only where they can be seen
        Circle 64 64 38 $null "#2A2935" 2
        Circle 64 64 30 $null "#2A2935" 2
    }
    Circle 64 64 21 "#AFA9EC" $null 0
    $tri = @(
        (New-Object System.Drawing.PointF (58 * $s), (53 * $s)),
        (New-Object System.Drawing.PointF (77 * $s), (64 * $s)),
        (New-Object System.Drawing.PointF (58 * $s), (75 * $s)))
    $tb = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml("#26215C"))
    $g.FillPolygon($tb, [System.Drawing.PointF[]]$tri)
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($sz in $sizes) {
    $bmp = New-IconBitmap $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    if ($sz -in 64, 256) { $bmp.Save((Join-Path $assets "icon-$sz.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}
$big = New-IconBitmap 1024
$big.Save((Join-Path $assets "icon-1024.png"), [System.Drawing.Imaging.ImageFormat]::Png)   # source for a macOS .icns
$big.Dispose()

# ICO container with PNG-compressed entries
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $root "app.ico"), $out.ToArray())
"app.ico: $($out.Length) bytes, sizes: $($sizes -join ', ')"
