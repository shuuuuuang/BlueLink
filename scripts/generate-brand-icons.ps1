param(
    [string]$Source = (Join-Path (Split-Path -Parent $PSScriptRoot) 'design\brand\final\bluelink-final-logo.png')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$sourceBitmap = [System.Drawing.Bitmap]::FromFile($Source)

function Get-AlphaBounds([System.Drawing.Bitmap]$Bitmap) {
    $minX = $Bitmap.Width
    $minY = $Bitmap.Height
    $maxX = -1
    $maxY = -1
    for ($y = 0; $y -lt $Bitmap.Height; $y++) {
        for ($x = 0; $x -lt $Bitmap.Width; $x++) {
            if ($Bitmap.GetPixel($x, $y).A -gt 4) {
                if ($x -lt $minX) { $minX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    if ($maxX -lt $minX -or $maxY -lt $minY) { throw 'The source logo is fully transparent.' }
    [System.Drawing.Rectangle]::new($minX, $minY, $maxX - $minX + 1, $maxY - $minY + 1)
}

$sourceBounds = Get-AlphaBounds $sourceBitmap

function New-LogoBitmap(
    [int]$Size,
    [double]$ContentFraction,
    [System.Drawing.Color]$Background,
    [bool]$RoundBackground,
    [bool]$Monochrome
) {
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        if ($Background.A -gt 0) {
            $brush = [System.Drawing.SolidBrush]::new($Background)
            try {
                if ($RoundBackground) { $graphics.FillEllipse($brush, 0, 0, $Size, $Size) }
                else { $graphics.FillRectangle($brush, 0, 0, $Size, $Size) }
            }
            finally { $brush.Dispose() }
        }

        $targetMax = $Size * $ContentFraction
        $scale = [Math]::Min($targetMax / $sourceBounds.Width, $targetMax / $sourceBounds.Height)
        $width = [Math]::Max(1, [int][Math]::Round($sourceBounds.Width * $scale))
        $height = [Math]::Max(1, [int][Math]::Round($sourceBounds.Height * $scale))
        $left = [int](($Size - $width) / 2)
        $top = [int](($Size - $height) / 2)
        $destination = [System.Drawing.Rectangle]::new($left, $top, $width, $height)
        $graphics.DrawImage($sourceBitmap, $destination, $sourceBounds, [System.Drawing.GraphicsUnit]::Pixel)
    }
    finally { $graphics.Dispose() }

    if ($Monochrome) {
        for ($y = 0; $y -lt $bitmap.Height; $y++) {
            for ($x = 0; $x -lt $bitmap.Width; $x++) {
                $alpha = $bitmap.GetPixel($x, $y).A
                if ($alpha -gt 0) { $bitmap.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($alpha, 255, 255, 255)) }
            }
        }
    }
    return $bitmap
}

function Save-Png([System.Drawing.Bitmap]$Bitmap, [string]$Path) {
    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $Bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
}

try {
    $windowsDirectory = Join-Path $root 'design\brand\app-icons\windows'
    New-Item -ItemType Directory -Path $windowsDirectory -Force | Out-Null
    $frames = @()
    foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
        $bitmap = New-LogoBitmap $size 0.90 ([System.Drawing.Color]::Transparent) $false $false
        try {
            $stream = [System.IO.MemoryStream]::new()
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames += ,@($size, $stream.ToArray())
        }
        finally { $bitmap.Dispose() }
    }

    $iconPath = Join-Path $windowsDirectory 'bluelink.ico'
    $file = [System.IO.File]::Create($iconPath)
    $writer = [System.IO.BinaryWriter]::new($file)
    try {
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]$frames.Count)
        $offset = 6 + (16 * $frames.Count)
        foreach ($frame in $frames) {
            $size = [int]$frame[0]
            $bytes = [byte[]]$frame[1]
            $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
            $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$bytes.Length)
            $writer.Write([UInt32]$offset)
            $offset += $bytes.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame[1]) }
    }
    finally { $writer.Dispose(); $file.Dispose() }

    $preview = New-LogoBitmap 512 0.86 ([System.Drawing.Color]::FromArgb(255, 245, 247, 251)) $true $false
    try { Save-Png $preview (Join-Path $windowsDirectory 'bluelink-icon-preview.png') }
    finally { $preview.Dispose() }

    $res = Join-Path $root 'android\app\src\main\res'
    $foreground = New-LogoBitmap 432 0.66 ([System.Drawing.Color]::Transparent) $false $false
    try { Save-Png $foreground (Join-Path $res 'drawable-nodpi\bluelink_launcher_foreground.png') }
    finally { $foreground.Dispose() }
    $monochrome = New-LogoBitmap 432 0.66 ([System.Drawing.Color]::Transparent) $false $true
    try { Save-Png $monochrome (Join-Path $res 'drawable-nodpi\bluelink_launcher_monochrome.png') }
    finally { $monochrome.Dispose() }

    $installerAssets = Join-Path $root 'installer\Assets'
    New-Item -ItemType Directory -Path $installerAssets -Force | Out-Null
    $side = [System.Drawing.Bitmap]::new(420, 650, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $graphics = [System.Drawing.Graphics]::FromImage($side)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            [System.Drawing.Rectangle]::new(0, 0, 420, 650),
            [System.Drawing.Color]::FromArgb(255, 5, 77, 224),
            [System.Drawing.Color]::FromArgb(255, 3, 31, 113),
            35.0)
        try { $graphics.FillRectangle($gradient, 0, 0, 420, 650) }
        finally { $gradient.Dispose() }
        $accent = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(45, 18, 200, 232))
        try {
            $graphics.FillEllipse($accent, -180, 360, 600, 360)
            $graphics.FillEllipse($accent, 80, -110, 430, 300)
        }
        finally { $accent.Dispose() }

        $target = [System.Drawing.Rectangle]::new(45, 105, 330, 285)
        $graphics.DrawImage($sourceBitmap, $target, $sourceBounds, [System.Drawing.GraphicsUnit]::Pixel)
        $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
        $softWhite = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(220, 255, 255, 255))
        $titleFont = [System.Drawing.Font]::new('Microsoft YaHei UI', 30, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $captionFont = [System.Drawing.Font]::new('Segoe UI', 16, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
        try {
            $titleFormat = [System.Drawing.StringFormat]::new()
            $titleFormat.Alignment = [System.Drawing.StringAlignment]::Center
            $brandName = ([char]0x84DD).ToString() + ([char]0x8054).ToString()
            $graphics.DrawString($brandName, $titleFont, $white,
                [System.Drawing.RectangleF]::new(0, 438, 420, 54), $titleFormat)
            $graphics.DrawString('BLUETOOTH ONLY', $captionFont, $softWhite,
                [System.Drawing.RectangleF]::new(0, 493, 420, 34), $titleFormat)
            $titleFormat.Dispose()
        }
        finally { $titleFont.Dispose(); $captionFont.Dispose(); $white.Dispose(); $softWhite.Dispose() }
        $side.Save((Join-Path $installerAssets 'wizard-side.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
    }
    finally { $graphics.Dispose(); $side.Dispose() }

    $small = New-LogoBitmap 96 0.84 ([System.Drawing.Color]::FromArgb(255, 245, 247, 251)) $false $false
    try { $small.Save((Join-Path $installerAssets 'wizard-small.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp) }
    finally { $small.Dispose() }
}
finally {
    $sourceBitmap.Dispose()
}

Write-Host 'BlueLink Windows and Android icon assets generated.'
