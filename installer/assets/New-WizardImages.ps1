<#
.SYNOPSIS
    Regenerates the Inno Setup wizard images in installer\assets from src\MdReader.App\Assets\MdReader.ico.

.DESCRIPTION
    Run this after the application icon changes; the generated PNGs are committed.
      wizard-small-<n>.png      icon only, transparent (top-right corner of every wizard page), one per DPI step
      wizard-light-<w>x<h>.png  Finished page image for light mode (aspect ratio 164:314)
      wizard-dark-<w>x<h>.png   Finished page image for dark mode (WizardStyle=modern dynamic)
    Windows PowerShell 5.1 compatible (System.Drawing).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File installer\assets\New-WizardImages.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = $PSScriptRoot
$repoRoot = Split-Path -Parent (Split-Path -Parent $assets)
$iconPath = Join-Path $repoRoot 'src\MdReader.App\Assets\MdReader.ico'

# The 256 px entry of the .ico is PNG-compressed, which System.Drawing.Icon can't decode: read it from the icon directory.
function Get-LargestIconImage([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $count = [BitConverter]::ToUInt16($bytes, 4)
    $best = $null
    for ($i = 0; $i -lt $count; $i++) {
        $entry = 6 + 16 * $i
        $width = [int]$bytes[$entry]; if ($width -eq 0) { $width = 256 }
        if ($null -eq $best -or $width -gt $best.Width) {
            $best = @{ Width = $width; Size = [BitConverter]::ToUInt32($bytes, $entry + 8); Offset = [BitConverter]::ToUInt32($bytes, $entry + 12) }
        }
    }
    $data = New-Object byte[] $best.Size
    [Array]::Copy($bytes, [int]$best.Offset, $data, 0, [int]$best.Size)
    if ($data[0] -ne 0x89) { throw "The largest icon image in $Path isn't PNG-compressed; export a PNG and adapt this script." }
    $stream = New-Object IO.MemoryStream (, $data)
    return [System.Drawing.Image]::FromStream($stream)
}

function New-Canvas([int]$Width, [int]$Height) {
    $bitmap = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    return @{ Bitmap = $bitmap; Graphics = $graphics }
}

function Save-Canvas($Canvas, [string]$Name) {
    $Canvas.Graphics.Dispose()
    $path = Join-Path $assets $Name
    $Canvas.Bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $Canvas.Bitmap.Dispose()
    Write-Host "  $Name"
}

function Draw-CenteredText($Graphics, [string]$Text, [string]$FontName, [float]$Size, [System.Drawing.FontStyle]$Style,
                           [System.Drawing.Color]$Color, [int]$Width, [float]$Top) {
    $font = New-Object System.Drawing.Font($FontName, $Size, $Style, [System.Drawing.GraphicsUnit]::Pixel)
    $brush = New-Object System.Drawing.SolidBrush($Color)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $Graphics.DrawString($Text, $font, $brush, (New-Object System.Drawing.RectangleF(0, $Top, $Width, ($Size * 2))), $format)
    $format.Dispose(); $brush.Dispose(); $font.Dispose()
}

$icon = Get-LargestIconImage $iconPath
try {
    Write-Host "Writing wizard images to $assets"

    # Small image: the icon on a transparent square, one file per DPI step (Inno Setup picks the closest size).
    foreach ($size in 58, 77, 97, 124, 159) {
        $canvas = New-Canvas $size $size
        $canvas.Graphics.DrawImage($icon, 0, 0, $size, $size)
        Save-Canvas $canvas "wizard-small-$size.png"
    }

    # Large image (Finished page): soft gradient, centered icon, product name. 164:314 aspect ratio.
    $variants = @(
        @{ Name = 'light'; Top = '#F6F8FA'; Bottom = '#DDEBFB'; Title = '#1F2328'; Subtitle = '#59636E' },
        @{ Name = 'dark';  Top = '#161B22'; Bottom = '#0C2D5E'; Title = '#F0F6FC'; Subtitle = '#9198A1' }
    )
    foreach ($variant in $variants) {
        foreach ($dimensions in @(@(202, 386), @(336, 643), @(534, 1022))) {
            $width = $dimensions[0]; $height = $dimensions[1]
            $canvas = New-Canvas $width $height
            $g = $canvas.Graphics
            $rect = New-Object System.Drawing.Rectangle(0, 0, $width, $height)
            $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect,
                [System.Drawing.ColorTranslator]::FromHtml($variant.Top),
                [System.Drawing.ColorTranslator]::FromHtml($variant.Bottom),
                [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
            $g.FillRectangle($gradient, $rect)
            $gradient.Dispose()

            $iconSize = [int][Math]::Round($width * 0.44)
            $iconTop = [int][Math]::Round($height * 0.30)
            $g.DrawImage($icon, [int](($width - $iconSize) / 2), $iconTop, $iconSize, $iconSize)

            $titleSize = [float]($width * 0.125)
            $titleTop = [float]($iconTop + $iconSize + $width * 0.09)
            Draw-CenteredText $g 'MdReader' 'Segoe UI Semibold' $titleSize ([System.Drawing.FontStyle]::Regular) `
                ([System.Drawing.ColorTranslator]::FromHtml($variant.Title)) $width $titleTop
            Draw-CenteredText $g 'Markdown reader' 'Segoe UI' ([float]($width * 0.068)) ([System.Drawing.FontStyle]::Regular) `
                ([System.Drawing.ColorTranslator]::FromHtml($variant.Subtitle)) $width ([float]($titleTop + $titleSize * 1.45))

            Save-Canvas $canvas ("wizard-{0}-{1}x{2}.png" -f $variant.Name, $width, $height)
        }
    }
}
finally {
    $icon.Dispose()
}
