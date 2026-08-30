$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sdk = if ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT }
    elseif ($env:ANDROID_HOME) { $env:ANDROID_HOME }
    elseif (Test-Path -LiteralPath 'D:\Tool\Android\Sdk') { 'D:\Tool\Android\Sdk' }
    else { $null }
if (-not $sdk -or -not (Test-Path -LiteralPath $sdk)) {
    throw 'Android SDK not found. Set ANDROID_SDK_ROOT to an SDK containing platform 34.'
}
$env:ANDROID_SDK_ROOT = $sdk
$env:ANDROID_HOME = $sdk
if (-not $env:JAVA_HOME -and (Test-Path -LiteralPath 'D:\Tool\jdk-17.0.20')) {
    $env:JAVA_HOME = 'D:\Tool\jdk-17.0.20'
}
$bundled = 'D:\Tool\gradle-8.5\bin\gradle.bat'
$gradle = if ($env:BLUELINK_GRADLE) { $env:BLUELINK_GRADLE }
    elseif (Get-Command gradle -ErrorAction SilentlyContinue) { (Get-Command gradle).Source }
    elseif (Test-Path -LiteralPath $bundled) { $bundled }
    else { Join-Path $root 'gradlew.bat' }
& $gradle '--no-daemon' '-p' (Join-Path $root 'android') `
    ':protocol-core:test' ':app:testDebugUnitTest' ':app:lintDebug' ':app:assembleDebug' ':app:assembleRelease'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$version = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
$sourceApk = Join-Path $root 'android\app\build\outputs\apk\debug\app-debug.apk'
$sourceReleaseApk = Join-Path $root 'android\app\build\outputs\apk\release\app-release-unsigned.apk'
$artifactDir = Join-Path $root 'artifacts\android'
$artifactApk = Join-Path $artifactDir "BlueLink-$version-android-debug.apk"
$artifactReleaseApk = Join-Path $artifactDir "BlueLink-$version-android-release-unsigned.apk"
if (-not (Test-Path -LiteralPath $sourceApk)) { throw "Android APK not found: $sourceApk" }
if (-not (Test-Path -LiteralPath $sourceReleaseApk)) { throw "Android release APK not found: $sourceReleaseApk" }
New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null
Copy-Item -LiteralPath $sourceApk -Destination $artifactApk -Force
Copy-Item -LiteralPath $sourceReleaseApk -Destination $artifactReleaseApk -Force
Write-Host "Android artifact: $artifactApk"
Write-Host "Android unsigned release artifact: $artifactReleaseApk"
