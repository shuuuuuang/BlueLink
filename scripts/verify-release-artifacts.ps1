param([switch]$AllowUnsignedDevelopmentArtifacts)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
$installer = Join-Path $root "artifacts\installer\BlueLink-Setup-$version-win-x64.exe"
$windowsExe = Join-Path $root 'artifacts\windows\win-x64\BlueLink.exe'
$windowsClient = Join-Path $root 'artifacts\windows\win-x64\app\BlueLink.exe'
$uninstaller = Join-Path $root 'artifacts\windows\win-x64\Uninstall.exe'
$apk = Join-Path $root "artifacts\android\BlueLink-$version-android-debug.apk"
$releaseApk = Join-Path $root "artifacts\android\BlueLink-$version-android-release-unsigned.apk"

foreach ($artifact in @($installer, $windowsExe, $windowsClient, $uninstaller, $apk, $releaseApk)) {
    if (-not (Test-Path -LiteralPath $artifact)) { throw "Release artifact missing: $artifact" }
    if ((Get-Item -LiteralPath $artifact).Length -lt 100KB) { throw "Release artifact is unexpectedly small: $artifact" }
}
if (Get-ChildItem -LiteralPath (Split-Path -Parent $windowsExe) -File -Filter '*.dll') { throw 'Windows installed root contains DLL files.' }
foreach ($forbidden in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'clrjit.dll')) {
    if (Get-ChildItem -LiteralPath (Split-Path -Parent $windowsClient) -Recurse -File -Filter $forbidden) { throw "Framework-dependent app contains $forbidden" }
}
$runtimeConfig = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $windowsClient) 'BlueLink.runtimeconfig.json') -Raw
if ($runtimeConfig -notmatch 'Microsoft.WindowsDesktop.App' -or $runtimeConfig -notmatch '"version"\s*:\s*"8\.') { throw 'Windows runtimeconfig does not require Microsoft.WindowsDesktop.App 8.x.' }

$windowsVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($windowsExe).ProductVersion
$installerVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($installer).ProductVersion
if (-not $windowsVersion.StartsWith($version)) { throw "Windows product version mismatch: $windowsVersion" }
if (-not $installerVersion.StartsWith($version)) { throw "Installer product version mismatch: $installerVersion" }

$buildTools = Get-ChildItem -LiteralPath 'D:\Tool\Android\Sdk\build-tools' -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $buildTools) { throw 'Android build-tools were not found.' }
$aapt = Join-Path $buildTools.FullName 'aapt.exe'
$apksigner = Join-Path $buildTools.FullName 'apksigner.bat'
$badging = (& $aapt dump badging $apk) -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'aapt could not inspect the APK.' }
if ($badging -notmatch "versionName='$([regex]::Escape($version))'") { throw 'APK versionName mismatch.' }
$expectedLabel = ([string][char]0x84DD) + ([string][char]0x8054)
if ($badging -notmatch "application-label:'$([regex]::Escape($expectedLabel))'") { throw 'APK application label mismatch.' }
if ($badging -match 'android.permission.INTERNET') { throw 'APK unexpectedly requests INTERNET.' }
& $apksigner verify --verbose $apk | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'APK signature verification failed.' }
$releaseBadging = (& $aapt dump badging $releaseApk) -join "`n"
if ($LASTEXITCODE -ne 0 -or $releaseBadging -notmatch "versionName='$([regex]::Escape($version))'") {
    throw 'Android release APK metadata is invalid.'
}

if (-not $AllowUnsignedDevelopmentArtifacts) {
    foreach ($windowsArtifact in @($installer, $windowsExe, $uninstaller)) {
        $signature = Get-AuthenticodeSignature -LiteralPath $windowsArtifact
        if ($signature.Status.ToString() -ne 'Valid') {
            throw "Release signing gate failed for $windowsArtifact`: $($signature.Status)."
        }
    }
    & $apksigner verify --verbose $releaseApk | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Release signing gate failed for the Android release APK.' }
}
else {
    Write-Warning 'Unsigned development artifacts were allowed explicitly; release signing is NOT verified.'
}

foreach ($snapshot in @('windows-ui-expanded.png', 'windows-ui-collapsed.png', 'windows-ui-compact.png')) {
    $path = Join-Path $root "artifacts\acceptance\$snapshot"
    if (-not (Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -lt 40000) {
        throw "Stateful UI snapshot missing or invalid: $snapshot"
    }
}
$previewSnapshot = Join-Path $root 'artifacts\acceptance\windows-image-preview.png'
if (-not (Test-Path -LiteralPath $previewSnapshot) -or (Get-Item -LiteralPath $previewSnapshot).Length -lt 40000) {
    throw 'Windows image-preview snapshot is missing or invalid.'
}
foreach ($page in @('welcome', 'location', 'runtime', 'progress', 'complete', 'uninstall')) {
    $snapshot = "installer-$page.png"
    $path = Join-Path $root "artifacts\acceptance\$snapshot"
    if (-not (Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -lt 30000) {
        throw "Custom installer page snapshot missing or invalid: $snapshot"
    }
}
$criticalSnapshots = @('installer-runtime-required-current.png', 'installer-overwrite-wpfui-uia.png', 'uninstaller-main.png')
foreach ($snapshot in $criticalSnapshots) {
    $path = Join-Path $root "artifacts\acceptance\$snapshot"
    if (-not (Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).Length -lt 20000) { throw "Critical UI snapshot missing or invalid: $snapshot" }
}
foreach ($artifact in @($installer, $windowsExe, $windowsClient, $uninstaller, $apk, $releaseApk)) {
    $file = Get-Item -LiteralPath $artifact
    $hash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash
    Write-Host "[PASS] $($file.FullName) | $($file.Length) bytes | SHA256 $hash"
}
Write-Host "Release artifact verification passed for $version" `
    $(if ($AllowUnsignedDevelopmentArtifacts) { '(unsigned development mode).' } else { '(signed release mode).' })
