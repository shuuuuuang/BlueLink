param(
    [Parameter(Mandatory)][string]$KeystorePath,
    [string]$InputDirectory = (Join-Path $PSScriptRoot '../artifacts/android'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/android-signed')
)
$ErrorActionPreference = 'Stop'
foreach ($name in @('ANDROID_KEYSTORE_PASSWORD', 'ANDROID_KEY_ALIAS', 'ANDROID_KEY_PASSWORD')) {
    if (-not [Environment]::GetEnvironmentVariable($name)) { throw "Missing signing environment variable: $name" }
}
$KeystorePath = (Resolve-Path -LiteralPath $KeystorePath).Path
$InputDirectory = (Resolve-Path -LiteralPath $InputDirectory).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new signing output directory.' }
$sdk = if ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } else { $env:ANDROID_HOME }
if (-not $sdk) { throw 'Set ANDROID_SDK_ROOT or ANDROID_HOME.' }
$tools = Join-Path $sdk 'build-tools/34.0.0'
$signer = Join-Path $tools 'apksigner.bat'
$aligner = Join-Path $tools 'zipalign.exe'
foreach ($tool in @($signer, $aligner)) {
    if (-not (Test-Path -LiteralPath $tool)) { throw "Android build-tools 34.0.0 missing: $tool" }
}
$version = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '../VERSION') -Raw).Trim()
$manifest = @(Get-Content -LiteralPath (Join-Path $InputDirectory 'build-manifest.json') -Raw | ConvertFrom-Json)
$release = @($manifest | Where-Object Variant -eq 'release')
if ($release.Count -ne 5 -or @($release.Abi | Sort-Object -Unique).Count -ne 5) {
    throw 'Expected ARM32, ARM64, x86, x86_64 and universal Release APKs.'
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$signed = @()
$certificate = $null
foreach ($entry in $release) {
    if ($entry.Abi -notin @('armeabi-v7a', 'arm64-v8a', 'x86', 'x86_64', 'universal')) { throw 'Unsupported ABI.' }
    $expectedName = "BlueLink-$version-android-$($entry.Abi)-release-unsigned.apk"
    if ($entry.File -cne $expectedName -or $entry.Signed) { throw 'Expected unsigned Release input.' }
    $source = Join-Path $InputDirectory $expectedName
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.Sha256) { throw "APK hash mismatch: $expectedName" }
    $aligned = Join-Path $OutputDirectory "$($entry.Abi).aligned.apk"
    $destination = Join-Path $OutputDirectory "BlueLink-$version-android-$($entry.Abi)-release.apk"
    try {
        & $aligner -f -p 4 $source $aligned
        if ($LASTEXITCODE -ne 0) { throw 'APK alignment failed.' }
        # Passwords are read by apksigner from environment, never placed in process arguments.
        & $signer sign --ks $KeystorePath --ks-key-alias $env:ANDROID_KEY_ALIAS --ks-pass env:ANDROID_KEYSTORE_PASSWORD --key-pass env:ANDROID_KEY_PASSWORD --out $destination $aligned
        if ($LASTEXITCODE -ne 0) { throw 'APK signing failed.' }
        $verification = & $signer verify --verbose --print-certs $destination 2>&1
        if ($LASTEXITCODE -ne 0) { throw 'APK signature verification failed.' }
        $digest = [regex]::Match(($verification -join "`n"), '(?m)^Signer #1 certificate SHA-256 digest: ([0-9a-fA-F]{64})\s*$')
        if (-not $digest.Success) { throw 'Signing certificate fingerprint missing.' }
        $currentCertificate = $digest.Groups[1].Value.ToLowerInvariant()
        if ($certificate -and $certificate -ne $currentCertificate) { throw 'APK signing certificates differ.' }
        $certificate = $currentCertificate
        & $aligner -c -p 4 $destination
        if ($LASTEXITCODE -ne 0) { throw 'Signed APK alignment verification failed.' }
        $signed += [ordered]@{
            File = [IO.Path]::GetFileName($destination); Abi = $entry.Abi
            NativeAbis = $entry.NativeAbis; Variant = 'release'; Signed = $true
            CertificateSha256 = $certificate; Version = $version
            Sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        }
        Write-Host "Signed and verified: $([IO.Path]::GetFileName($destination))"
    } finally {
        if (Test-Path -LiteralPath $aligned) { Remove-Item -LiteralPath $aligned -Force }
    }
}
$signed | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'build-manifest.json') -Encoding utf8
$signed | ForEach-Object { "$($_.Sha256)  $($_.File)" } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
