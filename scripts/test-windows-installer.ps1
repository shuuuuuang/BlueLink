param(
    [string]$Installer,
    [string]$InstallDir = (Join-Path (Split-Path -Parent $PSScriptRoot) '.acceptance\windows-install'),
    [switch]$IsolatedAcceptance,
    [string]$AcceptanceId = 'Acceptance',
    [string]$InstalledTreeReport = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\acceptance\installed-directory-tree.txt')
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
if (-not $Installer) {
    $version = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
    $Installer = Join-Path $root "artifacts\installer\BlueLink-Setup-$version-win-x64.exe"
}
if (-not (Test-Path -LiteralPath $Installer)) { throw "Installer not found: $Installer" }
$Installer = [System.IO.Path]::GetFullPath($Installer)
$InstallDir = [System.IO.Path]::GetFullPath($InstallDir)

$acceptanceRoot = Join-Path $root '.acceptance'
$productionRoot = 'D:\BlueLink'
function Get-ProgramFingerprint([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return '<absent>' }
    return ((Get-ChildItem -LiteralPath $Path -Recurse -File -Force | Where-Object {
        -not $_.FullName.StartsWith((Join-Path $Path 'Download') + '\', [StringComparison]::OrdinalIgnoreCase)
    } | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($Path.TrimEnd('\').Length + 1)
        $relative + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n")
}
$productionFingerprint = Get-ProgramFingerprint $productionRoot
function Assert-ProductionUnchanged {
    if ((Get-ProgramFingerprint $productionRoot) -ne $productionFingerprint) {
        throw "Isolated acceptance modified the protected production directory: $productionRoot"
    }
}

function Get-InstallerProcessTreeIds([int]$RootProcessId) {
    $ids = New-Object 'System.Collections.Generic.HashSet[int]'
    [void]$ids.Add($RootProcessId)
    do {
        $added = $false
        foreach ($processInfo in @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)) {
            if ($ids.Contains([int]$processInfo.ParentProcessId) -and
                $ids.Add([int]$processInfo.ProcessId)) { $added = $true }
        }
    } while ($added)
    return @($ids)
}

function Invoke-OverwriteCancelUiAutomation(
    [string]$InstallerPath, [string]$SnapshotPath, [string[]]$Arguments) {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    if (Test-Path -LiteralPath $SnapshotPath) { Remove-Item -LiteralPath $SnapshotPath -Force }
    $installerProcess = Start-Process -FilePath $InstallerPath `
        -ArgumentList ($Arguments + "SetupOverwriteCancelSnapshotPath=$SnapshotPath") -PassThru
    $clicked = $false
    try {
        $dialogDeadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            $processIds = @(Get-InstallerProcessTreeIds $installerProcess.Id)
            $windowCondition = [Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::ControlTypeProperty,
                [Windows.Automation.ControlType]::Window)
            $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
                [Windows.Automation.TreeScope]::Children, $windowCondition)
            foreach ($window in $windows) {
                if ($processIds -notcontains [int]$window.Current.ProcessId) { continue }
                $cancelNameCondition = [Windows.Automation.PropertyCondition]::new(
                    [Windows.Automation.AutomationElement]::NameProperty, '取消')
                $continueNameCondition = [Windows.Automation.PropertyCondition]::new(
                    [Windows.Automation.AutomationElement]::NameProperty, '关闭并继续安装')
                $cancel = $window.FindFirst(
                    [Windows.Automation.TreeScope]::Descendants, $cancelNameCondition)
                $continue = $window.FindFirst(
                    [Windows.Automation.TreeScope]::Descendants, $continueNameCondition)
                if ($null -eq $cancel -or $null -eq $continue -or
                    -not (Test-Path -LiteralPath $SnapshotPath)) { continue }
                $bounds = $cancel.Current.BoundingRectangle
                if ($cancel.Current.IsOffscreen -or -not $cancel.Current.IsEnabled -or
                    $bounds.Width -lt 60 -or $bounds.Height -lt 24) {
                    throw 'The visible overwrite cancel button is not actionable.'
                }
                $pattern = [Windows.Automation.InvokePattern]$cancel.GetCurrentPattern(
                    [Windows.Automation.InvokePattern]::Pattern)
                $pattern.Invoke()
                $clicked = $true
                break
            }
            if (-not $clicked) { Start-Sleep -Milliseconds 200 }
        } while (-not $clicked -and [DateTime]::UtcNow -lt $dialogDeadline -and -not $installerProcess.HasExited)
        if (-not $clicked) { throw 'Timed out locating the real overwrite cancel button.' }
        $exitDeadline = [DateTime]::UtcNow.AddSeconds(45)
        while (-not $installerProcess.HasExited -and [DateTime]::UtcNow -lt $exitDeadline) {
            Start-Sleep -Milliseconds 200
        }
        if (-not $installerProcess.HasExited) { throw 'Installer did not exit after overwrite cancellation.' }
        return $installerProcess.ExitCode
    }
    finally {
        if (-not $installerProcess.HasExited) {
            foreach ($childProcessId in @(Get-InstallerProcessTreeIds $installerProcess.Id) |
                Sort-Object -Descending) {
                Stop-Process -Id $childProcessId -Force -ErrorAction SilentlyContinue
            }
        }
        $installerProcess.Dispose()
    }
}
$resolvedParent = [System.IO.Path]::GetFullPath((Split-Path -Parent $InstallDir))
if (-not $resolvedParent.StartsWith([System.IO.Path]::GetFullPath($acceptanceRoot), [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use an install directory outside $acceptanceRoot"
}
New-Item -ItemType Directory -Path $acceptanceRoot -Force | Out-Null

$registryPath = if ($IsolatedAcceptance) { 'HKCU:\Software\BlueLink' + $AcceptanceId } else { 'HKCU:\Software\BlueLink' }
$registeredFolder = (Get-ItemProperty -LiteralPath $registryPath -Name InstallFolder -ErrorAction SilentlyContinue).InstallFolder
$trimSeparators = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
$registeredFullPath = if ($registeredFolder) { [System.IO.Path]::GetFullPath($registeredFolder).TrimEnd($trimSeparators) } else { $null }
$requestedFullPath = [System.IO.Path]::GetFullPath($InstallDir).TrimEnd($trimSeparators)
if ($registeredFolder -and -not ($registeredFullPath.Equals(
        $requestedFullPath, [System.StringComparison]::OrdinalIgnoreCase))) {
    throw "A non-acceptance BlueLink installation already exists at $registeredFolder. Uninstall it manually before running this destructive installer test."
}
if ($registeredFolder) {
    # Always pin cleanup to the isolated target. A failed older acceptance
    # bundle may have persisted a foreign InstallFolder variable even though
    # its MSI is already absent; allowing Burn to reuse that stale value can
    # make the postcondition inspect a real production installation.
    $preclean = Start-Process -FilePath $Installer -ArgumentList @(
        '-uninstall', '-quiet', '-norestart', "InstallFolder=$InstallDir") -Wait -PassThru
    if ($preclean.ExitCode -ne 0) { throw "Acceptance pre-clean uninstall failed with code $($preclean.ExitCode)." }
}
if (Test-Path -LiteralPath $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force }

$invalidDir = Join-Path $acceptanceRoot 'illegal-nonempty'
if (Test-Path -LiteralPath $invalidDir) { Remove-Item -LiteralPath $invalidDir -Recurse -Force }
New-Item -ItemType Directory -Path $invalidDir -Force | Out-Null
Set-Content -LiteralPath (Join-Path $invalidDir 'foreign-file.txt') -Value 'installer rejection fixture' -Encoding UTF8
$invalidSetup = Start-Process -FilePath $Installer -ArgumentList @('-quiet', '-norestart', "InstallFolder=$invalidDir", 'CreateDesktopShortcut=0', 'AutoStart=0') -Wait -PassThru
Assert-ProductionUnchanged
if ($invalidSetup.ExitCode -eq 0) { throw 'Installer unexpectedly accepted a non-empty foreign directory.' }
if (Test-Path -LiteralPath (Join-Path $invalidDir 'BlueLink.exe')) { throw 'Rejected directory was modified by the installer.' }
Remove-Item -LiteralPath $invalidDir -Recurse -Force

$arguments = @('-quiet', '-norestart', "InstallFolder=$InstallDir", 'CreateDesktopShortcut=0', 'AutoStart=0')
$setup = Start-Process -FilePath $Installer -ArgumentList $arguments -Wait -PassThru
Assert-ProductionUnchanged
if ($setup.ExitCode -ne 0) { throw "Installer exited with code $($setup.ExitCode)." }

$exe = Join-Path $InstallDir 'BlueLink.exe'
$clientExe = Join-Path $InstallDir 'app\BlueLink.exe'
$uninstallerExe = Join-Path $InstallDir 'Uninstall.exe'
$download = Join-Path $InstallDir 'Download'
$manifest = Join-Path $InstallDir '.bluelink-install.json'
foreach ($required in @($exe, (Join-Path $InstallDir 'BlueLink.exe.config'), $clientExe,
        (Join-Path $InstallDir 'app\BlueLink.dll'), (Join-Path $InstallDir 'app\BlueLink.deps.json'),
        (Join-Path $InstallDir 'app\BlueLink.runtimeconfig.json'), $uninstallerExe,
        (Join-Path $InstallDir 'Uninstall.exe.config'), (Join-Path $InstallDir 'bootstrap\Wpf.Ui.dll'),
        (Join-Path $InstallDir 'bootstrap\runtime-package.json'), $manifest, $download)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Installed payload missing: $required" }
}
if (Get-ChildItem -LiteralPath $InstallDir -File -Filter '*.dll') { throw 'Installed root directory contains DLL files.' }
foreach ($forbidden in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'clrjit.dll')) {
    if (Get-ChildItem -LiteralPath (Join-Path $InstallDir 'app') -Recurse -File -Filter $forbidden) {
        throw "Framework-dependent install contains self-contained runtime file: $forbidden"
    }
}
$manifestValue = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
if ($manifestValue.ProductId -ne 'BlueLink.Desktop' -or $manifestValue.StructureVersion -ne 2 -or
    [string]::IsNullOrWhiteSpace([string]$manifestValue.PayloadFingerprint) -or
    @($manifestValue.PayloadFiles).Count -eq 0 -or
    @($manifestValue.OwnedPaths) -contains 'Download') { throw 'Installed ownership manifest is invalid or claims Download.' }
foreach ($payload in @($manifestValue.PayloadFiles)) {
    $payloadPath = Join-Path $InstallDir ([string]$payload.Path).Replace('/', '\')
    if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf) -or
        (Get-Item -LiteralPath $payloadPath).Length -ne [long]$payload.Length -or
        (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash -ne [string]$payload.Sha256) {
        throw "Installed payload verification failed: $($payload.Path)"
    }
}
if (Test-Path -LiteralPath (Join-Path $InstallDir 'unins000.exe')) {
    throw 'Legacy Inno uninstaller was unexpectedly installed.'
}

# Preserve the exact format variation that previously made a valid deployed
# structure look like a legacy directory: Windows PowerShell emits two spaces
# after the colon and a UTF-8 BOM. Validation must be based on JSON semantics.
$deployedManifest = (Get-Content -LiteralPath $manifest -Raw).Replace(
    '"ProductId": "BlueLink.Desktop"', '"ProductId":  "BlueLink.Desktop"')
[IO.File]::WriteAllText($manifest, $deployedManifest, [Text.UTF8Encoding]::new($true))

$expectedVersion = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
$actualVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
if (-not $actualVersion.StartsWith($expectedVersion, [System.StringComparison]::Ordinal)) {
    throw "Installed version mismatch. Expected $expectedVersion, got $actualVersion"
}

$smoke = Start-Process -FilePath $exe -ArgumentList '--startup-smoke-test' -WindowStyle Hidden -Wait -PassThru
if ($smoke.ExitCode -ne 0) { throw "Installed application smoke test failed with code $($smoke.ExitCode)." }

# A canceled/failed older MSI transaction can leave a seven-digit .rbf copy of
# BlueLink.exe beside the application. It is product-owned content and must not
# block a later overwrite, but it must survive an overwrite confirmation cancel.
$rollbackFile = Join-Path $InstallDir '4d54a9e.rbf'
Copy-Item -LiteralPath $exe -Destination $rollbackFile -Force
$rollbackHash = (Get-FileHash -LiteralPath $rollbackFile -Algorithm SHA256).Hash

Add-Type -AssemblyName System.Drawing
$icon = [System.Drawing.Icon]::ExtractAssociatedIcon($exe)
if ($null -eq $icon -or $icon.Width -lt 32) { throw 'Installed executable has no usable product icon.' }
$icon.Dispose()

$runningApplication = Start-Process -FilePath $clientExe -ArgumentList '--acceptance-background' -PassThru
Start-Sleep -Milliseconds 1800
if ($runningApplication.HasExited) { throw "Installed application exited before overwrite test (code $($runningApplication.ExitCode))." }

$beforeCancelHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
$beforeCancelManifestHash = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash
$cancelSnapshot = Join-Path $acceptanceRoot 'installer-overwrite-cancel.png'
$cancelExitCode = Invoke-OverwriteCancelUiAutomation `
    -InstallerPath $Installer -SnapshotPath $cancelSnapshot -Arguments @('-repair')
Assert-ProductionUnchanged
if ($cancelExitCode -ne 0) { throw "Canceled overwrite test exited with code $cancelExitCode." }
if (-not (Test-Path -LiteralPath $cancelSnapshot) -or (Get-Item -LiteralPath $cancelSnapshot).Length -lt 5000) {
    throw 'The WPF UI overwrite confirmation snapshot was not produced.'
}
Start-Sleep -Milliseconds 800
$runningApplication.Refresh()
if ($runningApplication.HasExited) { throw 'Canceling the overwrite confirmation unexpectedly closed the running application.' }
if ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ne $beforeCancelHash) {
    throw 'Canceling the overwrite confirmation modified the installed executable.'
}
if ((Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash -ne $beforeCancelManifestHash) {
    throw 'Canceling the overwrite confirmation modified the ownership manifest.'
}
if (-not (Test-Path -LiteralPath $rollbackFile) -or
    (Get-FileHash -LiteralPath $rollbackFile -Algorithm SHA256).Hash -ne $rollbackHash) {
    throw 'Canceling the overwrite confirmation modified the product rollback file.'
}
# Burn can spend several seconds unloading the managed BA AppDomain after its
# visible process reports success. Do not launch a second instance on the same
# timestamp while the engine is still releasing its global synchronization
# objects and clean-room directory.
Start-Sleep -Milliseconds 1500
$repair = Start-Process -FilePath $Installer -ArgumentList @('-repair', '-quiet', '-norestart') -Wait -PassThru
Assert-ProductionUnchanged
if ($repair.ExitCode -ne 0) { throw "Repair exited with code $($repair.ExitCode)." }
$runningApplication.Refresh()
if (-not $runningApplication.HasExited) {
    Stop-Process -Id $runningApplication.Id -Force -ErrorAction SilentlyContinue
    throw 'Overwrite repair did not close the running installed application.'
}
$runningApplication.Dispose()
if (Test-Path -LiteralPath $rollbackFile) { throw 'Successful overwrite left the stale BlueLink rollback file behind.' }
$repairSmoke = Start-Process -FilePath $exe -ArgumentList '--startup-smoke-test' -WindowStyle Hidden -Wait -PassThru
if ($repairSmoke.ExitCode -ne 0) { throw "Repaired application smoke test failed with code $($repairSmoke.ExitCode)." }

$preservedDownload = Join-Path $download 'acceptance-user-file.txt'
Set-Content -LiteralPath $preservedDownload -Value 'owned by acceptance user fixture' -Encoding UTF8
$treeRoot = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
$treeLines = Get-ChildItem -LiteralPath $treeRoot -Recurse -Force | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($treeRoot.Length).TrimStart('\')
    if ($_.PSIsContainer) { $relative + '\' } else { "{0} ({1} bytes)" -f $relative,$_.Length }
}
$reportDirectory = Split-Path -Parent $InstalledTreeReport
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$treeLines | Set-Content -LiteralPath $InstalledTreeReport -Encoding UTF8

$uninstall = Start-Process -FilePath $Installer -ArgumentList @('-uninstall', '-quiet', '-norestart') -Wait -PassThru
Assert-ProductionUnchanged
if ($uninstall.ExitCode -ne 0) { throw "Uninstaller exited with code $($uninstall.ExitCode)." }
if (Test-Path -LiteralPath $exe) { throw 'Uninstaller left the application executable behind.' }
if (Test-Path -LiteralPath (Join-Path $InstallDir 'app')) { throw 'Uninstaller left the app directory behind.' }
if (Test-Path -LiteralPath (Join-Path $InstallDir 'bootstrap')) { throw 'Uninstaller left the bootstrap directory behind.' }
if (-not (Test-Path -LiteralPath $preservedDownload)) { throw 'Uninstaller deleted a user file from Download.' }

if (Test-Path -LiteralPath $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
Write-Host "Installer acceptance passed: $Installer"
Write-Host "Installed version: $actualVersion"
Write-Host 'Verified: illegal-directory rejection, semantic JSON ownership parsing with UTF-8 BOM/two-space formatting, root/app/bootstrap structure, root has no DLL, framework-dependent runtimeconfig payload, Download preservation, app startup, recognized BlueLink rollback file, UI-Automation cancellation leaves files/process untouched, stale rollback cleanup after overwrite, running-app overwrite, embedded icon, repair, Burn uninstall, no Inno payload.'
