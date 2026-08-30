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
    $ArtifactRoot = Join-Path $root ('artifacts\qa\image-preview-explorer\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
$SourceImage = [IO.Path]::GetFullPath($SourceImage)
$ArtifactRoot = [IO.Path]::GetFullPath($ArtifactRoot)
foreach ($path in @($ExecutablePath, $SourceImage)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required input is missing: $path" }
}
New-Item -ItemType Directory -Path $ArtifactRoot -Force | Out-Null

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class BlueLinkPreviewNative { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd); [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command); [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd); }'

$explorerActionName = -join @(
    [char]0x5728, [char]0x8D44, [char]0x6E90, [char]0x7BA1, [char]0x7406,
    [char]0x5668, [char]0x4E2D, [char]0x6253, [char]0x5F00)
$zoomOutActionName = -join @(
    [char]0x7F29, [char]0x5C0F, [char]0x56FE, [char]0x7247)
$zoomPercentageName = -join @(
    [char]0x5F53, [char]0x524D, [char]0x7F29, [char]0x653E,
    [char]0x6BD4, [char]0x4F8B)
$rotateRightActionName = -join @(
    [char]0x5411, [char]0x53F3, [char]0x65CB, [char]0x8F6C)

function Find-PreviewWindow([int]$processId, [string]$buttonName) {
    $windowCondition = New-Object Windows.Automation.AndCondition(
        (New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Window)),
        (New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)))
    $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children, $windowCondition)
    $buttonCondition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::NameProperty, $buttonName)
    foreach ($window in $windows) {
        $button = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $buttonCondition)
        if ($null -ne $button) { return [pscustomobject]@{ Window = $window; Button = $button } }
    }
    return $null
}

function Wait-PreviewWindow([int]$processId, [string]$buttonName) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $result = Find-PreviewWindow $processId $buttonName
        if ($null -ne $result) { return $result }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Timed out waiting for the image preview and its Explorer button.'
}

