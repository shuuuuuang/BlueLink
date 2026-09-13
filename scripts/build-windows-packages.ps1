param(
    [ValidateSet('x86','x64','arm64','all')][string[]]$Architecture = @('all'),
    [ValidateSet('Installer','Portable','Both')][string]$Format = 'Both',
    [string]$DotnetPath = '',
    [string]$OutputDirectory = '',
    [string]$NuGetConfig = (Join-Path $PSScriptRoot '../NuGet.Config'),
    [switch]$Offline,
    [switch]$SkipTests
)
# Builds unsigned review installers and self-contained portable packages. Does not install them.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $DotnetPath) {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    $DotnetPath = if ($command) { $command.Source } else { 'D:\Tool\dotnet-sdk-8\dotnet.exe' }
}
if (-not (Test-Path -LiteralPath $DotnetPath)) { throw 'Install .NET 8 SDK or specify -DotnetPath.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root ('artifacts/windows/multiarch-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $OutputDirectory.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $OutputDirectory)) { throw 'Use a new output directory inside the worktree.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$previousCli = $env:DOTNET_CLI_HOME
$previousPackages = $env:NUGET_PACKAGES
$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-home'
if (-not $env:NUGET_PACKAGES -and (Test-Path -LiteralPath (Join-Path $root '.nuget-installer/wixtoolset.sdk'))) { $env:NUGET_PACKAGES = Join-Path $root '.nuget-installer' }
$version = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
$architectures = if ($Architecture -contains 'all') { @('x86','x64','arm64') } else { @($Architecture | Select-Object -Unique) }
$manifest = [Collections.Generic.List[object]]::new()
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Invoke-Dotnet([string[]]$Arguments, [string]$LogPath) {
    & $DotnetPath @Arguments *> $LogPath
    if ($LASTEXITCODE -ne 0) { throw "Build failed; see $LogPath" }
}
function Restore-Project([string]$Project, [string]$Rid, [string]$LogPath) {
    $arguments = @('restore', $Project, '--configfile', $NuGetConfig, '-p:NuGetAudit=false')
    if ($Rid) { $arguments += @('-r', $Rid) }
    if ($Offline) { $arguments += @('--ignore-failed-sources', '--source', $env:NUGET_PACKAGES) }
    Invoke-Dotnet $arguments $LogPath
}
function Assert-PeMachine([string]$Path, [string]$Architecture) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw "Invalid PE: $Path" }
        $stream.Position = 0x3C; $offset = $reader.ReadInt32(); $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x4550) { throw "Invalid PE signature: $Path" }
        $expected = @{ x86 = 0x14c; x64 = 0x8664; arm64 = 0xaa64 }[$Architecture]
        if ($reader.ReadUInt16() -ne $expected) { throw "Wrong PE architecture ($Architecture): $Path" }
    } finally { $reader.Dispose() }
}
function Record-Artifact([string]$Path, [string]$Arch, [string]$Kind) {
    $manifest.Add([ordered]@{ File = [IO.Path]::GetFileName($Path); Architecture = $Arch; Kind = $Kind; Version = $version; SelfContained = $true; RuntimeVerification = $nativeVerification; Signed = $false; Size = (Get-Item -LiteralPath $Path).Length; Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash })
}
if (-not ('BlueLinkPackageSummary' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class BlueLinkPackageSummary {
    [DllImport("msi.dll", CharSet=CharSet.Unicode)] static extern uint MsiGetSummaryInformation(uint db, string path, uint update, out uint summary);
    [DllImport("msi.dll", CharSet=CharSet.Unicode)] static extern uint MsiSummaryInfoGetProperty(uint summary, uint property, out uint type, out int number, out long time, StringBuilder value, ref uint length);
    [DllImport("msi.dll")] static extern uint MsiCloseHandle(uint handle);
    public static string GetTemplate(string path) {
        uint handle;
        var status = MsiGetSummaryInformation(0, path, 0, out handle);
        if (status != 0) throw new InvalidOperationException("Cannot read MSI summary: " + status);
        try {
            uint type, length=256; int number; long time; var value=new StringBuilder(256);
            status=MsiSummaryInfoGetProperty(handle, 7, out type, out number, out time, value, ref length);
            if (status != 0) throw new InvalidOperationException("Cannot read MSI platform: " + status);
            return value.ToString();
        } finally { MsiCloseHandle(handle); }
    }
}
"@
}
Push-Location $root
try {
    $buildOptions = @('-c','Release','--no-restore','-m:1','-nodeReuse:false','-p:UseSharedCompilation=false')
    foreach ($arch in $architectures) {
        Write-Host "Building Windows $arch ($Format)..."
        $rid = "win-$arch"
        $work = Join-Path $OutputDirectory $rid
        New-Item -ItemType Directory -Path $work | Out-Null
        $publish = Join-Path $work 'publish'
        $appProject = 'windows/BlueLink.App/BlueLink.App.csproj'
        Restore-Project $appProject $rid (Join-Path $work 'restore-app.log')
        Invoke-Dotnet (@('publish',$appProject) + $buildOptions + @('-r',$rid,'--self-contained','true','-p:PublishSingleFile=false','-p:PublishTrimmed=false','-p:DebugType=None','-p:DebugSymbols=false','-o',$publish)) (Join-Path $work 'publish.log')
        foreach ($native in @('BlueLink.exe','coreclr.dll','hostfxr.dll','wpfgfx_cor3.dll')) { Assert-PeMachine (Join-Path $publish $native) $arch }
        $runtime = Get-Content -LiteralPath (Join-Path $publish 'BlueLink.runtimeconfig.json') -Raw | ConvertFrom-Json
        if (-not $runtime.runtimeOptions.includedFrameworks -or $runtime.runtimeOptions.frameworks) { throw 'Expected self-contained runtime config.' }
        $nativeVerification = 'Not verified on this host'
        $hostArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
        $canRun = $arch -eq $hostArchitecture -or ($arch -eq 'x86' -and $hostArchitecture -in @('x64','arm64')) -or ($arch -eq 'x64' -and $hostArchitecture -eq 'arm64' -and [Environment]::OSVersion.Version.Build -ge 22000)
        if (-not $SkipTests -and $canRun) {
            $verification = Join-Path $work 'verification'
            $verificationProject = 'windows/BlueLink.TransferVerification/BlueLink.TransferVerification.csproj'
            Restore-Project $verificationProject $rid (Join-Path $work 'restore-verification.log')
            Invoke-Dotnet (@('publish',$verificationProject) + $buildOptions + @('-r',$rid,'--self-contained','true','-p:DebugType=None','-p:DebugSymbols=false','-o',$verification)) (Join-Path $work 'build-verification.log')
            # Exercise the exact app assembly included in the artifact.
            Copy-Item -LiteralPath (Join-Path $publish 'BlueLink.dll') -Destination (Join-Path $verification 'BlueLink.dll') -Force
            & (Join-Path $verification 'BlueLink.TransferVerification.exe') --portable-only *> (Join-Path $work 'runtime-tests.log')
            if ($LASTEXITCODE -ne 0) { throw "Native $arch regression failed. See $work/runtime-tests.log" }
            [IO.File]::WriteAllText((Join-Path $verification 'BlueLink.portable'), 'portable')
            & (Join-Path $verification 'BlueLink.TransferVerification.exe') --portable-probe *> (Join-Path $work 'portable-probe.log')
            if ($LASTEXITCODE -ne 0) { throw "Portable $arch probe failed. See $work/portable-probe.log" }
            $nativeVerification = 'Passed: SQLite, relocation, local data roots and update policy'
        }
        if ($Format -in @('Portable','Both')) {
            # Clone only the freshly published payload; never copy user Data or Download contents.
            $portable = Join-Path $work 'portable'
            Copy-Item -LiteralPath $publish -Destination $portable -Recurse
            [IO.File]::WriteAllText((Join-Path $portable 'BlueLink.portable'), "BlueLink portable $version $rid`n")
            New-Item -ItemType Directory -Path (Join-Path $portable 'Data'),(Join-Path $portable 'Download') | Out-Null
            [IO.File]::WriteAllText((Join-Path $portable 'PORTABLE.txt'), "Extract to a writable directory and run BlueLink.exe. Keep BlueLink.portable, Data and Download when updating. Exit BlueLink before moving the directory. Identity keys use Windows user protection; another account/PC requires trust verification again. External file paths are not moved. This unsigned build is for review.`r`n")
            $zip = Join-Path $OutputDirectory "BlueLink-$version-$rid-Portable.zip"
            [IO.Compression.ZipFile]::CreateFromDirectory($portable, $zip)
            Record-Artifact $zip $arch 'Portable'
        }
        if ($Format -in @('Installer','Both')) {
            $stage = Join-Path $work 'stage'
            New-Item -ItemType Directory -Path $stage,(Join-Path $stage 'bootstrap'),(Join-Path $stage 'Download') | Out-Null
            Copy-Item -LiteralPath $publish -Destination (Join-Path $stage 'app') -Recurse
            # WiX 4 managed BA uses the x86 .NET Framework host on ARM64; client/runtime remain native ARM64.
            $hostArch = if ($arch -eq 'arm64') { 'x86' } else { $arch }
            $hostRid = "win-$hostArch"
            foreach ($component in @('Launcher','Uninstall','SetupUI','Installation.Tests')) {
                $project = "installer/BlueLink.$component/BlueLink.$component.csproj"
                $output = Join-Path $work $component
                Restore-Project $project $hostRid (Join-Path $work "restore-$component.log")
                Invoke-Dotnet (@('build',$project) + $buildOptions + @('-r',$hostRid,"-p:PlatformTarget=$hostArch",'-o',$output)) (Join-Path $work "build-$component.log")
                if ($component -in @('Launcher','Uninstall')) {
                    foreach ($file in Get-ChildItem -LiteralPath $output -File | Where-Object Extension -ne '.pdb') {
                        $destination = if ($file.Name -in @('BlueLink.exe','BlueLink.exe.config','Uninstall.exe','Uninstall.exe.config')) { $stage } else { Join-Path $stage 'bootstrap' }
                        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
                    }
                }
            }
            $registration = 'Software\BlueLink.Review'
            $packageUpgrade = '{1968FCA3-785D-49B1-9328-62EC6101FB9A}'
            $bundleUpgrade = '{BEF13DA0-A4EB-44EB-82F2-0A608304CC25}'
            [xml]$config = Get-Content -LiteralPath (Join-Path $stage 'Uninstall.exe.config') -Raw
            foreach ($setting in $config.configuration.appSettings.add) {
                if ($setting.key -eq 'ProductRegistryKey') { $setting.value = $registration }
                if ($setting.key -eq 'BundleUpgradeCode') { $setting.value = $bundleUpgrade }
            }
            $config.Save((Join-Path $stage 'Uninstall.exe.config'))
            [ordered]@{ RuntimeIdentifier = $rid; SelfContained = $true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'bootstrap/bundled-runtime.json') -Encoding utf8
            [ordered]@{ Version = @($runtime.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App')[0].version; Rid = $rid; SelfContained = $true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'bootstrap/runtime-package.json') -Encoding utf8
            $ownership = Join-Path $stage '.bluelink-install.json'
            $generated = Join-Path $work 'Package.Generated.wxs'
            & (Join-Path $PSScriptRoot 'generate-wix-payload.ps1') -AppPublishDir (Join-Path $stage 'app') -BootstrapDir (Join-Path $stage 'bootstrap') -ManifestPath $ownership -OutputFile $generated -Version $version -GuidNamespace "BlueLink.Review/$arch/" *> (Join-Path $work 'payload.log')
            $payload = Get-Content -LiteralPath $ownership -Raw | ConvertFrom-Json
            & (Join-Path $work 'Installation.Tests/BlueLink.Installation.Tests.exe') $stage (Join-Path $root ('.acceptance/install-ownership-' + [Guid]::NewGuid().ToString('N'))) *> (Join-Path $work 'ownership.log')
            if ($LASTEXITCODE -ne 0) { throw 'Installation ownership verification failed.' }
            foreach ($component in @('Package','Bundle')) { Restore-Project "installer/BlueLink.$component/BlueLink.$component.wixproj" '' (Join-Path $work "restore-$component.log") }
            $productCode = [Guid]::NewGuid().ToString('B')
            $provider = 'BlueLink.Review.' + [Guid]::NewGuid().ToString('N')
            $common = $buildOptions + @("-p:ProductVersion=$version","-p:ProductCode=$productCode","-p:PackageUpgradeCode=$packageUpgrade","-p:BundleUpgradeCode=$bundleUpgrade","-p:BundleProviderKey=$provider","-p:ProductRegistryKey=$registration","-p:ManifestPath=$ownership")
            $msiArgs = @('build','installer/BlueLink.Package/BlueLink.Package.wixproj','-t:Rebuild') + $common + @("-p:Platform=$arch","-p:InstallerPlatform=$arch",'-p:MajorUpgradeSchedule=afterInstallInitialize','-p:AllowSameVersionUpgrades=yes',"-p:GeneratedPayloadPath=$generated","-p:ProductName=BlueLink Review ($arch)","-p:LauncherExe=$stage\BlueLink.exe","-p:LauncherConfig=$stage\BlueLink.exe.config","-p:UninstallExe=$stage\Uninstall.exe","-p:UninstallConfig=$stage\Uninstall.exe.config")
            foreach ($name in @('ApplicationComponentGuid','UninstallerComponentGuid','ManifestComponentGuid','DownloadComponentGuid','StartMenuComponentGuid','DesktopComponentGuid','AutoStartComponentGuid')) { $msiArgs += "-p:$name=" + [Guid]::NewGuid().ToString('B') }
            Invoke-Dotnet $msiArgs (Join-Path $work 'msi-build.log')
            $msi = Join-Path $root "installer/BlueLink.Package/bin/$arch/Release/BlueLink.Package.msi"
            Invoke-Dotnet (@('build','installer/BlueLink.Bundle/BlueLink.Bundle.wixproj','-t:Rebuild') + $common + @("-p:Platform=$hostArch","-p:InstallerPlatform=$hostArch","-p:TargetArchitecture=$arch",'-p:BundledDesktopRuntime=1',"-p:BundleName=BlueLink Review ($arch)","-p:MsiPath=$msi","-p:BaOutput=$work\SetupUI",'-p:IncludePrerequisites=no',"-p:PayloadFingerprint=$($payload.PayloadFingerprint)")) (Join-Path $work 'bundle-build.log')
            $exe = Join-Path $root "installer/BlueLink.Bundle/bin/$hostArch/Release/BlueLink.Bundle.exe"
            # WiX does not track every preprocessor property in incremental outputs.
            # Rebuild above, then verify the embedded architecture and exact MSI before publishing.
            $wixAssets = Get-Content -LiteralPath 'installer/BlueLink.Bundle/obj/project.assets.json' -Raw | ConvertFrom-Json
            $wixPath = @($wixAssets.packageFolders.PSObject.Properties.Name | ForEach-Object { Join-Path $_ 'wixtoolset.sdk/4.0.6/tools/net6.0/wix.dll' } | Where-Object { Test-Path -LiteralPath $_ }) | Select-Object -First 1
            if (-not $wixPath) { throw 'WiX extraction tool missing from restored package cache.' }
            $extracted = Join-Path $work 'bundle-content'
            $ba = Join-Path $work 'bundle-ba'
            Invoke-Dotnet @($wixPath,'burn','extract',$exe,'-o',$extracted,'-oba',$ba) (Join-Path $work 'bundle-verification.log')
            [xml]$burn = Get-Content -LiteralPath (Join-Path $ba 'manifest.xml') -Raw
            $variables = @{}
            foreach ($variable in $burn.BurnManifest.Variable) { $variables[$variable.Id] = $variable.Value }
            if ($variables.TargetArchitecture -ne $arch -or $variables.BundledDesktopRuntime -ne '1' -or $variables.ExpectedPayloadFingerprint -ne $payload.PayloadFingerprint -or $variables.ExpectedMsiProductCode -ne $productCode) { throw 'Embedded Burn metadata does not match this architecture/payload.' }
            $embedded = @(Get-ChildItem -LiteralPath $extracted -Recurse -Filter '*.msi')
            if ($embedded.Count -ne 1 -or (Get-FileHash -LiteralPath $embedded[0].FullName).Hash -ne (Get-FileHash -LiteralPath $msi).Hash) { throw 'Burn contains a stale or mismatched MSI.' }
            if ((Get-FileHash -LiteralPath (Join-Path $ba 'incoming-install-manifest.json')).Hash -ne (Get-FileHash -LiteralPath $ownership).Hash) { throw 'Embedded recovery manifest mismatch.' }
            Assert-PeMachine (Join-Path $ba 'mbanative.dll') $hostArch
            $msiTemplate = [BlueLinkPackageSummary]::GetTemplate($msi).Split(';')[0]
            if ($msiTemplate -ne @{ x86='Intel'; x64='x64'; arm64='Arm64' }[$arch]) { throw "MSI platform mismatch: $msiTemplate vs $arch" }
            foreach ($kind in @('msi','exe')) {
                $source = if ($kind -eq 'msi') { $msi } else { $exe }
                $destination = Join-Path $OutputDirectory "BlueLink-Review-$version-$rid-Setup.$kind"
                Copy-Item -LiteralPath $source -Destination $destination
                Record-Artifact $destination $arch 'ReviewInstaller'
            }
        }
        $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'build-manifest.json') -Encoding utf8
        $manifest | ForEach-Object { "$($_.Sha256)  $($_.File)" } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
    }
    Write-Host "Windows artifacts: $OutputDirectory"
} finally { Pop-Location; $env:DOTNET_CLI_HOME = $previousCli; $env:NUGET_PACKAGES = $previousPackages }
