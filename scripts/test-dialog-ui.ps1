param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$ExecutablePath = '',
    [ValidateSet('General', 'Trust')]
    [string]$Mode = 'General',
    [Alias('ArtifactRoot')]
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $root "artifacts\qa\dialog-uia\$stamp-$($Mode.ToLowerInvariant())-$($Configuration.ToLowerInvariant())"
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

function Text-FromCodePoints([int[]]$CodePoints) {
    return -join ($CodePoints | ForEach-Object { [char]$_ })
}

function Find-Dialog([int]$ProcessId, [string]$PrimaryName, [int]$TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $rootElement = [System.Windows.Automation.AutomationElement]::RootElement
    $processCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $buttonCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    while ([DateTime]::UtcNow -lt $deadline) {
        $windows = $rootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children, $processCondition)
        foreach ($window in $windows) {
            $buttons = $window.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
            foreach ($button in $buttons) {
                if ($button.Current.Name -eq $PrimaryName) {
                    return $window
                }
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for the official WPF UI dialog for process $ProcessId."
}

function Find-Button($Dialog, [string]$Name) {
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $typeCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $condition = New-Object System.Windows.Automation.AndCondition($nameCondition, $typeCondition)
    return $Dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Assert-Actionable($Element, [string]$Description) {
    if ($null -eq $Element) { throw "$Description was not found." }
    $rect = $Element.Current.BoundingRectangle
    if ($Element.Current.IsOffscreen -or -not $Element.Current.IsEnabled -or
        $rect.Width -lt 1 -or $rect.Height -lt 1) {
        throw "$Description is not visible and actionable."
    }
}

function Save-ElementScreenshot($Element, [string]$Path) {
    $rect = $Element.Current.BoundingRectangle
    $width = [Math]::Max(1, [int][Math]::Ceiling($rect.Width))
    $height = [Math]::Max(1, [int][Math]::Ceiling($rect.Height))
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen(
            [int][Math]::Floor($rect.Left), [int][Math]::Floor($rect.Top), 0, 0,
            (New-Object System.Drawing.Size($width, $height)))
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$exe = if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    Join-Path $root "windows\BlueLink.App\bin\$Configuration\net8.0-windows10.0.19041.0\BlueLink.exe"
} else {
    [IO.Path]::GetFullPath($ExecutablePath)
}
if (-not (Test-Path -LiteralPath $exe)) { throw "BlueLink executable is missing: $exe" }
$appReport = Join-Path $OutputDirectory 'app-dialog-result.json'
$uiaReport = Join-Path $OutputDirectory 'dialog-uia-result.json'
$screenshot = Join-Path $OutputDirectory 'official-message-box.png'
$primaryName = if ($Mode -eq 'Trust') {
    Text-FromCodePoints @(0x786E, 0x8BA4, 0x5E76, 0x4FE1, 0x4EFB)
} else {
    Text-FromCodePoints @(0x786E, 0x8BA4)
}
$cancelName = if ($Mode -eq 'Trust') {
    Text-FromCodePoints @(0x62D2, 0x7EDD)
} else {
    Text-FromCodePoints @(0x53D6, 0x6D88)
}
$argumentPrefix = if ($Mode -eq 'Trust') { '--trust-dialog-ui-smoke-test=' } else { '--dialog-ui-smoke-test=' }
$process = Start-Process -FilePath $exe -ArgumentList "$argumentPrefix$appReport" -PassThru
try {
    $dialog = Find-Dialog -ProcessId $process.Id -PrimaryName $primaryName -TimeoutSeconds 15
    $primary = Find-Button -Dialog $dialog -Name $primaryName
    $cancel = Find-Button -Dialog $dialog -Name $cancelName
    Assert-Actionable -Element $primary -Description 'Primary dialog button'
    Assert-Actionable -Element $cancel -Description 'Cancel dialog button'
    Save-ElementScreenshot -Element $dialog -Path $screenshot
    $primaryRect = $primary.Current.BoundingRectangle
    $cancelRect = $cancel.Current.BoundingRectangle

    $pattern = $primary.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    if ($null -eq $pattern) { throw 'Primary dialog button does not expose InvokePattern.' }
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
    if (-not $process.WaitForExit(20000)) {
        throw 'BlueLink did not exit after the real primary button was invoked.'
    }
    if ($process.ExitCode -ne 0) { throw "BlueLink dialog smoke process failed: $($process.ExitCode)" }
    if (-not (Test-Path -LiteralPath $appReport)) { throw 'The application dialog result report is missing.' }
    $appResult = Get-Content -LiteralPath $appReport -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $appResult.Confirmed -or -not $appResult.OfficialMessageBox) {
        throw 'The application did not record the official primary-button result.'
    }
    if ($Mode -eq 'Trust' -and -not $appResult.TrustConfirmation) {
        throw 'The application did not record the trust-confirmation workflow.'
    }

    [ordered]@{
        Configuration = $Configuration
        Mode = $Mode
        Executable = $exe
        ProcessExitCode = $process.ExitCode
        DialogName = $dialog.Current.Name
        PrimaryButton = $primaryName
        CancelButton = $cancelName
        PrimaryBounds = [ordered]@{ X=$primaryRect.Left; Y=$primaryRect.Top; Width=$primaryRect.Width; Height=$primaryRect.Height }
        CancelBounds = [ordered]@{ X=$cancelRect.Left; Y=$cancelRect.Top; Width=$cancelRect.Width; Height=$cancelRect.Height }
        Screenshot = $screenshot
        AppReport = $appReport
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $uiaReport -Encoding UTF8
    Write-Host "Official WPF UI $Mode dialog automation passed: $OutputDirectory"
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
}
