param(
    [Parameter(Mandatory = $true)][string]$BaselineInstaller,
    [Parameter(Mandatory = $true)][string]$CandidateInstaller,
    [string]$InstallDir = (Join-Path (Split-Path -Parent $PSScriptRoot) '.acceptance\windows-upgrade'),
    [string]$AcceptanceId = 'Upgrade',
    [string]$CandidateStage = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\windows\win-x64')
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$acceptanceRoot = [IO.Path]::GetFullPath((Join-Path $root '.acceptance'))
$installRoot = [IO.Path]::GetFullPath($InstallDir)
$allowedPrefix = $acceptanceRoot.TrimEnd('\') + '\'
if (-not $installRoot.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Upgrade acceptance directory must remain below $acceptanceRoot"
}
foreach ($installer in @($BaselineInstaller, $CandidateInstaller)) {
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Installer not found: $installer" }
}

function Get-ProgramFingerprint([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return '<absent>' }
    return ((Get-ChildItem -LiteralPath $Path -Recurse -File -Force | Where-Object {
        -not $_.FullName.StartsWith((Join-Path $Path 'Download') + '\', [StringComparison]::OrdinalIgnoreCase)
    } | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($Path.TrimEnd('\').Length + 1)
        $relative + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n")
}

$productionRoot = 'D:\BlueLink'
$productionFingerprint = Get-ProgramFingerprint $productionRoot
function Assert-ProductionUnchanged {
    if ((Get-ProgramFingerprint $productionRoot) -ne $productionFingerprint) {
        throw "Isolated upgrade acceptance modified protected production directory: $productionRoot"
    }
}

function Invoke-Bundle([string]$Path, [string[]]$Arguments, [string]$Operation) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -Wait -PassThru
    Assert-ProductionUnchanged
    if ($process.ExitCode -ne 0) { throw "$Operation failed with exit code $($process.ExitCode)." }
}

function Assert-Payload([string]$Root, $ExpectedManifest) {
    $installedManifestPath = Join-Path $Root '.bluelink-install.json'
    if (-not (Test-Path -LiteralPath $installedManifestPath)) { throw 'Installed payload manifest is missing.' }
    $installedManifest = Get-Content -LiteralPath $installedManifestPath -Raw | ConvertFrom-Json
    if ([string]$installedManifest.ApplicationVersion -ne [string]$ExpectedManifest.ApplicationVersion -or
        [string]$installedManifest.PayloadFingerprint -ne [string]$ExpectedManifest.PayloadFingerprint) {
        throw 'Installed version or payload fingerprint does not match the candidate installer.'
    }
    foreach ($payload in @($installedManifest.PayloadFiles)) {
        $path = Join-Path $Root ([string]$payload.Path).Replace('/', '\')
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Installed payload missing: $($payload.Path)" }
        $item = Get-Item -LiteralPath $path
        if ($item.Length -ne [long]$payload.Length -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne [string]$payload.Sha256) {
            throw "Installed payload hash mismatch: $($payload.Path)"
        }
    }
    return $installedManifest
}

$registryPath = 'HKCU:\Software\BlueLink' + $AcceptanceId
$existingRegistration = Get-ItemProperty -LiteralPath $registryPath -ErrorAction SilentlyContinue
$registeredFolder = $existingRegistration.InstallFolder
if ($registeredFolder) {
    $registeredFull = [IO.Path]::GetFullPath([string]$registeredFolder).TrimEnd('\')
    if (-not $registeredFull.Equals($installRoot.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Existing acceptance registration points outside this test: $registeredFolder"
    }
    $providerKey = [string]$existingRegistration.BundleProviderKey
    $registeredBundle = Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue } |
        Where-Object { [string]$_.BundleProviderKey -eq $providerKey } |
        Select-Object -First 1
    if ($registeredBundle) {
        $quietUninstall = [string]$registeredBundle.QuietUninstallString
        if ($quietUninstall -notmatch '^\s*"([^"]+)"') {
            throw "Cannot resolve the cached acceptance bundle from its exact registration: $providerKey"
        }
        $cachedBundle = [IO.Path]::GetFullPath($Matches[1])
        $packageCacheRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Package Cache')).TrimEnd('\') + '\'
        if (-not $cachedBundle.StartsWith($packageCacheRoot, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $cachedBundle -PathType Leaf)) {
            throw "Registered acceptance bundle is outside the package cache or missing: $cachedBundle"
        }
        Invoke-Bundle $cachedBundle @('-uninstall', '-quiet', '-norestart', "InstallFolder=$installRoot") 'Acceptance pre-clean'
    } else {
        $productCode = [string]$existingRegistration.MsiProductCode
        if ($productCode -notmatch '^\{[0-9A-Fa-f-]{36}\}$') {
            throw "Acceptance registration has neither an exact cached bundle nor a valid MSI product code: $providerKey"
        }
        $cleanupProcess = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') `
            -ArgumentList @("/x$productCode", '/qn', '/norestart') -Wait -PassThru
        Assert-ProductionUnchanged
        if ($cleanupProcess.ExitCode -ne 0 -and $cleanupProcess.ExitCode -ne 1605) {
            throw "Acceptance MSI pre-clean failed with exit code $($cleanupProcess.ExitCode)."
        }
    }
}
if (Test-Path -LiteralPath $installRoot) { Remove-Item -LiteralPath $installRoot -Recurse -Force }

$commonArguments = @('-quiet', '-norestart', "InstallFolder=$installRoot", 'CreateDesktopShortcut=0', 'AutoStart=0')
Invoke-Bundle $BaselineInstaller $commonArguments '0.2.12 baseline install'
$baselineManifest = Get-Content -LiteralPath (Join-Path $installRoot '.bluelink-install.json') -Raw | ConvertFrom-Json
$baselineFingerprint = Get-ProgramFingerprint $installRoot
$baselineRootHash = (Get-FileHash -LiteralPath (Join-Path $installRoot 'BlueLink.exe') -Algorithm SHA256).Hash
$baselineAppHash = (Get-FileHash -LiteralPath (Join-Path $installRoot 'app\BlueLink.dll') -Algorithm SHA256).Hash

$preservedDownload = Join-Path $installRoot 'Download\upgrade-acceptance-user-file.txt'
New-Item -ItemType Directory -Path (Split-Path -Parent $preservedDownload) -Force | Out-Null
Set-Content -LiteralPath $preservedDownload -Value 'user-owned upgrade fixture' -Encoding UTF8

$candidateManifestPath = $CandidateInstaller + '.manifest.json'
if (-not (Test-Path -LiteralPath $candidateManifestPath)) {
    $candidateManifestPath = Join-Path $CandidateStage '.bluelink-install.json'
}
$candidateManifest = Get-Content -LiteralPath $candidateManifestPath -Raw | ConvertFrom-Json
if ([version]$candidateManifest.ApplicationVersion -le [version]$baselineManifest.ApplicationVersion) {
    throw 'Candidate acceptance version must be greater than the baseline version.'
}
Invoke-Bundle $CandidateInstaller $commonArguments '0.2.12 to 0.2.13 upgrade'
$installedManifest = Assert-Payload $installRoot $candidateManifest
if (-not (Test-Path -LiteralPath $preservedDownload)) { throw 'Upgrade deleted a user file from Download.' }
if ((Get-FileHash -LiteralPath (Join-Path $installRoot 'BlueLink.exe') -Algorithm SHA256).Hash -eq $baselineRootHash -and
    (Get-FileHash -LiteralPath (Join-Path $installRoot 'app\BlueLink.dll') -Algorithm SHA256).Hash -eq $baselineAppHash) {
    throw 'Upgrade completed without replacing either critical application binary.'
}
if ((Get-ProgramFingerprint $installRoot) -eq $baselineFingerprint) {
    throw 'Upgrade left the complete program fingerprint unchanged.'
}

$firstCandidateFingerprint = Get-ProgramFingerprint $installRoot
Invoke-Bundle $CandidateInstaller $commonArguments 'exact candidate repeat install'
[void](Assert-Payload $installRoot $candidateManifest)
if ((Get-ProgramFingerprint $installRoot) -ne $firstCandidateFingerprint) {
    throw 'Repeating the exact candidate changed the verified program fingerprint.'
}

$oldProvider = "BlueLink.Desktop.$AcceptanceId.Bundle.$($baselineManifest.ApplicationVersion)"
$oldBundleRegistration = Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
    Where-Object { (Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue).BundleProviderKey -eq $oldProvider }
if ($oldBundleRegistration) { throw 'Upgrade left the old acceptance Burn registration active.' }

Invoke-Bundle $CandidateInstaller @('-uninstall', '-quiet', '-norestart', "InstallFolder=$installRoot") 'Candidate uninstall'
if (Test-Path -LiteralPath (Join-Path $installRoot 'BlueLink.exe')) { throw 'Upgrade acceptance uninstall left program files.' }
if (-not (Test-Path -LiteralPath $preservedDownload)) { throw 'Upgrade acceptance uninstall deleted Download content.' }
if (Test-Path -LiteralPath $installRoot) { Remove-Item -LiteralPath $installRoot -Recurse -Force }

Write-Host "Installer upgrade acceptance passed: $($baselineManifest.ApplicationVersion) -> $($installedManifest.ApplicationVersion)"
Write-Host "Verified candidate payload fingerprint: $($installedManifest.PayloadFingerprint)"
