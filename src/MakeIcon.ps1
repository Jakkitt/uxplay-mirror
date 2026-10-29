# Draws the UxPlay Mirror app icon (phone + cast waves on a blue gradient) into a multi-size .ico.
# Each size is drawn on its own pixel grid (not scaled down from 256 px) so small sizes stay crisp:
# whole-pixel line widths, edges on pixel boundaries, and fewer details at 16-20 px.
param([string]$Out = (Join-Path $PSScriptRoot 'app.ico'))
Add-Type -AssemblyName System.Drawing

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Max(1, $r * 2)
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'Half'

    # background tile: fills the whole square at small sizes, a little inset at large ones
    $m = if ($s -le 24) { 0 } else { [Math]::Round($s * 0.03) }
    $bg = New-RoundRect $m $m ($s - 2 * $m) ($s - 2 * $m) ([Math]::Max(3, $s * 0.22))
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), ([System.Drawing.Color]::FromArgb(255, 59, 130, 246)), ([System.Drawing.Color]::FromArgb(255, 124, 77, 255))
    $g.FillPath($grad, $bg)

    $white = [System.Drawing.Color]::White
    # whole-pixel stroke; the outline is inset by half a stroke so its edges land on pixel boundaries
    $sw = if ($s -le 24) { 2 } elseif ($s -le 48) { [Math]::Round($s * 0.07) } else { [Math]::Round($s * 0.055) }
    $pen = New-Object System.Drawing.Pen $white, $sw
    $pen.LineJoin = 'Round'

    # phone body
    $fx = if ($s -le 20) { 0.22 } else { 0.20 }
    $px = [Math]::Round($s * $fx)
    $fw = if ($s -le 20) { 0.36 } else { 0.34 }
    $pw = [Math]::Round($s * $fw)
    $py = [Math]::Round($s * 0.16)
    $ph = $s - 2 * $py
    $off = $sw / 2
    $phone = New-RoundRect ($px + $off) ($py + $off) ($pw - $sw) ($ph - $sw) ([Math]::Max(1, $s * 0.07))
    $g.DrawPath($pen, $phone)
    if ($s -ge 32) {
        $dw = [Math]::Round($s * 0.09); $dh = [Math]::Max(2, [Math]::Round($s * 0.035))
        $g.FillEllipse((New-Object System.Drawing.SolidBrush $white), [float]($px + ($pw - $dw) / 2), [float]($py + $ph - $sw - $dh - [Math]::Round($s * 0.04)), [float]$dw, [float]$dh)
    }

    # cast waves to the right of the phone (one at the tiniest sizes)
    $wave = New-Object System.Drawing.Pen $white, $sw
    $wave.StartCap = 'Round'; $wave.EndCap = 'Round'
    $cx = $px + $pw; $cy = $s / 2
    $radii = if ($s -le 20) { @(($s * 0.24)) } else { @(($s * 0.15), ($s * 0.28)) }
    foreach ($r in $radii) {
        $g.DrawArc($wave, [float]($cx - $r + $s * 0.04), [float]($cy - $r), [float](2 * $r), [float](2 * $r), -50, 100)
    }
    $g.Dispose()
    $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) {
    $b = New-IconBitmap $sz
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
    , $ms.ToArray()
}

$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
"wrote $Out"
