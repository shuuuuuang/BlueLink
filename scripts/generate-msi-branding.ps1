# Converts the approved project logo into native MSI bitmap resources.
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$source = [Drawing.Bitmap]::FromFile((Join-Path $root 'design/brand/final/bluelink-final-logo.png'))
$directory = Join-Path $root 'design/brand/installer'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
try {
    $minX=$source.Width; $minY=$source.Height; $maxX=-1; $maxY=-1
    for ($y=0; $y -lt $source.Height; $y++) {
        for ($x=0; $x -lt $source.Width; $x++) {
            if ($source.GetPixel($x,$y).A -gt 4) {
                $minX=[Math]::Min($minX,$x); $minY=[Math]::Min($minY,$y)
                $maxX=[Math]::Max($maxX,$x); $maxY=[Math]::Max($maxY,$y)
            }
        }
    }
    if ($maxX -lt $minX) { throw 'Project logo is empty.' }
    $bounds=[Drawing.Rectangle]::new($minX,$minY,$maxX-$minX+1,$maxY-$minY+1)
    foreach ($spec in @(
        @{ Name='msi-sidebar.bmp'; Width=520; Height=936; Background='#082D95'; X=52; Y=158; Size=416 },
        @{ Name='msi-banner.bmp'; Width=1480; Height=176; Background='#FFFFFF'; X=1332; Y=24; Size=120 }
    )) {
        $bitmap=[Drawing.Bitmap]::new($spec.Width,$spec.Height,[Drawing.Imaging.PixelFormat]::Format24bppRgb)
        $graphics=[Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([Drawing.ColorTranslator]::FromHtml($spec.Background))
            if ($spec.Name -eq 'msi-sidebar.bmp') {
                # EXE brand palette and curved accents, rendered at 4x native MSI units.
                $gradient=[Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.Point]::new(0,0),[Drawing.Point]::new(520,936),[Drawing.Color]::Black,[Drawing.Color]::White)
                $blend=[Drawing.Drawing2D.ColorBlend]::new(3)
                $blend.Colors=@([Drawing.ColorTranslator]::FromHtml('#082D95'),[Drawing.ColorTranslator]::FromHtml('#075DDF'),[Drawing.ColorTranslator]::FromHtml('#031F73'))
                $blend.Positions=@([single]0,[single]0.48,[single]1)
                $gradient.InterpolationColors=$blend
                $topBrush=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(140,12,82,200))
                $bottomBrush=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(148,6,32,102))
                try {
                    $graphics.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
                    $graphics.FillRectangle($gradient,0,0,520,936)
                    $graphics.FillEllipse($topBrush,-300,-325,735,735)
                    $graphics.FillEllipse($bottomBrush,235,580,740,740)
                } finally { $gradient.Dispose(); $topBrush.Dispose(); $bottomBrush.Dispose() }
                $nameFont=[Drawing.Font]::new('Microsoft YaHei UI',52,[Drawing.FontStyle]::Bold,[Drawing.GraphicsUnit]::Pixel)
                $tagFont=[Drawing.Font]::new('Microsoft YaHei UI',25,[Drawing.FontStyle]::Bold,[Drawing.GraphicsUnit]::Pixel)
                $tagBrush=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#BED7FF'))
                $format=[Drawing.StringFormat]::new()
                try {
                    $format.Alignment=[Drawing.StringAlignment]::Center
                    $graphics.TextRenderingHint=[Drawing.Text.TextRenderingHint]::AntiAliasGridFit
                    $graphics.DrawString('蓝联',$nameFont,[Drawing.Brushes]::White,[Drawing.RectangleF]::new(16,620,488,80),$format)
                    $graphics.DrawString('BLUETOOTH · USB',$tagFont,$tagBrush,[Drawing.RectangleF]::new(16,708,488,44),$format)
                } finally { $nameFont.Dispose(); $tagFont.Dispose(); $tagBrush.Dispose(); $format.Dispose() }
            }
            $graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $scale=[Math]::Min($spec.Size/$bounds.Width,$spec.Size/$bounds.Height)
            $width=[int][Math]::Round($bounds.Width*$scale)
            $height=[int][Math]::Round($bounds.Height*$scale)
            $destination=[Drawing.Rectangle]::new($spec.X+($spec.Size-$width)/2,$spec.Y+($spec.Size-$height)/2,$width,$height)
            $graphics.DrawImage($source,$destination,$bounds,[Drawing.GraphicsUnit]::Pixel)
            $bitmap.Save((Join-Path $directory $spec.Name),[Drawing.Imaging.ImageFormat]::Bmp)
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $source.Dispose() }
