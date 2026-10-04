<#
.SYNOPSIS
    Draws the Lumen icon (a sparkle on a violet rounded square) and writes a multi-size .ico file.

.DESCRIPTION
    An .ico file is a small directory of images. Since Windows Vista each entry may simply be
    a PNG, so we render every size with GDI+ and pack the PNG bytes behind an ICONDIR header.
    Run from the repository root:  powershell -ExecutionPolicy Bypass -File scripts/make-icon.ps1
#>
param(
    [string]$OutFile = "src/Lumen.App/Assets/lumen.ico"
)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded square with a diagonal gradient.
    $r = [single]($size * 0.22)
    $rect = New-Object System.Drawing.RectangleF(0, 0, ($size - 1), ($size - 1))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($rect.Right - 2 * $r, $rect.Y, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($rect.Right - 2 * $r, $rect.Bottom - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect,
        [System.Drawing.Color]::FromArgb(255, 99, 102, 241),
        [System.Drawing.Color]::FromArgb(255, 168, 85, 247), 45.0)
    $g.FillPath($brush, $path)

    # Four-pointed sparkle: each point is a quadratic-looking curve through the centre.
    $c = $size / 2.0
    $big = $size * 0.36
    $waist = $size * 0.07
    $star = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pts = @(
        (New-Object System.Drawing.PointF($c, ($c - $big))),
        (New-Object System.Drawing.PointF(($c + $waist), ($c - $waist))),
        (New-Object System.Drawing.PointF(($c + $big), $c)),
        (New-Object System.Drawing.PointF(($c + $waist), ($c + $waist))),
        (New-Object System.Drawing.PointF($c, ($c + $big))),
        (New-Object System.Drawing.PointF(($c - $waist), ($c + $waist))),
        (New-Object System.Drawing.PointF(($c - $big), $c)),
        (New-Object System.Drawing.PointF(($c - $waist), ($c - $waist)))
    )
    $star.AddClosedCurve($pts, 0.15)
    $g.FillPath([System.Drawing.Brushes]::White, $star)

    # A small second sparkle, top-right (skipped on tiny sizes where it would be a blur).
    if ($size -ge 32) {
        $c2x = $size * 0.76; $c2y = $size * 0.24; $s = $size * 0.11; $w = $size * 0.025
        $small = New-Object System.Drawing.Drawing2D.GraphicsPath
        $small.AddClosedCurve(@(
            (New-Object System.Drawing.PointF($c2x, ($c2y - $s))),
            (New-Object System.Drawing.PointF(($c2x + $w), ($c2y - $w))),
            (New-Object System.Drawing.PointF(($c2x + $s), $c2y)),
            (New-Object System.Drawing.PointF(($c2x + $w), ($c2y + $w))),
            (New-Object System.Drawing.PointF($c2x, ($c2y + $s))),
            (New-Object System.Drawing.PointF(($c2x - $w), ($c2y + $w))),
            (New-Object System.Drawing.PointF(($c2x - $s), $c2y)),
            (New-Object System.Drawing.PointF(($c2x - $w), ($c2y - $w)))
        ), 0.15)
        $g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(220, 255, 255, 255))), $small)
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) { , (New-IconPng $s) }

$dir = Split-Path $OutFile -Parent
if ($dir) { New-Item -ItemType Directory -Force $dir | Out-Null }
$fs = [System.IO.File]::Create((Join-Path (Get-Location) $OutFile))
$w = New-Object System.IO.BinaryWriter($fs)

# ICONDIR: reserved(0), type(1 = icon), image count
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    # ICONDIRENTRY: width, height (0 means 256), colors, reserved, planes, bpp, size, offset
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()

# A PNG copy for the README.
[System.IO.File]::WriteAllBytes((Join-Path (Get-Location) "docs/lumen-256.png"), $images[-1])
Write-Host "Wrote $OutFile ($($sizes.Count) sizes) and docs/lumen-256.png"
