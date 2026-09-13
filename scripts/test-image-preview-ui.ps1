param(
    [string]$ExecutablePath,
    [string]$SourceImage,
    [string]$ArtifactRoot
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $root 'windows\BlueLink.App\bin\Debug\net8.0-windows10.0.19041.0\BlueLink.exe'
}
if ([string]::IsNullOrWhiteSpace($SourceImage)) {
    $SourceImage = Join-Path $root 'design\brand\final\bluelink-final-logo.png'
}
if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    $ArtifactRoot = Join-Path $root ('artifacts\qa\image-preview\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
$SourceImage = [IO.Path]::GetFullPath($SourceImage)
$ArtifactRoot = [IO.Path]::GetFullPath($ArtifactRoot)
foreach ($path in @($ExecutablePath, $SourceImage)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required preview input is missing: $path" }
}
New-Item -ItemType Directory -Path $ArtifactRoot -Force | Out-Null

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

$xamlPath = Join-Path $root 'windows\BlueLink.App\ImagePreviewWindow.xaml'
$codePath = Join-Path $root 'windows\BlueLink.App\ImagePreviewWindow.xaml.cs'
$mathPath = Join-Path $root 'windows\BlueLink.App\ImagePreviewViewportMath.cs'
$xaml = Get-Content -LiteralPath $xamlPath -Raw -Encoding UTF8
$code = Get-Content -LiteralPath $codePath -Raw -Encoding UTF8
$math = Get-Content -LiteralPath $mathPath -Raw -Encoding UTF8
[xml]$xaml | Out-Null
Assert-True ($xaml -match '^<ui:FluentWindow\b') 'Image preview must use WPF UI FluentWindow.'
Assert-True ($xaml -match '<ui:TitleBar\b') 'Image preview must use the WPF UI TitleBar.'
Assert-True ($xaml -match 'x:Name="TitleBarFileName"') 'The actual file name must be hosted in the title bar.'
$legacyMarkers = @('x:Name="FileNameText"', 'x:Name="FileDetailText"',
    'ToggleMaximize_Click', 'x:Name="FullscreenButton"')
$legacyMarker = $legacyMarkers | Where-Object { $xaml.Contains($_) } | Select-Object -First 1
Assert-True ($null -eq $legacyMarker) "Legacy metadata row or custom fullscreen action remains: $legacyMarker"
Assert-True ($code -match 'TitleBarFileName\.Text = fileName' -and $code -match 'Path\.GetFileName\(fullPath\)') 'The title bar is not populated from the actual file name.'
Assert-True ($xaml -notmatch '<ScrollViewer\b|HorizontalScrollBarVisibility|VerticalScrollBarVisibility') 'Image preview must not expose ScrollViewer scrollbars.'
Assert-True ($xaml -notmatch '#101624|#0D1320|Background="Black"') 'Image preview must not retain a black viewport background.'
Assert-True ($xaml -match 'Background="\{DynamicResource TransparencyGridBrush\}"') 'The preview viewport must be fully covered by the checkerboard brush.'
Assert-True ($xaml -match 'x:Name="Navigator"' -and $xaml -match 'x:Name="NavigatorCrop"') 'Navigator and crop rectangle are required.'
Assert-True ($xaml -match 'x:Name="ImageSurface"' -and $xaml -match '<MatrixTransform x:Name="ImageTransform"') 'The preview must use an unclipped surface and one matrix transform.'
Assert-True ($xaml -notmatch 'ImageScale|ImageRotation|ImageTranslation|ZoomTextButton') 'Legacy split transforms or clickable zoom percentage remain.'
Assert-True ($xaml -match 'x:Name="ZoomText"' -and $xaml -match 'PreviewZoomSegmentButtonStyle') 'The continuous three-part zoom control is missing.'
Assert-True ($xaml -match '<ColumnDefinition Width="38"\s*/>\s*<ColumnDefinition Width="64"\s*/>\s*<ColumnDefinition Width="38"\s*/>') 'Zoom segments must have zero spacing.'
Assert-True ($xaml -notmatch 'Separator.*Zoom|Zoom.*Separator') 'Zoom control must not contain vertical separators.'
foreach ($action in @('Fit_Click', 'ActualSize_Click', 'RotateLeft_Click', 'RotateRight_Click', 'Reset_Click')) {
    Assert-True ($xaml -match $action) "Missing image preview action: $action"
}
Assert-True ($xaml -notmatch 'Locate_Click|SaveAs_Click|PreviewFileActions|Esc 关闭') 'Duplicate file actions or Escape hint remain in the preview.'
Assert-True ($xaml -notmatch 'DownloadOriginal|Download_Click|<ControlTemplate\b') 'A download action or hand-written button template remains.'
Assert-True ($code -match 'LoadZoomedVisualFixture' -and $math -match 'VisibleMapRect' -and
    $math -match 'CreateImageMatrix' -and $math -match 'TransformedBounds') 'Unified viewport geometry or zoomed navigator smoke support is missing.'

Add-Type -AssemblyName System.Drawing
$sourceItem = Get-Item -LiteralPath $SourceImage
$sourceHash = (Get-FileHash -LiteralPath $SourceImage -Algorithm SHA256).Hash
$sourceBitmap = [Drawing.Bitmap]::FromFile($SourceImage)
try {
    $sourceDimensions = "$($sourceBitmap.Width)x$($sourceBitmap.Height)"
    $sourcePixelFormat = [string]$sourceBitmap.PixelFormat
    Assert-True ($sourceBitmap.Width -gt 0 -and $sourceBitmap.Height -gt 0) 'Source image dimensions are invalid.'
    Assert-True ($sourceBitmap.PixelFormat.ToString().Contains('32bpp')) 'Source image is not a 32-bit alpha-capable bitmap.'
}
finally { $sourceBitmap.Dispose() }
function Invoke-PreviewCapture([string]$name, [switch]$Zoomed) {
    $path = Join-Path $ArtifactRoot $name
    $arguments = @("--preview-ui-smoke-test=$path", "--preview-source=$SourceImage")
    if ($Zoomed) { $arguments += '--preview-zoomed' }
    $process = Start-Process -FilePath $ExecutablePath -ArgumentList $arguments -Wait -PassThru
    Assert-True ($process.ExitCode -eq 0) "Preview smoke exited with code $($process.ExitCode): $name"
    Assert-True ((Test-Path -LiteralPath $path) -and (Get-Item -LiteralPath $path).Length -gt 40000) "Preview screenshot is missing or too small: $name"
    return $path
}

function Assert-CheckerboardCorners([string]$path) {
    $bitmap = [Drawing.Bitmap]::FromFile($path)
    try {
        $samples = @(
            @(5, 130),
            @($($bitmap.Width - 6), 150),
            @(5, $($bitmap.Height - 90)),
            @($($bitmap.Width - 6), $($bitmap.Height - 90))
        )
        foreach ($sample in $samples) {
            $color = $bitmap.GetPixel($sample[0], $sample[1])
            $checkerColors = @('F6F8FB', 'E4E9F0', '232B38', '2B3544')
            $sampleColor = '{0:X2}{1:X2}{2:X2}' -f $color.R, $color.G, $color.B
            Assert-True ($sampleColor -in $checkerColors) "Unexpected preview checkerboard color at $($sample[0]),$($sample[1]): $sampleColor"
        }
    }
    finally { $bitmap.Dispose() }
}

function Assert-NavigatorVisible([string]$path) {
    $bitmap = [Drawing.Bitmap]::FromFile($path)
    try {
        $left = [Math]::Max(0, $bitmap.Width - 220)
        $top = [Math]::Max(0, $bitmap.Height - 250)
        $bluePixels = 0
        $darkBorderPixels = 0
        for ($x = $left; $x -lt $bitmap.Width - 8; $x += 2) {
            for ($y = $top; $y -lt $bitmap.Height - 78; $y += 2) {
                $color = $bitmap.GetPixel($x, $y)
                if ($color.B -gt $color.R + 70 -and $color.G -gt $color.R + 30) { $bluePixels++ }
                $borderColor = '{0:X2}{1:X2}{2:X2}' -f $color.R, $color.G, $color.B
                if ($borderColor -in @('DCE3EF', '3A465B')) { $darkBorderPixels++ }
            }
        }
        Assert-True ($bluePixels -gt 20 -and $darkBorderPixels -gt 20) 'Zoomed screenshot does not contain the navigator image and crop border.'
    }
    finally { $bitmap.Dispose() }
}

$fit = Invoke-PreviewCapture 'image-preview-fit.png'
$zoomed = Invoke-PreviewCapture 'image-preview-zoomed-navigator.png' -Zoomed
Assert-CheckerboardCorners $fit
Assert-CheckerboardCorners $zoomed
Assert-NavigatorVisible $zoomed
$fitHash = (Get-FileHash -LiteralPath $fit -Algorithm SHA256).Hash
$zoomedHash = (Get-FileHash -LiteralPath $zoomed -Algorithm SHA256).Hash
Assert-True ($fitHash -ne $zoomedHash) 'Fit and zoomed preview captures are unexpectedly identical.'

@(
    'BlueLink image preview UI acceptance: PASS',
    "Executable=$ExecutablePath",
    "SourceImage=$SourceImage",
    "SourceLength=$($sourceItem.Length)",
    "SourceDimensions=$sourceDimensions",
    "SourcePixelFormat=$sourcePixelFormat",
    "SourceSHA256=$sourceHash",
    "FitScreenshot=$fit",
    "FitSHA256=$fitHash",
    "ZoomedScreenshot=$zoomed",
    "ZoomedSHA256=$zoomedHash",
    'CheckerboardHasNoBlackCorners=True',
    'ZoomedNavigatorStateCaptured=True'
) | Set-Content -LiteralPath (Join-Path $ArtifactRoot 'image-preview-ui-result.txt') -Encoding UTF8

Write-Host "BlueLink image preview UI acceptance passed: $ArtifactRoot"
