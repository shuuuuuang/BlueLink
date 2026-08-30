param(
    [Parameter(Mandatory = $true)][string]$Installer,
    [string]$InstallDir = (Join-Path (Split-Path -Parent $PSScriptRoot) '.acceptance\root-uninstall'),
    [ValidatePattern('^[A-Za-z0-9]+$')]
    [string]$AcceptanceId = 'Acceptance'
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$acceptanceRoot = [IO.Path]::GetFullPath((Join-Path $root '.acceptance'))
$installFullPath = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
if (-not $installFullPath.StartsWith($acceptanceRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to install outside $acceptanceRoot"
}
$InstallDir = $installFullPath

function Wait-Element([scriptblock]$resolve, [int]$timeoutSeconds, [string]$description) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    do {
        $element = & $resolve
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "UI Automation timed out waiting for $description."
}

function Find-Window([string]$name) {
    $condition = New-Object Windows.Automation.AndCondition(
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Window)),
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name)))
    [Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [Windows.Automation.TreeScope]::Children, $condition)
}

function Find-Button($rootElement, [string]$name) {
    $condition = New-Object Windows.Automation.AndCondition(
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Button)),
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name)))
    $rootElement.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-Buttons($rootElement, [string]$name) {
    $condition = New-Object Windows.Automation.AndCondition(
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Button)),
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name)))
    $rootElement.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Invoke-Button($button, [string]$description) {
    if ($null -eq $button) { throw "UI Automation did not find $description." }
    $pattern = $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
    if ($null -eq $pattern) { throw "$description does not expose InvokePattern." }
    ([Windows.Automation.InvokePattern]$pattern).Invoke()
}

if (Test-Path -LiteralPath $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
$install = Start-Process -FilePath $Installer -ArgumentList @(
    '-quiet', '-norestart', "InstallFolder=$InstallDir", 'CreateDesktopShortcut=0', 'AutoStart=0') -Wait -PassThru
if ($install.ExitCode -ne 0) { throw "Acceptance install failed with code $($install.ExitCode)." }

$client = Join-Path $InstallDir 'app\BlueLink.exe'
$uninstaller = Join-Path $InstallDir 'Uninstall.exe'
$downloadFixture = Join-Path $InstallDir 'Download\root-uninstall-user-file.txt'
Set-Content -LiteralPath $downloadFixture -Value 'preserve this user-owned download' -Encoding UTF8
$running = Start-Process -FilePath $client -ArgumentList '--acceptance-background' -PassThru
Start-Sleep -Milliseconds 1500
if ($running.HasExited) { throw 'Installed client exited before root uninstaller test.' }

Start-Process -FilePath $uninstaller | Out-Null
# Do not initialize the UI Automation client while the detached WPF process is
# creating its first HWND. First require the real top-level Win32 window, then
# attach UIA and exercise the visible controls.
$windowProcess = Wait-Element {
    Get-Process -Name 'Uninstall' -ErrorAction SilentlyContinue | Where-Object {
        $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -eq '卸载蓝联'
    } | Select-Object -First 1
} 15 'the root uninstaller Win32 window handle'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$mainWindow = Wait-Element { Find-Window '卸载蓝联' } 30 'the root uninstaller window'
Add-Type -AssemblyName System.Drawing
$uninstallerSnapshot = Join-Path $root 'artifacts\acceptance\uninstaller-main.png'
$snapshotDirectory = Split-Path -Parent $uninstallerSnapshot
New-Item -ItemType Directory -Path $snapshotDirectory -Force | Out-Null
$windowBounds = $mainWindow.Current.BoundingRectangle
$snapshot = New-Object Drawing.Bitmap([Math]::Max(1, [int][Math]::Ceiling($windowBounds.Width)),
    [Math]::Max(1, [int][Math]::Ceiling($windowBounds.Height)))
$graphics = [Drawing.Graphics]::FromImage($snapshot)
try {
    $graphics.CopyFromScreen([int][Math]::Floor($windowBounds.X), [int][Math]::Floor($windowBounds.Y),
        0, 0, $snapshot.Size, [Drawing.CopyPixelOperation]::SourceCopy)
    $snapshot.Save($uninstallerSnapshot, [Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $snapshot.Dispose()
}
if ((Get-Item -LiteralPath $uninstallerSnapshot).Length -lt 20000) {
    throw 'The current root uninstaller screenshot was not captured correctly.'
}
Invoke-Button (Find-Button $mainWindow '卸载') 'the visible root uninstaller button'

# WPF UI MessageBox is exposed as an owned Window inside the owner's
# automation subtree; its visual Title is not the desktop window Name. Once
# the dialog is present there are at least two semantic “卸载” buttons and the
# owned dialog button is first in tree order.
$confirmButton = Wait-Element {
    $window = Find-Window '卸载蓝联'
    if ($null -eq $window) { return $null }
    $buttons = Find-Buttons $window '卸载'
    if ($buttons.Count -ge 2) { return $buttons.Item(0) }
    return $null
} 15 'the official WPF UI confirmation button'
Invoke-Button $confirmButton 'the visible confirmation button'

$closeButton = Wait-Element {
    $window = Find-Window '卸载蓝联'
    if ($null -eq $window) { return $null }
    Find-Button $window '关闭'
} 120 'the successful uninstall Close button'
Invoke-Button $closeButton 'the successful uninstall Close button'
Start-Sleep -Milliseconds 800

$running.Refresh()
if (-not $running.HasExited) {
    Stop-Process -Id $running.Id -Force -ErrorAction SilentlyContinue
    throw 'Root uninstaller reported success while the installed client was still running.'
}
$running.Dispose()

foreach ($removed in @(
    (Join-Path $InstallDir 'BlueLink.exe'), (Join-Path $InstallDir 'Uninstall.exe'),
    (Join-Path $InstallDir '.bluelink-install.json'), (Join-Path $InstallDir 'app'),
    (Join-Path $InstallDir 'bootstrap'))) {
    if (Test-Path -LiteralPath $removed) { throw "Root uninstaller left program content behind: $removed" }
}
$acceptanceRegistryPath = 'HKCU:\Software\BlueLink' + $AcceptanceId
if (Test-Path -LiteralPath $acceptanceRegistryPath) {
    throw 'Root uninstaller left the exact acceptance product registration behind.'
}
if (-not (Test-Path -LiteralPath $downloadFixture)) {
    throw 'Root uninstaller deleted the user-owned Download fixture.'
}

$resultPath = Join-Path $root 'artifacts\acceptance\root-uninstaller-result.txt'
@(
    'Root Uninstall.exe UI Automation acceptance: PASS'
    "InstallDir=$installFullPath"
    'Running client stopped=True'
    'Program files removed=True'
    'Exact registration removed=True'
    'Download fixture preserved=True'
    "Current UI screenshot=$uninstallerSnapshot"
) | Set-Content -LiteralPath $resultPath -Encoding UTF8

Remove-Item -LiteralPath $InstallDir -Recurse -Force
Write-Host "Root Uninstall.exe UI Automation acceptance passed: $resultPath"
