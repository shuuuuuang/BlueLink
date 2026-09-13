param(
    [string]$DotnetPath = 'D:\Tool\dotnet-sdk-8\dotnet.exe',
    [string]$OutputDirectory = '',
    [switch]$CompileInstaller
)
# Produces an unpublished inspection artifact. No executable in the stage is run.
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot ('artifacts\windows\review-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $OutputDirectory.StartsWith($projectRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Review artifacts must stay inside this worktree.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new output directory to preserve earlier evidence.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$previousCli = $env:DOTNET_CLI_HOME
$previousPackages = $env:NUGET_PACKAGES
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-home'
$setupAssets = Join-Path $projectRoot 'installer\BlueLink.SetupUI\obj\project.assets.json'
if (Test-Path -LiteralPath $setupAssets) {
    $restored = Get-Content -LiteralPath $setupAssets -Raw | ConvertFrom-Json
    $cache = @($restored.packageFolders.PSObject.Properties.Name) | Select-Object -First 1
    if ($cache -and (Test-Path -LiteralPath (Join-Path $cache 'wixtoolset.sdk'))) { $env:NUGET_PACKAGES = $cache }
}
$productRegistryKey = 'Software\BlueLink.Review'
$packageUpgrade = '{1968FCA3-785D-49B1-9328-62EC6101FB9A}'
$bundleUpgrade = '{BEF13DA0-A4EB-44EB-82F2-0A608304CC25}'
$version = (Get-Content -LiteralPath (Join-Path $projectRoot 'VERSION') -Raw).Trim()
$stage = Join-Path $OutputDirectory 'stage'
$app = Join-Path $stage 'app'
$bootstrap = Join-Path $stage 'bootstrap'
New-Item -ItemType Directory -Path $app, $bootstrap, (Join-Path $stage 'Download') | Out-Null
function Invoke-Build([string[]]$Arguments, [string]$LogName) {
    & $DotnetPath @Arguments *> (Join-Path $OutputDirectory $LogName)
    if ($LASTEXITCODE -ne 0) { throw "Windows review build failed. See $OutputDirectory\$LogName" }
}
Push-Location $projectRoot
try {
    Invoke-Build @('publish', 'windows\BlueLink.App\BlueLink.App.csproj', '-c', 'Release', '--no-restore', '--self-contained', 'false', '-m:1', '-nodeReuse:false', '-p:UseSharedCompilation=false', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $app) 'publish.log'
    foreach ($component in @('BlueLink.Launcher', 'BlueLink.Uninstall', 'BlueLink.SetupUI', 'BlueLink.Installation.Tests')) {
        Invoke-Build @('build', "installer\$component\$component.csproj", '-c', 'Release', '--no-restore', '-m:1', '-nodeReuse:false', '-p:UseSharedCompilation=false') "$component-build.log"
    }
    foreach ($component in @('BlueLink.Launcher', 'BlueLink.Uninstall')) {
        $source = Join-Path $projectRoot "installer\$component\bin\Release\net472\win-x64"
        foreach ($file in Get-ChildItem -LiteralPath $source -File) {
            if ($file.Extension -eq '.pdb') { continue }
            $destination = if ($file.Name -in @('BlueLink.exe','BlueLink.exe.config','Uninstall.exe','Uninstall.exe.config')) { $stage } else { $bootstrap }
            Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        }
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'installer\dotnet-runtime-lock.json') -Destination (Join-Path $bootstrap 'runtime-package.json')
    # The detached uninstaller copies this config. Its identity must match MSI/Burn,
    # and these bytes must be finalized before the ownership manifest is hashed.
    $uninstallConfigPath = Join-Path $stage 'Uninstall.exe.config'
    [xml]$uninstallConfig = Get-Content -LiteralPath $uninstallConfigPath -Raw
    foreach ($setting in $uninstallConfig.configuration.appSettings.add) {
        if ($setting.key -eq 'ProductRegistryKey') { $setting.value = $productRegistryKey }
        if ($setting.key -eq 'BundleUpgradeCode') { $setting.value = $bundleUpgrade }
    }
    if ($uninstallConfig.SelectSingleNode('/configuration/appSettings/add[@key="ProductRegistryKey"]/@value').Value -ne $productRegistryKey -or
        $uninstallConfig.SelectSingleNode('/configuration/appSettings/add[@key="BundleUpgradeCode"]/@value').Value -ne $bundleUpgrade) {
        throw 'Uninstaller configuration must match the Review MSI and bundle registration identities.'
    }
    $uninstallConfig.Save($uninstallConfigPath)
    $manifest = Join-Path $stage '.bluelink-install.json'
    $generated = Join-Path $OutputDirectory 'Package.Review.Generated.wxs'
    & (Join-Path $PSScriptRoot 'generate-wix-payload.ps1') -AppPublishDir $app -BootstrapDir $bootstrap -ManifestPath $manifest -OutputFile $generated -Version $version -GuidNamespace 'BlueLink.Review/' *> (Join-Path $OutputDirectory 'payload.log')
    $payload = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    $locked = Get-Content -LiteralPath (Join-Path $projectRoot 'installer\release-payload-lock.json') -Raw | ConvertFrom-Json
    $testRoot = Join-Path $projectRoot ('.acceptance\install-ownership-review-' + [Guid]::NewGuid().ToString('N'))
    & 'installer\BlueLink.Installation.Tests\bin\Release\net472\win-x64\BlueLink.Installation.Tests.exe' $stage $testRoot *> (Join-Path $OutputDirectory 'ownership-tests.log')
    if ($LASTEXITCODE -ne 0) { throw 'Install payload ownership verification failed.' }
    if ($CompileInstaller) {
        # Review builds share a version. Remove the prior MSI inside the transaction
        # before writing the new payload; old random component GUIDs cannot share ownership.
        # Keep all identities separate from the formal release; this script only builds.
        $productCode = [Guid]::NewGuid().ToString('B')
        $provider = 'BlueLink.Review.' + [Guid]::NewGuid().ToString('N')
        $common = @('-c','Release','--no-restore','-m:1','-nodeReuse:false',"-p:ProductVersion=$version","-p:ProductCode=$productCode","-p:PackageUpgradeCode=$packageUpgrade","-p:BundleUpgradeCode=$bundleUpgrade","-p:BundleProviderKey=$provider","-p:ProductRegistryKey=$productRegistryKey")
        $msiArgs = @('build','installer\BlueLink.Package\BlueLink.Package.wixproj') + $common + @('-p:MajorUpgradeSchedule=afterInstallInitialize','-p:AllowSameVersionUpgrades=yes',"-p:GeneratedPayloadPath=$generated",'-p:ProductName=BlueLink Review',"-p:LauncherExe=$stage\BlueLink.exe","-p:LauncherConfig=$stage\BlueLink.exe.config","-p:UninstallExe=$stage\Uninstall.exe","-p:UninstallConfig=$stage\Uninstall.exe.config","-p:ManifestPath=$manifest")
        foreach ($name in @('ApplicationComponentGuid','UninstallerComponentGuid','ManifestComponentGuid','DownloadComponentGuid','StartMenuComponentGuid','DesktopComponentGuid','AutoStartComponentGuid')) { $msiArgs += '-p:' + $name + '=' + [Guid]::NewGuid().ToString('B') }
        Invoke-Build $msiArgs 'msi-build.log'
        $msi = Join-Path $projectRoot 'installer\BlueLink.Package\bin\x64\Release\BlueLink.Package.msi'
        Invoke-Build (@('build','installer\BlueLink.Bundle\BlueLink.Bundle.wixproj') + $common + @('-p:BundleName=BlueLink Review',"-p:MsiPath=$msi","-p:BaOutput=$projectRoot\installer\BlueLink.SetupUI\bin\Release\net472\win-x64",'-p:IncludePrerequisites=no',"-p:PayloadFingerprint=$($payload.PayloadFingerprint)","-p:ManifestPath=$manifest")) 'bundle-build.log'
        Copy-Item -LiteralPath $msi -Destination (Join-Path $OutputDirectory 'BlueLink-Review-DO-NOT-DISTRIBUTE.msi')
        Copy-Item -LiteralPath (Join-Path $projectRoot 'installer\BlueLink.Bundle\bin\x64\Release\BlueLink.Bundle.exe') -Destination (Join-Path $OutputDirectory 'BlueLink-Review-DO-NOT-DISTRIBUTE.exe')
    }
    [ordered]@{
        ProductRegistryKey = $productRegistryKey
        BundleUpgradeCode = $bundleUpgrade
        PackageUpgradeCode = $packageUpgrade
        SameVersionUpgrades = $true
        OldMsiRemovalSchedule = 'afterInstallInitialize'
        Version = $version
        PayloadFingerprint = $payload.PayloadFingerprint
        Purpose = 'Unpublished Windows review; not a signed production release. Installation acceptance is recorded separately.'
        PublishedVersionLocked = [string]$locked.Version -eq $version
        MatchesLockedRelease = [string]$locked.PayloadFingerprint -eq [string]$payload.PayloadFingerprint
        NativeDesktopAndInstallationAcceptance = 'Not verified'
        InstallerCompiled = [bool]$CompileInstaller
        PrerequisitesIncluded = $false
        Signed = $false
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'review-artifact.json') -Encoding utf8
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, (Join-Path $OutputDirectory 'BlueLink-Windows-review.zip'))
    Write-Host "Windows review artifacts: $OutputDirectory"
} finally { Pop-Location; $env:DOTNET_CLI_HOME = $previousCli; $env:NUGET_PACKAGES = $previousPackages }
