param(
    [string]$Configuration = 'Release',
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = 'D:\Tool\dotnet-sdk-8\dotnet.exe'
$nugetConfig = Join-Path $projectRoot 'NuGet.config'
$stageRoot = Join-Path $projectRoot 'artifacts\windows\win-x64'
$appPublish = Join-Path $projectRoot '.build\windows-app-publish'
$runtimeCache = Join-Path $projectRoot '.build\dotnet-runtime'
$acceptanceDir = Join-Path $projectRoot 'artifacts\acceptance'
$version = (Get-Content -LiteralPath (Join-Path $projectRoot 'VERSION') -Raw).Trim()

function Get-DeterministicGuid([string]$purpose, [string]$value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes("BlueLink|$purpose|$value"))[0..15] }
    finally { $sha.Dispose() }
    $bytes[7] = ($bytes[7] -band 0x0F) -bor 0x50
    $bytes[8] = ($bytes[8] -band 0x3F) -bor 0x80
    return ([Guid]::new([byte[]]$bytes)).ToString('B').ToUpperInvariant()
}

function Get-DescendantProcessIds([int]$RootProcessId) {
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

function Invoke-OverwriteDialogAutomation([string]$InstallerPath, [string]$SnapshotPath) {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    if (Test-Path -LiteralPath $SnapshotPath) { Remove-Item -LiteralPath $SnapshotPath -Force }
    $installerProcess = Start-Process -FilePath $InstallerPath `
        -ArgumentList "SetupOverwriteCancelSnapshotPath=$SnapshotPath" -PassThru
    $clicked = $false
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            $processIds = @(Get-DescendantProcessIds $installerProcess.Id)
            $windowCondition = [Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::ControlTypeProperty,
                [Windows.Automation.ControlType]::Window)
            $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
                [Windows.Automation.TreeScope]::Children, $windowCondition)
            foreach ($window in $windows) {
                if ($processIds -notcontains [int]$window.Current.ProcessId) { continue }
                $buttonTypeCondition = [Windows.Automation.PropertyCondition]::new(
                    [Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [Windows.Automation.ControlType]::Button)
                $cancelNameCondition = [Windows.Automation.PropertyCondition]::new(
                    [Windows.Automation.AutomationElement]::NameProperty, '取消')
                $cancelCondition = [Windows.Automation.AndCondition]::new(
                    $buttonTypeCondition, $cancelNameCondition)
                $cancel = $window.FindFirst(
                    [Windows.Automation.TreeScope]::Descendants, $cancelCondition)
                $continueNameCondition = [Windows.Automation.PropertyCondition]::new(
                    [Windows.Automation.AutomationElement]::NameProperty, '关闭并继续安装')
                $continue = $window.FindFirst([Windows.Automation.TreeScope]::Descendants,
                    $continueNameCondition)
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
        } while (-not $clicked -and [DateTime]::UtcNow -lt $deadline -and -not $installerProcess.HasExited)
        if (-not $clicked) { throw 'Timed out locating the real overwrite cancel button.' }
        $exitDeadline = [DateTime]::UtcNow.AddSeconds(45)
        while (-not $installerProcess.HasExited -and [DateTime]::UtcNow -lt $exitDeadline) {
            Start-Sleep -Milliseconds 200
        }
        if (-not $installerProcess.HasExited) { throw 'Installer did not exit after the real cancel button was invoked.' }
        if ($installerProcess.ExitCode -ne 0) { throw "Overwrite UI Automation smoke exited with code $($installerProcess.ExitCode)." }
    }
    finally {
        if (-not $installerProcess.HasExited) {
            foreach ($processId in @(Get-DescendantProcessIds $installerProcess.Id) | Sort-Object -Descending) {
                Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
            }
        }
        $installerProcess.Dispose()
    }
}

$productCode = Get-DeterministicGuid 'MsiProduct' $version
$bundleProviderKey = "BlueLink.Desktop.Bundle.$version"

$appProject = Join-Path $projectRoot 'windows\BlueLink.App\BlueLink.App.csproj'
$launcherProject = Join-Path $projectRoot 'installer\BlueLink.Launcher\BlueLink.Launcher.csproj'
$uninstallProject = Join-Path $projectRoot 'installer\BlueLink.Uninstall\BlueLink.Uninstall.csproj'
$setupUiProject = Join-Path $projectRoot 'installer\BlueLink.SetupUI\BlueLink.SetupUI.csproj'
$ownershipTestsProject = Join-Path $projectRoot 'installer\BlueLink.Installation.Tests\BlueLink.Installation.Tests.csproj'
$packageProject = Join-Path $projectRoot 'installer\BlueLink.Package\BlueLink.Package.wixproj'
$bundleProject = Join-Path $projectRoot 'installer\BlueLink.Bundle\BlueLink.Bundle.wixproj'
$appProjectText = Get-Content -LiteralPath $appProject -Raw
$appUsesWpfUi = $appProjectText -match '<PackageReference Include="WPF-UI" Version="4\.3\.0"'
$scopeMatch = [regex]::Match($appProjectText, '<BlueLinkWpfUiMigrationScope>\s*([^<]+)\s*</BlueLinkWpfUiMigrationScope>')
$uiMigrationScope = if (-not $appUsesWpfUi) {
    'None'
} elseif ($scopeMatch.Success) {
    $scopeMatch.Groups[1].Value.Trim()
} else {
    'Full'
}
if ($uiMigrationScope -notin @('None', 'SettingsOnly', 'Full')) {
    throw "Unsupported BlueLink WPF UI migration scope: $uiMigrationScope"
}
Write-Host "Windows WPF UI migration scope: $uiMigrationScope"

if (-not (Test-Path -LiteralPath $dotnet)) { throw "The .NET SDK was not found: $dotnet" }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.nuget-mirror-test'

& (Join-Path $projectRoot 'scripts\verify-version.ps1') -ProjectRoot $projectRoot
& (Join-Path $projectRoot 'scripts\verify-settings-wiring.ps1')
if ($uiMigrationScope -ne 'None') {
    & (Join-Path $projectRoot 'scripts\verify-ui-contract.ps1') -Scope $uiMigrationScope
} else {
    Write-Host '[PASS] Windows client matches the preserved pre-migration UI source; migration-only UI gates are skipped.'
}
& (Join-Path $projectRoot 'scripts\verify-installer-contract.ps1')

foreach ($directory in @($stageRoot, $appPublish)) {
    $resolved = [IO.Path]::GetFullPath($directory)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath($projectRoot), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear build directory outside project: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    New-Item -ItemType Directory -Path $resolved -Force | Out-Null
}
New-Item -ItemType Directory -Path $runtimeCache, $acceptanceDir -Force | Out-Null

& $dotnet restore $appProject --configfile $nugetConfig -r win-x64 --ignore-failed-sources -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw "NuGet restore failed: $appProject" }
foreach ($project in @($launcherProject, $uninstallProject, $setupUiProject, $ownershipTestsProject, $packageProject, $bundleProject)) {
    & $dotnet restore $project --configfile $nugetConfig --ignore-failed-sources -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "NuGet restore failed: $project" }
}

& $dotnet publish $appProject -c $Configuration -r win-x64 --self-contained false `
    -p:PublishSingleFile=false -p:PublishTrimmed=false -p:UseAppHost=true `
    -p:DebugType=None -p:DebugSymbols=false -o $appPublish --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Framework-dependent Windows publish failed.' }

foreach ($project in @($launcherProject, $uninstallProject, $setupUiProject)) {
    & $dotnet build $project -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "WPF bootstrap component compilation failed: $project" }
}

$launcherOutput = Join-Path $projectRoot "installer\BlueLink.Launcher\bin\$Configuration\net472\win-x64"
$uninstallOutput = Join-Path $projectRoot "installer\BlueLink.Uninstall\bin\$Configuration\net472\win-x64"
$appStage = Join-Path $stageRoot 'app'
$bootstrapStage = Join-Path $stageRoot 'bootstrap'
$downloadStage = Join-Path $stageRoot 'Download'
New-Item -ItemType Directory -Path $appStage, $bootstrapStage, $downloadStage -Force | Out-Null
Copy-Item -Path (Join-Path $appPublish '*') -Destination $appStage -Recurse -Force
Copy-Item -LiteralPath (Join-Path $launcherOutput 'BlueLink.exe'), (Join-Path $launcherOutput 'BlueLink.exe.config'), `
    (Join-Path $uninstallOutput 'Uninstall.exe'), (Join-Path $uninstallOutput 'Uninstall.exe.config') -Destination $stageRoot -Force
foreach ($source in @($launcherOutput, $uninstallOutput)) {
    Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object {
        $_.Extension -ne '.pdb' -and $_.Name -notin @('BlueLink.exe', 'BlueLink.exe.config', 'Uninstall.exe', 'Uninstall.exe.config')
    } | ForEach-Object {
        $sourceUri = [Uri]::new(([IO.Path]::GetFullPath($source).TrimEnd('\') + '\'))
        $relative = [Uri]::UnescapeDataString($sourceUri.MakeRelativeUri([Uri]::new($_.FullName)).ToString()).Replace('/', '\')
        $destination = Join-Path $bootstrapStage $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
    }
}

if ($uiMigrationScope -in @('SettingsOnly', 'Full')) {
    $settingsPublishAcceptance = Join-Path $acceptanceDir 'settings-published-current'
    $resolvedSettingsAcceptance = [IO.Path]::GetFullPath($settingsPublishAcceptance)
    $resolvedAcceptanceRoot = [IO.Path]::GetFullPath($acceptanceDir).TrimEnd('\') + '\'
    if (-not $resolvedSettingsAcceptance.StartsWith($resolvedAcceptanceRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear settings acceptance directory outside artifacts: $resolvedSettingsAcceptance"
    }
    if (Test-Path -LiteralPath $resolvedSettingsAcceptance) {
        Remove-Item -LiteralPath $resolvedSettingsAcceptance -Recurse -Force
    }
    & (Join-Path $projectRoot 'scripts\test-settings-ui.ps1') `
        -ExecutablePath (Join-Path $appStage 'BlueLink.exe') -ArtifactRoot $resolvedSettingsAcceptance
    if ($LASTEXITCODE -ne 0) { throw 'Published Settings UI Automation acceptance failed.' }
}

# Resolve the pinned immutable x64 Windows Desktop Runtime 8 patch from
# Microsoft's release metadata. The checked-in lock is accepted offline only
# when the cached payload still matches the previously resolved official
# SHA-512 and Microsoft Authenticode signature.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$runtimeLockPath = Join-Path $projectRoot 'installer\dotnet-runtime-lock.json'
$runtimeLock = Get-Content -LiteralPath $runtimeLockPath -Raw | ConvertFrom-Json
$metadataUrl = [string]$runtimeLock.MetadataUrl
$runtimeVersion = [string]$runtimeLock.Version
$runtimeFile = [pscustomobject]@{
    name = [string]$runtimeLock.FileName
    url = [string]$runtimeLock.Url
    hash = [string]$runtimeLock.Sha512
}
try { $metadata = Invoke-RestMethod -Uri $metadataUrl -UseBasicParsing }
catch {
    $metadata = $null
    Write-Warning "Microsoft release metadata is temporarily unavailable; using the pinned, hash-locked runtime metadata: $($_.Exception.Message)"
}
if ($metadata) {
    $release = $metadata.releases | Where-Object { $_.windowsdesktop.version -eq $runtimeVersion } | Select-Object -First 1
    $officialFile = $release.windowsdesktop.files | Where-Object {
        $_.rid -eq [string]$runtimeLock.Rid -and $_.name -like '*.exe'
    } | Select-Object -First 1
    if (-not $officialFile -or -not $officialFile.url -or -not $officialFile.hash) {
        throw "Microsoft release metadata did not contain the pinned Desktop Runtime $runtimeVersion x64 installer."
    }
    if (-not ([string]$officialFile.url).Equals([string]$runtimeLock.Url, [StringComparison]::Ordinal) -or
        -not ([string]$officialFile.hash).Equals([string]$runtimeLock.Sha512, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The pinned Desktop Runtime $runtimeVersion URL or SHA-512 no longer matches Microsoft metadata."
    }
    $runtimeFile = $officialFile
}
$runtimePayload = Join-Path $runtimeCache ([string]$runtimeLock.FileName)
if (Test-Path -LiteralPath $runtimePayload) {
    $cachedHash = (Get-FileHash -LiteralPath $runtimePayload -Algorithm SHA512).Hash
    if (-not $cachedHash.Equals([string]$runtimeFile.hash, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $runtimePayload -Force
    }
}
if (-not (Test-Path -LiteralPath $runtimePayload)) {
    & curl.exe --fail --location --retry 3 --output $runtimePayload ([string]$runtimeFile.url)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to download the Microsoft .NET Desktop Runtime payload.' }
}
$actualRuntimeHash = (Get-FileHash -LiteralPath $runtimePayload -Algorithm SHA512).Hash
if (-not $actualRuntimeHash.Equals([string]$runtimeFile.hash, [StringComparison]::OrdinalIgnoreCase)) { throw 'Downloaded .NET runtime SHA-512 does not match Microsoft metadata.' }
$runtimeSignature = Get-AuthenticodeSignature -LiteralPath $runtimePayload
if ($runtimeSignature.Status.ToString() -ne 'Valid' -or -not $runtimeSignature.SignerCertificate -or
    $runtimeSignature.SignerCertificate.Subject.IndexOf('Microsoft', [StringComparison]::OrdinalIgnoreCase) -lt 0) {
    throw "Downloaded .NET runtime does not have a valid Microsoft Authenticode signature: $($runtimeSignature.Status)"
}
$runtimeLength = (Get-Item -LiteralPath $runtimePayload).Length
if ($runtimeLength -ne [long]$runtimeLock.Size) { throw 'Cached .NET runtime size does not match the pinned Microsoft package.' }
$runtimeDisplaySize = ($runtimeLength / 1MB).ToString('0.0', [Globalization.CultureInfo]::InvariantCulture) + ' MB'
$runtimeInfo = [ordered]@{
    Version = $runtimeVersion
    Url = [string]$runtimeFile.url
    Sha512 = $actualRuntimeHash
    Size = $runtimeLength
    FileName = [string]$runtimeFile.name
    ManualUrl = [string]$runtimeLock.ManualUrl
}
$runtimeInfo | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bootstrapStage 'runtime-package.json') -Encoding utf8

$manifestPath = Join-Path $stageRoot '.bluelink-install.json'
$generatedWix = Join-Path $projectRoot 'installer\BlueLink.Package\Package.Generated.wxs'
& (Join-Path $projectRoot 'scripts\generate-wix-payload.ps1') `
    -AppPublishDir $appStage -BootstrapDir $bootstrapStage -ManifestPath $manifestPath `
    -OutputFile $generatedWix -Version $version
$generatedManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$payloadFingerprint = [string]$generatedManifest.PayloadFingerprint
if ($payloadFingerprint -notmatch '^[0-9A-Fa-f]{64}$') {
    throw 'Generated install manifest does not contain a valid payload fingerprint.'
}
$releaseIdentityPath = Join-Path $projectRoot 'installer\release-payload-lock.json'
if (Test-Path -LiteralPath $releaseIdentityPath) {
    $lockedIdentity = Get-Content -LiteralPath $releaseIdentityPath -Raw | ConvertFrom-Json
    $currentVersion = [Version]$version
    $lockedVersion = [Version]([string]$lockedIdentity.Version)
    if ($currentVersion -lt $lockedVersion) {
        throw "Release version $version is older than the locked release version $lockedVersion."
    }
    if ($currentVersion -eq $lockedVersion -and
        -not $payloadFingerprint.Equals([string]$lockedIdentity.PayloadFingerprint,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release $version already identifies a different payload. Increment VERSION before building another distributable installer."
    }
}

& $dotnet build $ownershipTestsProject -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Install ownership test compilation failed.' }
$ownershipTests = Join-Path $projectRoot "installer\BlueLink.Installation.Tests\bin\$Configuration\net472\win-x64\BlueLink.Installation.Tests.exe"
$ownershipTestRoot = Join-Path $projectRoot '.acceptance\install-ownership-tests'
& $ownershipTests $stageRoot $ownershipTestRoot
if ($LASTEXITCODE -ne 0) { throw 'Install ownership semantic tests failed.' }

$runtimeConfig = Get-Content -LiteralPath (Join-Path $appStage 'BlueLink.runtimeconfig.json') -Raw | ConvertFrom-Json
$desktopFramework = @($runtimeConfig.runtimeOptions.frameworks) | Where-Object name -eq 'Microsoft.WindowsDesktop.App' | Select-Object -First 1
if (-not $desktopFramework -or -not ([string]$desktopFramework.version).StartsWith('8.')) {
    throw 'Published runtimeconfig does not require Microsoft.WindowsDesktop.App 8.x.'
}
$forbiddenRuntimeFiles = @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'clrjit.dll')
foreach ($forbidden in $forbiddenRuntimeFiles) {
    if (Get-ChildItem -LiteralPath $appStage -Recurse -File -Filter $forbidden) { throw "Framework-dependent publish contains self-contained runtime file: $forbidden" }
}
if (Get-ChildItem -LiteralPath $stageRoot -File -Filter '*.dll') { throw 'The installed root directory contains DLL files.' }

$rootSmoke = Start-Process -FilePath (Join-Path $stageRoot 'BlueLink.exe') -ArgumentList '--startup-smoke-test' -WindowStyle Hidden -Wait -PassThru
if ($rootSmoke.ExitCode -ne 0) { throw "Root launcher startup smoke failed with exit code $($rootSmoke.ExitCode)." }

foreach ($snapshot in @(
    @{ Name = 'windows-ui-expanded.png'; Extra = @() },
    @{ Name = 'windows-ui-collapsed.png'; Extra = @('--ui-collapsed') },
    @{ Name = 'windows-ui-compact.png'; Extra = @('--ui-compact', '--ui-collapsed') }
)) {
    $snapshotPath = Join-Path $acceptanceDir $snapshot.Name
    $arguments = @("--ui-smoke-test=$snapshotPath") + $snapshot.Extra
    $visual = Start-Process -FilePath (Join-Path $appStage 'BlueLink.exe') -ArgumentList $arguments -Wait -PassThru
    if ($visual.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $snapshotPath) -or (Get-Item -LiteralPath $snapshotPath).Length -lt 40000) {
        throw "Windows stateful UI smoke failed: $($snapshot.Name)"
    }
}
$visualTests = @(
    @{ Name = 'windows-image-preview.png'; MinBytes = 40000; Arguments = @("--preview-ui-smoke-test=$(Join-Path $acceptanceDir 'windows-image-preview.png')", "--preview-source=$(Join-Path $projectRoot 'design\brand\final\bluelink-final-logo.png')") }
)
if ($uiMigrationScope -eq 'Full') {
    # Settings and dialogs are verified below through real UI Automation.
}
foreach ($visualTest in $visualTests) {
    $visual = Start-Process -FilePath (Join-Path $appStage 'BlueLink.exe') -ArgumentList $visualTest.Arguments -Wait -PassThru
    $path = Join-Path $acceptanceDir $visualTest.Name
    if ($visual.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -lt $visualTest.MinBytes) { throw "Windows visual smoke failed: $($visualTest.Name)" }
}

if ($uiMigrationScope -eq 'Full') {
    foreach ($dialogMode in @('General', 'Trust')) {
        $dialogAcceptance = Join-Path $acceptanceDir ("dialog-published-" + $dialogMode.ToLowerInvariant())
        & (Join-Path $projectRoot 'scripts\test-dialog-ui.ps1') `
            -Configuration $Configuration -ExecutablePath (Join-Path $appStage 'BlueLink.exe') `
            -Mode $dialogMode -ArtifactRoot $dialogAcceptance
        if ($LASTEXITCODE -ne 0) { throw "Published $dialogMode dialog UI Automation acceptance failed." }
    }

    $controlTemplateReport = Join-Path $acceptanceDir 'windows-control-template-runtime.json'
    $controlTemplateProbe = Start-Process -FilePath (Join-Path $appStage 'BlueLink.exe') `
        -ArgumentList "--control-template-smoke-test=$controlTemplateReport" -Wait -PassThru
    if ($controlTemplateProbe.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $controlTemplateReport)) {
        throw "Windows control-template runtime probe failed with exit code $($controlTemplateProbe.ExitCode)."
    }
    & (Join-Path $projectRoot 'scripts\verify-ui-contract.ps1') -Scope Full -RuntimeProbeReport $controlTemplateReport
    if ($LASTEXITCODE -ne 0) { throw 'WPF UI runtime contract verification failed.' }
}

$runtimeSnapshot = Join-Path $acceptanceDir 'installer-runtime-required-current.png'
$runtimeVisual = Start-Process -FilePath (Join-Path $stageRoot 'BlueLink.exe') -ArgumentList "--runtime-ui-smoke-test=$runtimeSnapshot" -Wait -PassThru
if ($runtimeVisual.ExitCode -ne 1602 -or -not (Test-Path -LiteralPath $runtimeSnapshot) -or (Get-Item -LiteralPath $runtimeSnapshot).Length -lt 40000) {
    throw "Runtime launcher UI smoke failed with exit code $($runtimeVisual.ExitCode)."
}

if ($SkipInstaller) { Write-Host "Windows framework-dependent staged directory: $stageRoot"; exit 0 }

& $dotnet build $packageProject -c $Configuration --no-restore -t:Rebuild `
    "-p:ProductVersion=$version" "-p:ProductCode=$productCode" "-p:BundleProviderKey=$bundleProviderKey" `
    "-p:LauncherExe=$(Join-Path $stageRoot 'BlueLink.exe')" `
    "-p:LauncherConfig=$(Join-Path $stageRoot 'BlueLink.exe.config')" "-p:UninstallExe=$(Join-Path $stageRoot 'Uninstall.exe')" `
    "-p:UninstallConfig=$(Join-Path $stageRoot 'Uninstall.exe.config')" "-p:ManifestPath=$manifestPath"
if ($LASTEXITCODE -ne 0) { throw 'WiX MSI compilation failed.' }

$msi = Join-Path $projectRoot "installer\BlueLink.Package\bin\x64\$Configuration\BlueLink.Package.msi"
$baOutput = Join-Path $projectRoot "installer\BlueLink.SetupUI\bin\$Configuration\net472\win-x64"
& $dotnet build $bundleProject -c $Configuration --no-restore -t:Rebuild `
    "-p:ProductVersion=$version" "-p:ProductCode=$productCode" "-p:PayloadFingerprint=$payloadFingerprint" `
    "-p:BundleProviderKey=$bundleProviderKey" "-p:MsiPath=$msi" "-p:BaOutput=$baOutput" `
    "-p:RuntimePayload=$runtimePayload" "-p:RuntimeDownloadUrl=$($runtimeFile.url)" `
    "-p:RuntimeVersion=$runtimeVersion" "-p:RuntimeSize=$runtimeDisplaySize"
if ($LASTEXITCODE -ne 0) { throw 'WiX Burn bundle compilation failed.' }

$bundle = Join-Path $projectRoot "installer\BlueLink.Bundle\bin\x64\$Configuration\BlueLink.Bundle.exe"
$installerDir = Join-Path $projectRoot 'artifacts\installer'
$installer = Join-Path $installerDir "BlueLink-Setup-$version-win-x64.exe"
New-Item -ItemType Directory -Path $installerDir -Force | Out-Null
Copy-Item -LiteralPath $bundle -Destination $installer -Force

foreach ($page in @('welcome', 'location', 'runtime', 'progress', 'complete', 'uninstall')) {
    $snapshotPath = Join-Path $acceptanceDir "installer-$page.png"
    $snapshot = Start-Process -FilePath $installer -ArgumentList @("SetupSnapshotPath=$snapshotPath", "SetupSnapshotPage=$page") -Wait -PassThru
    if ($snapshot.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $snapshotPath) -or (Get-Item -LiteralPath $snapshotPath).Length -lt 30000) {
        throw "Custom installer UI smoke failed: $page"
    }
}
$overwriteSnapshot = Join-Path $acceptanceDir 'installer-overwrite-wpfui-uia.png'
Invoke-OverwriteDialogAutomation $installer $overwriteSnapshot
if (-not (Test-Path -LiteralPath $overwriteSnapshot) -or (Get-Item -LiteralPath $overwriteSnapshot).Length -lt 20000) {
    throw 'Official WPF-UI overwrite MessageBox UI Automation smoke failed.'
}

[ordered]@{
    Version = $version
    ProductCode = $productCode
    PayloadFingerprint = $payloadFingerprint
} | ConvertTo-Json | Set-Content -LiteralPath $releaseIdentityPath -Encoding utf8

Write-Host "Custom WiX installer output: $installer"
Write-Host "Release payload fingerprint: $payloadFingerprint"
Write-Host "Microsoft Desktop Runtime ${runtimeVersion}: $runtimeLength bytes, SHA512 $actualRuntimeHash"
