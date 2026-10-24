# NovaDownload — browser extension + application icon generator.
# Draws the brand mark (rounded gradient square, white download arrow) at every size the
# browsers and Windows ask for. Deterministic: same input, same bytes.
#
#   powershell -File browser-extensions\build-icons.ps1
#
# Output:
#   browser-extensions\common\icons\icon{16,32,48,128}.png   (extension toolbar + store)
#   assets\novadownload.ico                                  (application + installer, 6 sizes)

param(
    [string]$OutputDirectory,
    [string]$IconOutput
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $root
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'common\icons' }
if (-not $IconOutput) { $IconOutput = Join-Path $repo 'assets\novadownload.ico' }

$top = [System.Drawing.Color]::FromArgb(255, 0x3B, 0x82, 0xF6)
$bottom = [System.Drawing.Color]::FromArgb(255, 0x63, 0x66, 0xF1)

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Get-MarkBitmap([int]$size) {
    # Supersample 4x then downscale: keeps the 16 px toolbar icon readable.
    $scale = 4
    $big = $size * $scale
    $bitmap = New-Object System.Drawing.Bitmap($big, $big, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $background = New-Object System.Drawing.Drawing2D.GraphicsPath
    $background = New-RoundedPath 0 0 $big $big ([single]($big * 0.22))
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point(0, $big)),
        $top, $bottom)
    $g.FillPath($brush, $background)

    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)

    # Arrow shaft.
    $shaft = New-Object System.Drawing.RectangleF(
        ([single]($big * 0.425)), ([single]($big * 0.20)),
        ([single]($big * 0.15)), ([single]($big * 0.34)))
    $g.FillRectangle($white, $shaft)

    # Arrow head.
    $head = New-Object System.Drawing.Drawing2D.GraphicsPath
    $head.AddPolygon(@(
        (New-Object System.Drawing.PointF(([single]($big * 0.28)), ([single]($big * 0.48)))),
        (New-Object System.Drawing.PointF(([single]($big * 0.72)), ([single]($big * 0.48)))),
        (New-Object System.Drawing.PointF(([single]($big * 0.50)), ([single]($big * 0.76))))
    ))
    $g.FillPath($white, $head)

    # Receiving tray.
    $tray = New-RoundedPath ([single]($big * 0.27)) ([single]($big * 0.83)) ([single]($big * 0.46)) ([single]($big * 0.09)) ([single]($big * 0.045))
    $g.FillPath($white, $tray)

    $g.Dispose()
    $brush.Dispose()
    $white.Dispose()

    $result = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $rg = [System.Drawing.Graphics]::FromImage($result)
    $rg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $rg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $rg.DrawImage($bitmap, 0, 0, $size, $size)
    $rg.Dispose()
    $bitmap.Dispose()
    return $result
}

function Save-Png([System.Drawing.Bitmap]$bitmap, [string]$path) {
    $directory = Split-Path -Parent $path
    if (-not (Test-Path $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
}

function New-Ico([int[]]$sizes, [string]$path) {
    $directory = Split-Path -Parent $path
    if (-not (Test-Path $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }

    $images = @()
    foreach ($size in $sizes) {
        $bitmap = Get-MarkBitmap $size
        $stream = New-Object System.IO.MemoryStream
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $images += , @{ Size = $size; Bytes = $stream.ToArray() }
        $stream.Dispose()
        $bitmap.Dispose()
    }

    $file = [System.IO.File]::Create($path)
    $writer = New-Object System.IO.BinaryWriter($file)
    $writer.Write([UInt16]0)                 # reserved
    $writer.Write([UInt16]1)                 # type: icon
    $writer.Write([UInt16]$images.Count)

    $offset = 6 + (16 * $images.Count)
    foreach ($image in $images) {
        $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
        $writer.Write([Byte]$dimension)      # width
        $writer.Write([Byte]$dimension)      # height
        $writer.Write([Byte]0)               # palette
        $writer.Write([Byte]0)               # reserved
        $writer.Write([UInt16]1)             # color planes
        $writer.Write([UInt16]32)            # bits per pixel
        $writer.Write([UInt32]$image.Bytes.Length)
        $writer.Write([UInt32]$offset)
        $offset += $image.Bytes.Length
    }

    foreach ($image in $images) { $writer.Write($image.Bytes) }
    $writer.Flush()
    $writer.Dispose()
    $file.Dispose()
}

foreach ($size in 16, 32, 48, 128) {
    $bitmap = Get-MarkBitmap $size
    Save-Png $bitmap (Join-Path $OutputDirectory "icon$size.png")
    $bitmap.Dispose()
    Write-Output "icon -> $OutputDirectory\icon$size.png"
}

New-Ico @(16, 32, 48, 64, 128, 256) $IconOutput
Write-Output "icon -> $IconOutput"