function Capture-Element($element, [string]$path) {
    $bounds = $element.Current.BoundingRectangle
    $width = [Math]::Max(1, [int][Math]::Ceiling($bounds.Width))
    $height = [Math]::Max(1, [int][Math]::Ceiling($bounds.Height))
    $bitmap = New-Object Drawing.Bitmap($width, $height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen([int][Math]::Floor($bounds.Left), [int][Math]::Floor($bounds.Top),
            0, 0, $bitmap.Size)
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Assert-PreviewCapture([string]$path) {
    $bitmap = [Drawing.Bitmap]::FromFile($path)
    try {
        $visibleSamples = 0
        $totalSamples = 0
        for ($x = 0; $x -lt $bitmap.Width; $x += 20) {
            for ($y = 0; $y -lt $bitmap.Height; $y += 20) {
                $color = $bitmap.GetPixel($x, $y)
                if (($color.R + $color.G + $color.B) -gt 300) { $visibleSamples++ }
                $totalSamples++
            }
        }
        Assert-True ($totalSamples -gt 0 -and $visibleSamples / $totalSamples -gt 0.55) 'Preview capture is obscured or unexpectedly dark.'
    }
    finally { $bitmap.Dispose() }
}

function Get-ChangedPixelCount([string]$beforePath, [string]$afterPath, $windowBounds, $regionBounds) {
    $before = [Drawing.Bitmap]::FromFile($beforePath)
    $after = [Drawing.Bitmap]::FromFile($afterPath)
    try {
        $left = [Math]::Max(0, [int][Math]::Floor($regionBounds.Left - $windowBounds.Left))
        $top = [Math]::Max(0, [int][Math]::Floor($regionBounds.Top - $windowBounds.Top))
        $right = [Math]::Min($before.Width, [int][Math]::Ceiling($regionBounds.Right - $windowBounds.Left))
        $bottom = [Math]::Min($before.Height, [int][Math]::Ceiling($regionBounds.Bottom - $windowBounds.Top))
        $changed = 0
        for ($x = $left; $x -lt $right; $x++) {
            for ($y = $top; $y -lt $bottom; $y++) {
                $a = $before.GetPixel($x, $y)
                $b = $after.GetPixel($x, $y)
                if ([Math]::Abs($a.R - $b.R) + [Math]::Abs($a.G - $b.G) +
                    [Math]::Abs($a.B - $b.B) -gt 12) { $changed++ }
            }
        }
        return $changed
    }
    finally {
        $before.Dispose()
        $after.Dispose()
    }
}

function Get-ShellWindows($shell) {
    $result = @()
    $windows = $shell.Windows()
    for ($index = 0; $index -lt $windows.Count; $index++) {
        try {
            $window = $windows.Item($index)
            $result += [pscustomobject]@{
                Hwnd = [long]$window.HWND
                LocationUrl = [string]$window.LocationURL
                FolderPath = [string]$window.Document.Folder.Self.Path
                Window = $window
            }
        } catch { }
    }
    return $result
}

function Test-ExplorerSelection($entry, [string]$leafName) {
    try {
        $rootElement = [Windows.Automation.AutomationElement]::FromHandle([IntPtr]$entry.Hwnd)
        if ($null -eq $rootElement) { return $false }
        $condition = New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::NameProperty, $leafName)
        $items = $rootElement.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
        foreach ($item in $items) {
            try {
                $selection = [Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern(
                    [Windows.Automation.SelectionItemPattern]::Pattern)
                if ($selection.Current.IsSelected) { return $true }
            } catch { }
        }
    } catch { }
    return $false
}

function Get-ExplorerUiaDetails($entry, [string]$leafName) {
    $details = @()
    try {
        $rootElement = [Windows.Automation.AutomationElement]::FromHandle([IntPtr]$entry.Hwnd)
        if ($null -eq $rootElement) { return @('ExplorerAutomationRoot=<null>') }
        $details += "ExplorerAutomationRoot=$($rootElement.Current.Name)"
        $items = $rootElement.FindAll(
            [Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.Condition]::TrueCondition)
        for ($index = 0; $index -lt $items.Count; $index++) {
            $item = $items.Item($index)
            $name = [string]$item.Current.Name
            $selected = $false
            $hasSelectionPattern = $false
            try {
                $selection = [Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern(
                    [Windows.Automation.SelectionItemPattern]::Pattern)
                $hasSelectionPattern = $true
                $selected = $selection.Current.IsSelected
            } catch { }
            if ($selected -or $name.Equals($leafName, [StringComparison]::OrdinalIgnoreCase) -or
                $name.IndexOf('preview-explorer-fixture', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $details += "Name=$name|Type=$($item.Current.ControlType.ProgrammaticName)|Class=$($item.Current.ClassName)|HasSelectionPattern=$hasSelectionPattern|Selected=$selected|Offscreen=$($item.Current.IsOffscreen)"
            }
        }
    } catch {
        $details += "ExplorerAutomationError=$($_.Exception.Message)"
    }
    return $details
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('BlueLinkPreviewExplorer-' + [Guid]::NewGuid().ToString('N'))
$fixtureRoot = [IO.Path]::GetFullPath($fixtureRoot)
Assert-True ($fixtureRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) 'Fixture escaped the system temp root.'
Assert-True ((Split-Path -Leaf $fixtureRoot) -match '^BlueLinkPreviewExplorer-[0-9a-f]{32}$') 'Unexpected fixture directory name.'
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
$fixturePath = Join-Path $fixtureRoot 'preview-explorer-fixture.png'
Copy-Item -LiteralPath $SourceImage -Destination $fixturePath

$shell = New-Object -ComObject Shell.Application
$baseline = @{}
foreach ($entry in @(Get-ShellWindows $shell)) { $baseline[$entry.Hwnd] = $entry }
$process = $null
$matchedShell = $null
$preview = $null
$folderOnlyMatch = $null
$explorerCaptured = $false
$observedShellState = @()
$originalCursor = $null
try {
    $process = Start-Process -FilePath $ExecutablePath -ArgumentList @(
        '--preview-interaction-smoke-test', "--preview-source=$fixturePath") -PassThru
    $preview = Wait-PreviewWindow $process.Id $explorerActionName
    $buttonBounds = $preview.Button.Current.BoundingRectangle
    Assert-True (-not $preview.Button.Current.IsOffscreen -and
        $preview.Button.Current.IsEnabled -and $buttonBounds.Width -gt 80 -and $buttonBounds.Height -gt 20) `
        'Explorer button is not visibly actionable.'
    $zoomOutCondition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::NameProperty, $zoomOutActionName)
    $zoomOutButton = $preview.Window.FindFirst(
        [Windows.Automation.TreeScope]::Descendants, $zoomOutCondition)
    Assert-True (($null -ne $zoomOutButton) -and
        ($zoomOutButton.Current.ControlType -eq [Windows.Automation.ControlType]::Button)) 'The visible zoom-out segment is missing.'

    $zoomPercentageCondition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::NameProperty, $zoomPercentageName)
    $zoomPercentage = $preview.Window.FindFirst(
        [Windows.Automation.TreeScope]::Descendants, $zoomPercentageCondition)
    Assert-True (($null -ne $zoomPercentage) -and
        ($zoomPercentage.Current.ControlType -eq [Windows.Automation.ControlType]::Text)) 'The zoom percentage must be a non-clickable text segment.'
    $rotateRightCondition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::NameProperty, $rotateRightActionName)
    $rotateRightButton = $preview.Window.FindFirst(
        [Windows.Automation.TreeScope]::Descendants, $rotateRightCondition)
    Assert-True (($null -ne $rotateRightButton) -and
        ($rotateRightButton.Current.ControlType -eq [Windows.Automation.ControlType]::Button)) 'The right-rotation button is missing.'

    $windowHandle = [IntPtr]$preview.Window.Current.NativeWindowHandle
    [BlueLinkPreviewNative]::ShowWindow($windowHandle, 9) | Out-Null
    [BlueLinkPreviewNative]::SetForegroundWindow($windowHandle) | Out-Null
    $preview.Window.SetFocus()
    Start-Sleep -Milliseconds 500
    $windowBounds = $preview.Window.Current.BoundingRectangle
    Assert-True (-not $preview.Window.Current.IsOffscreen -and
        $windowBounds.Width -gt 900 -and $windowBounds.Height -gt 500) 'Preview window is not visibly rendered on screen.'
    $windowDpi = [BlueLinkPreviewNative]::GetDpiForWindow($windowHandle)
    Assert-True ($windowDpi -ge 96) 'GetDpiForWindow returned an invalid DPI value.'

    $originalCursor = [Windows.Forms.Cursor]::Position
    [Windows.Forms.Cursor]::Position = [Drawing.Point]::new(0, 0)
    Start-Sleep -Milliseconds 300
    $defaultCapture = Join-Path $ArtifactRoot 'preview-zoom-default.png'
    Capture-Element $preview.Window $defaultCapture
    Assert-PreviewCapture $defaultCapture
    $zoomOutBounds = $zoomOutButton.Current.BoundingRectangle
    [Windows.Forms.Cursor]::Position = [Drawing.Point]::new(
        [int]($zoomOutBounds.Left + $zoomOutBounds.Width / 2),
        [int]($zoomOutBounds.Top + $zoomOutBounds.Height / 2))
    Start-Sleep -Milliseconds 350
    $hoverCapture = Join-Path $ArtifactRoot 'preview-zoom-left-hover.png'
    Capture-Element $preview.Window $hoverCapture
    Assert-PreviewCapture $hoverCapture
    $hoverChangedPixels = Get-ChangedPixelCount $defaultCapture $hoverCapture $windowBounds $zoomOutBounds
    Assert-True ($hoverChangedPixels -gt 150) 'The zoom-out segment did not expose a visible hover state.'

    [Windows.Forms.Cursor]::Position = [Drawing.Point]::new(0, 0)
    $rotateInvoke = [Windows.Automation.InvokePattern]$rotateRightButton.GetCurrentPattern(
        [Windows.Automation.InvokePattern]::Pattern)
    $rotateInvoke.Invoke()
    Start-Sleep -Milliseconds 500
    $rotatedCapture = Join-Path $ArtifactRoot 'preview-rotated-right.png'
    Capture-Element $preview.Window $rotatedCapture
    Assert-PreviewCapture $rotatedCapture
    Assert-True ((Get-FileHash -LiteralPath $defaultCapture -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $rotatedCapture -Algorithm SHA256).Hash) 'Right rotation did not change the rendered preview.'

    $invoke = [Windows.Automation.InvokePattern]$preview.Button.GetCurrentPattern(
        [Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()

    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        foreach ($entry in @(Get-ShellWindows $shell)) {
            if (-not [IO.Path]::GetFullPath($entry.FolderPath).TrimEnd('\').Equals(
                $fixtureRoot.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) { continue }
            $selected = @()
            try {
                $selectedItems = $entry.Window.Document.SelectedItems()
                for ($selectedIndex = 0; $selectedIndex -lt $selectedItems.Count; $selectedIndex++) {
                    $selected += [string]$selectedItems.Item($selectedIndex).Path
                }
            }
            catch { }
            $uiaSelected = Test-ExplorerSelection $entry (Split-Path -Leaf $fixturePath)
            $observedShellState += "Hwnd=$($entry.Hwnd)|Folder=$($entry.FolderPath)|Selected=$($selected -join ',')|UiaSelected=$uiaSelected"
            $folderOnlyMatch = $entry
            if (-not $explorerCaptured) {
                Start-Sleep -Milliseconds 800
                try {
                    $explorerElement = [Windows.Automation.AutomationElement]::FromHandle([IntPtr]$entry.Hwnd)
                    Capture-Element $explorerElement (Join-Path $ArtifactRoot 'explorer-after-click.png')
                    $explorerCaptured = $true
                } catch { }
            }
            if ($uiaSelected -or ($selected | Where-Object { [IO.Path]::GetFullPath($_).Equals($fixturePath, [StringComparison]::OrdinalIgnoreCase) })) {
                $matchedShell = $entry
                break
            }
        }
        if ($null -eq $matchedShell) { Start-Sleep -Milliseconds 200 }
    } while ($null -eq $matchedShell -and [DateTime]::UtcNow -lt $deadline)

    if ($null -eq $matchedShell) {
        $observedShellState | Select-Object -Unique | Set-Content -LiteralPath (
            Join-Path $ArtifactRoot 'explorer-observed-state.txt') -Encoding UTF8
        if ($null -ne $folderOnlyMatch) {
            Get-ExplorerUiaDetails $folderOnlyMatch (Split-Path -Leaf $fixturePath) |
                Set-Content -LiteralPath (Join-Path $ArtifactRoot 'explorer-uia-state.txt') -Encoding UTF8
        }
        throw 'Explorer did not open the fixture folder with the requested file selected.'
    }
    $reused = $baseline.ContainsKey($matchedShell.Hwnd)
    @(
        'BlueLink image preview Explorer interaction: PASS',
        "Executable=$ExecutablePath",
        "Fixture=$fixturePath",
        "ExplorerHwnd=$($matchedShell.Hwnd)",
        "ExplorerWindowReused=$reused",
        'RealUiAutomationInvoke=True',
        'ZoomOutButtonVisible=True',
        'ZoomPercentageIsReadOnlyText=True',
        'ZoomOutHoverVisualChanged=True',
        "ZoomOutHoverChangedPixels=$hoverChangedPixels",
        'RightRotationInvoked=True',
        "WindowDpi=$windowDpi",
        "WindowScalePercent=$([Math]::Round($windowDpi / 96.0 * 100))",
        'FolderMatched=True',
        'RequestedFileSelected=True'
    ) | Set-Content -LiteralPath (Join-Path $ArtifactRoot 'explorer-interaction-result.txt') -Encoding UTF8
    Write-Host "BlueLink image preview Explorer interaction passed: $ArtifactRoot"
}
finally {
    if ($null -ne $originalCursor) {
        [Windows.Forms.Cursor]::Position = $originalCursor
    }
    if ($null -ne $matchedShell) {
        if ($baseline.ContainsKey($matchedShell.Hwnd)) {
            $previous = $baseline[$matchedShell.Hwnd]
            if (-not [string]::IsNullOrWhiteSpace($previous.LocationUrl)) {
                try { $matchedShell.Window.Navigate2($previous.LocationUrl) } catch { }
            }
        } else {
            try { $matchedShell.Window.Quit() } catch { }
        }
    }
    if ($null -ne $preview) {
        try {
            $windowPattern = [Windows.Automation.WindowPattern]$preview.Window.GetCurrentPattern(
                [Windows.Automation.WindowPattern]::Pattern)
            $windowPattern.Close()
        } catch { }
    }
    if ($null -ne $process) {
        if (-not $process.WaitForExit(8000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit(5000) | Out-Null
        }
        $process.Dispose()
    }
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
    if ($null -ne $shell) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
}
