param(
    [string]$Output = (Join-Path (Split-Path -Parent $PSScriptRoot) '.acceptance\installer\BlueLink-Acceptance.exe'),
    [ValidatePattern('^[A-Za-z0-9]+$')]
    [string]$AcceptanceId = 'Acceptance',
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$ProductVersion,
    [string]$Stage
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$acceptanceRoot = [IO.Path]::GetFullPath((Join-Path $root '.acceptance'))
$resolvedOutput = [IO.Path]::GetFullPath($Output)
if (-not $resolvedOutput.StartsWith($acceptanceRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Acceptance bundle output must remain below $acceptanceRoot"
}

$dotnet = 'D:\Tool\dotnet-sdk-8\dotnet.exe'
$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root '.nuget-mirror-test'
$version = if ($ProductVersion) { $ProductVersion } else {
    (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
}
$sourceStage = if ($Stage) { [IO.Path]::GetFullPath($Stage) } else { Join-Path $root 'artifacts\windows\win-x64' }
if (-not (Test-Path -LiteralPath $sourceStage -PathType Container)) { throw "Acceptance stage is missing: $sourceStage" }
$allowedStageRoots = @(
    ([IO.Path]::GetFullPath((Join-Path $root 'artifacts'))).TrimEnd('\') + '\'
    ([IO.Path]::GetFullPath((Join-Path $root '.acceptance'))).TrimEnd('\') + '\'
)
if (-not ($allowedStageRoots | Where-Object { $sourceStage.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) })) {
    throw 'Acceptance stage must remain below artifacts or .acceptance.'
}
$packageProject = Join-Path $root 'installer\BlueLink.Package\BlueLink.Package.wixproj'
$bundleProject = Join-Path $root 'installer\BlueLink.Bundle\BlueLink.Bundle.wixproj'
$baOutput = Join-Path $root 'installer\BlueLink.SetupUI\bin\Release\net472\win-x64'
$runtimePayload = Get-ChildItem -LiteralPath (Join-Path $root '.build\dotnet-runtime') -Filter 'windowsdesktop-runtime*win-x64.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $runtimePayload) { throw 'The verified Microsoft runtime cache is missing. Run build-windows-release.ps1 first.' }
$acceptanceRegistryKey = "Software\BlueLink$AcceptanceId"
$acceptanceProviderKey = "BlueLink.Desktop.$AcceptanceId.Bundle.$version"
function Get-AcceptanceGuid([string]$purpose) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes("BlueLink|Acceptance|$AcceptanceId|$purpose"))[0..15] }
    finally { $sha.Dispose() }
    $bytes[7] = ($bytes[7] -band 0x0F) -bor 0x50
    $bytes[8] = ($bytes[8] -band 0x3F) -bor 0x80
    ([Guid]::new([byte[]]$bytes)).ToString('B').ToUpperInvariant()
}
$acceptanceProductCode = Get-AcceptanceGuid "Product-$version"

$stage = Join-Path $acceptanceRoot "build\$AcceptanceId-$version"
$resolvedBuildRoot = [IO.Path]::GetFullPath((Join-Path $acceptanceRoot 'build')).TrimEnd('\') + '\'
$stage = [IO.Path]::GetFullPath($stage)
if (-not $stage.StartsWith($resolvedBuildRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to prepare an acceptance stage outside .acceptance\build.'
}
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -Path (Join-Path $sourceStage '*') -Destination $stage -Recurse -Force

$acceptanceUninstallConfig = Join-Path $stage 'Uninstall.exe.config'
[xml]$uninstallConfig = Get-Content -LiteralPath (Join-Path $stage 'Uninstall.exe.config') -Raw
$uninstallConfig.configuration.appSettings.add | Where-Object key -eq 'ProductRegistryKey' | ForEach-Object { $_.value = $acceptanceRegistryKey }
$uninstallConfig.configuration.appSettings.add | Where-Object key -eq 'BundleUpgradeCode' | ForEach-Object { $_.value = (Get-AcceptanceGuid 'BundleUpgrade') }
$uninstallConfig.Save($acceptanceUninstallConfig)

$generatedWix = Join-Path $root 'installer\BlueLink.Package\Package.Generated.wxs'
$manifestPath = Join-Path $stage '.bluelink-install.json'
& (Join-Path $root 'scripts\generate-wix-payload.ps1') `
    -AppPublishDir (Join-Path $stage 'app') -BootstrapDir (Join-Path $stage 'bootstrap') `
    -ManifestPath $manifestPath -OutputFile $generatedWix -Version $version
$acceptanceManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$payloadFingerprint = [string]$acceptanceManifest.PayloadFingerprint
if ($payloadFingerprint -notmatch '^[0-9A-Fa-f]{64}$') { throw 'Acceptance payload fingerprint is invalid.' }

$packageArguments = @(
    'build', $packageProject, '-c', 'Release', '--no-restore', '-t:Rebuild',
    "-p:ProductVersion=$version", "-p:ProductCode=$acceptanceProductCode",
    "-p:PackageUpgradeCode=$(Get-AcceptanceGuid 'PackageUpgrade')",
    "-p:BundleUpgradeCode=$(Get-AcceptanceGuid 'BundleUpgrade')",
    "-p:BundleProviderKey=$acceptanceProviderKey",
    "-p:ProductRegistryKey=$acceptanceRegistryKey",
    "-p:ProductName=BlueLink Acceptance $AcceptanceId", "-p:ProductRegistryKey=$acceptanceRegistryKey",
    '-p:StartMenuComponentCondition=0',
    "-p:ApplicationComponentGuid=$(Get-AcceptanceGuid 'ApplicationComponent')",
    "-p:UninstallerComponentGuid=$(Get-AcceptanceGuid 'UninstallerComponent')",
    "-p:ManifestComponentGuid=$(Get-AcceptanceGuid 'ManifestComponent')",
    "-p:DownloadComponentGuid=$(Get-AcceptanceGuid 'DownloadComponent')",
    "-p:StartMenuComponentGuid=$(Get-AcceptanceGuid 'StartMenuComponent')",
    "-p:DesktopComponentGuid=$(Get-AcceptanceGuid 'DesktopComponent')",
    "-p:AutoStartComponentGuid=$(Get-AcceptanceGuid 'AutoStartComponent')",
    "-p:LauncherExe=$(Join-Path $stage 'BlueLink.exe')",
    "-p:LauncherConfig=$(Join-Path $stage 'BlueLink.exe.config')",
    "-p:UninstallExe=$(Join-Path $stage 'Uninstall.exe')",
    "-p:UninstallConfig=$acceptanceUninstallConfig",
    "-p:ManifestPath=$manifestPath"
)
& $dotnet $packageArguments
if ($LASTEXITCODE -ne 0) { throw 'Isolated acceptance MSI build failed.' }

$msi = Join-Path $root 'installer\BlueLink.Package\bin\x64\Release\BlueLink.Package.msi'
$bundleArguments = @(
    'build', $bundleProject, '-c', 'Release', '--no-restore', '-t:Rebuild',
    "-p:ProductVersion=$version", "-p:BundleName=BlueLink Acceptance $AcceptanceId",
    "-p:ProductCode=$acceptanceProductCode", "-p:PayloadFingerprint=$payloadFingerprint",
    "-p:BundleUpgradeCode=$(Get-AcceptanceGuid 'BundleUpgrade')",
    "-p:PackageUpgradeCode=$(Get-AcceptanceGuid 'PackageUpgrade')",
    "-p:BundleProviderKey=$acceptanceProviderKey",
    "-p:ProductRegistryKey=$acceptanceRegistryKey",
    "-p:MsiPath=$msi", "-p:BaOutput=$baOutput", "-p:RuntimePayload=$($runtimePayload.FullName)",
    '-p:RuntimeDownloadUrl=https://example.invalid/not-used.exe', '-p:RuntimeVersion=8.0.x',
    '-p:RuntimeSize=acceptance', '-p:RuntimePerMachine=no', '-p:IncludePrerequisites=no'
)
foreach ($requiredIsolationArgument in @(
    "-p:BundleProviderKey=$acceptanceProviderKey",
    "-p:ProductRegistryKey=$acceptanceRegistryKey")) {
    if ($bundleArguments -notcontains $requiredIsolationArgument) {
        throw "Acceptance bundle isolation argument missing: $requiredIsolationArgument"
    }
}
& $dotnet $bundleArguments
if ($LASTEXITCODE -ne 0) { throw 'Isolated acceptance Burn build failed.' }

New-Item -ItemType Directory -Path (Split-Path -Parent $resolvedOutput) -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'installer\BlueLink.Bundle\bin\x64\Release\BlueLink.Bundle.exe') `
    -Destination $resolvedOutput -Force
Copy-Item -LiteralPath $manifestPath -Destination ($resolvedOutput + '.manifest.json') -Force
Write-Host "Isolated Windows acceptance bundle: $resolvedOutput"
