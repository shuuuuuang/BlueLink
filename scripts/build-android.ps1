param(
    [ValidateSet('all','armeabi-v7a','arm64-v8a','x86','x86_64')]
    [string[]]$Architecture = @('all'),
    [switch]$Offline,
    [switch]$SkipTests
)
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
$abis = if ($Architecture -contains 'all') { @('armeabi-v7a','arm64-v8a','x86','x86_64') } else { @($Architecture | Select-Object -Unique) }
$arguments = @('--no-daemon', '-p', (Join-Path $root 'android'), '-PbluelinkSplitApks=true', ('-PbluelinkAbis=' + ($abis -join ',')))
if ($Offline) { $arguments += '--offline' }
if (-not $SkipTests) { $arguments += @(':protocol-core:test', ':app:testDebugUnitTest', ':app:lintDebug') }
$arguments += @(':app:assembleDebug', ':app:assembleRelease')
& $gradle @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$version = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
$artifactDir = Join-Path $root 'artifacts/android'
New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$artifacts = @()
foreach ($variant in @('debug', 'release')) {
    $output = Join-Path $root "android/app/build/outputs/apk/$variant"
    $metadata = Get-Content -LiteralPath (Join-Path $output 'output-metadata.json') -Raw | ConvertFrom-Json
    foreach ($abi in @($abis) + @('universal')) {
        $matching = @($metadata.elements | Where-Object {
            $filters = @($_.filters | Where-Object filterType -eq 'ABI')
            if ($abi -eq 'universal') { $filters.Count -eq 0 } else { $filters.Count -eq 1 -and $filters[0].value -eq $abi }
        })
        if ($matching.Count -ne 1) { throw "Expected one $variant APK for $abi." }
        $source = Join-Path $output $matching[0].outputFile
        $archive = [IO.Compression.ZipFile]::OpenRead($source)
        try {
            $actualAbis = @($archive.Entries | Where-Object FullName -match '^lib/[^/]+/[^/]+[.]so$' |
                ForEach-Object { $_.FullName.Split('/')[1] } | Sort-Object -Unique)
            $expected = if ($abi -eq 'universal') { $abis } else { @($abi) }
            foreach ($item in $expected) {
                if (-not ($archive.Entries | Where-Object FullName -eq "lib/$item/libconscrypt_jni.so")) { throw "Missing Conscrypt native library: $variant/$item" }
            }
            if ($abi -ne 'universal' -and ($actualAbis.Count -ne 1 -or $actualAbis[0] -ne $abi)) { throw "Unexpected native ABI in $source" }
        } finally { $archive.Dispose() }
        $suffix = if ($variant -eq 'release') { 'release-unsigned' } else { 'debug' }
        $destination = Join-Path $artifactDir "BlueLink-$version-android-$abi-$suffix.apk"
        Copy-Item -LiteralPath $source -Destination $destination -Force
        $artifacts += [ordered]@{ File = [IO.Path]::GetFileName($destination); Abi = $abi; NativeAbis = $actualAbis; Variant = $variant; Signed = $variant -eq 'debug'; Sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash }
        if ($abi -eq 'universal') {
            Copy-Item -LiteralPath $destination -Destination (Join-Path $artifactDir "BlueLink-$version-android-$suffix.apk") -Force
        }
        Write-Host "Android artifact: $destination"
    }
}
$artifacts | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $artifactDir 'build-manifest.json') -Encoding utf8
$artifacts | ForEach-Object { "$($_.Sha256)  $($_.File)" } | Set-Content -LiteralPath (Join-Path $artifactDir 'SHA256SUMS.txt') -Encoding ascii
