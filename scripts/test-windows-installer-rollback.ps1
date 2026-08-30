param(
    [Parameter(Mandatory = $true)][string]$BaselineInstaller,
    [Parameter(Mandatory = $true)][string]$CandidateInstaller,
    [string]$InstallDir = (Join-Path (Split-Path -Parent $PSScriptRoot) '.acceptance\windows-rollback'),
    [ValidatePattern('^[A-Za-z0-9]+$')]
    [string]$AcceptanceId = 'Rollback'
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$acceptanceRoot = [IO.Path]::GetFullPath((Join-Path $root '.acceptance')).TrimEnd('\') + '\'
$installRoot = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
if (-not ($installRoot + '\').StartsWith($acceptanceRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Rollback acceptance directory must remain below $acceptanceRoot"
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
        throw "Rollback acceptance modified protected production directory: $productionRoot"
    }
}

function Invoke-Bundle([string]$Path, [string[]]$Arguments, [string]$Operation, [bool]$ExpectSuccess) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -Wait -PassThru
    Assert-ProductionUnchanged
    if ($ExpectSuccess -and $process.ExitCode -ne 0) {
        throw "$Operation failed with exit code $($process.ExitCode)."
    }
    if (-not $ExpectSuccess -and $process.ExitCode -eq 0) {
        throw "$Operation unexpectedly succeeded despite the isolated write-denial fixture."
    }
    return $process.ExitCode
}

$common = @('-quiet', '-norestart', "InstallFolder=$installRoot", 'CreateDesktopShortcut=0', 'AutoStart=0')
if (Test-Path -LiteralPath $installRoot) {
    throw "Rollback acceptance requires a fresh directory: $installRoot"
}
Invoke-Bundle $BaselineInstaller $common 'Baseline install' $true | Out-Null

$manifestPath = Join-Path $installRoot '.bluelink-install.json'
$launcherPath = Join-Path $installRoot 'BlueLink.exe'
$appDirectory = Join-Path $installRoot 'app'
$downloadFixture = Join-Path $installRoot 'Download\rollback-user-file.txt'
foreach ($required in @($manifestPath, $launcherPath, $appDirectory)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Baseline payload is incomplete: $required" }
}
Set-Content -LiteralPath $downloadFixture -Value 'user-owned rollback fixture' -Encoding UTF8
$baselineManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$baselineFingerprint = Get-ProgramFingerprint $installRoot

$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$denyApplied = $false
try {
    & "$env:SystemRoot\System32\icacls.exe" $appDirectory '/deny' ('*' + $sid + ':(OI)(CI)(W,D)') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply the isolated write-denial fixture.' }
    $denyApplied = $true
    $candidateExit = Invoke-Bundle $CandidateInstaller $common 'Injected-failure upgrade' $false
}
finally {
    if ($denyApplied) {
        & "$env:SystemRoot\System32\icacls.exe" $appDirectory '/remove:d' ('*' + $sid) | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Unable to remove the isolated write-denial fixture.' }
    }
}

$afterManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([string]$afterManifest.ApplicationVersion -ne [string]$baselineManifest.ApplicationVersion -or
    [string]$afterManifest.PayloadFingerprint -ne [string]$baselineManifest.PayloadFingerprint) {
    throw 'Failed upgrade did not restore the baseline ownership manifest.'
}
if ((Get-ProgramFingerprint $installRoot) -ne $baselineFingerprint) {
    throw 'Failed upgrade did not restore the exact baseline program fingerprint.'
}
if (-not (Test-Path -LiteralPath $downloadFixture)) {
    throw 'Failed upgrade deleted the user-owned Download fixture.'
}
$smoke = Start-Process -FilePath $launcherPath -ArgumentList '--startup-smoke-test' -WindowStyle Hidden -Wait -PassThru
if ($smoke.ExitCode -ne 0) { throw "Rolled-back baseline no longer starts (code $($smoke.ExitCode))." }

Invoke-Bundle $BaselineInstaller @('-uninstall', '-quiet', '-norestart', "InstallFolder=$installRoot") `
    'Baseline cleanup uninstall' $true | Out-Null
if (Test-Path -LiteralPath $launcherPath) { throw 'Rollback cleanup left installed program files.' }
if (-not (Test-Path -LiteralPath $downloadFixture)) { throw 'Rollback cleanup deleted Download content.' }

$resultPath = Join-Path $root 'artifacts\acceptance\installer-rollback-result.txt'
@(
    'Installer forced-failure rollback acceptance: PASS'
    "BaselineVersion=$($baselineManifest.ApplicationVersion)"
    "CandidateExitCode=$candidateExit"
    'BaselineProgramFingerprintRestored=True'
    'BaselineStartupAfterRollback=True'
    'DownloadPreserved=True'
    'ProtectedProductionDirectoryUnchanged=True'
) | Set-Content -LiteralPath $resultPath -Encoding UTF8
Write-Host "Installer forced-failure rollback acceptance passed: $resultPath"
