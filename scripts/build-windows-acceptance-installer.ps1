param(
    [string]$Output = (Join-Path (Split-Path -Parent $PSScriptRoot) '.acceptance\installer\BlueLink-Acceptance.exe'),
    [ValidatePattern('^[A-Za-z0-9]+$')]
    [string]$AcceptanceId = 'Acceptance',
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$ProductVersion
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
$stage = Join-Path $root 'artifacts\windows\win-x64'
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

$acceptanceUninstallConfig = Join-Path $acceptanceRoot 'installer\Uninstall.exe.config'
New-Item -ItemType Directory -Path (Split-Path -Parent $acceptanceUninstallConfig) -Force | Out-Null
[xml]$uninstallConfig = Get-Content -LiteralPath (Join-Path $stage 'Uninstall.exe.config') -Raw
$uninstallConfig.configuration.appSettings.add | Where-Object key -eq 'ProductRegistryKey' | ForEach-Object { $_.value = $acceptanceRegistryKey }
$uninstallConfig.configuration.appSettings.add | Where-Object key -eq 'BundleUpgradeCode' | ForEach-Object { $_.value = (Get-AcceptanceGuid 'BundleUpgrade') }
$uninstallConfig.Save($acceptanceUninstallConfig)

$packageArguments = @(
    'build', $packageProject, '-c', 'Release', '--no-restore', '-t:Rebuild',
    "-p:ProductVersion=$version", "-p:ProductCode=$(Get-AcceptanceGuid "Product-$version")",
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
    "-p:ManifestPath=$(Join-Path $stage '.bluelink-install.json')"
)
& $dotnet $packageArguments
if ($LASTEXITCODE -ne 0) { throw 'Isolated acceptance MSI build failed.' }

$msi = Join-Path $root 'installer\BlueLink.Package\bin\x64\Release\BlueLink.Package.msi'
$bundleArguments = @(
    'build', $bundleProject, '-c', 'Release', '--no-restore', '-t:Rebuild',
    "-p:ProductVersion=$version", "-p:BundleName=BlueLink Acceptance $AcceptanceId",
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
Write-Host "Isolated Windows acceptance bundle: $resolvedOutput"
