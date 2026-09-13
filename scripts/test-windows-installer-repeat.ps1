param(
    [ValidateSet('Build','Run')][string]$Mode = 'Build',
    [Parameter(Mandatory=$true)][string]$WorkDirectory,
    [string]$CandidateStage, [string]$BaselineStage, [string]$BrokenStage, [string]$BaselineBa,
    [switch]$ResumeFromBaseline
)
# Real Burn/MSI tests with a unique family; never launch the client.
# Silent Burn still writes a temporary RunOnce entry for restart recovery, which
# antivirus software can prompt about. Build mode only creates packages.
# Run mode is not part of the background UI suite and stops on unexpected errors.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$work = [IO.Path]::GetFullPath($WorkDirectory)
$boundary = (Join-Path $root '.acceptance').TrimEnd('\') + '\'
if (-not $work.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase) -or
    (Split-Path -Leaf $work) -notlike 'install-repeat-*') { throw 'Requires .acceptance/install-repeat-*.' }
for ($dir = [IO.DirectoryInfo]::new($work); $null -ne $dir; $dir = $dir.Parent) {
    if ($dir.Exists -and ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked test path.' }
}
$metadataPath = Join-Path $work 'fixtures.json'
$wix = 'E:\BlueLink\.nuget-mirror-test\wixtoolset.sdk\4.0.6\tools\net472\x64\wix.exe'
function Invoke-Wix([string[]]$Arguments, [string]$Log) {
    & $wix @Arguments *> $Log
    if ($LASTEXITCODE -ne 0) { throw "WiX failed: $Log" }
}
if ($Mode -eq 'Build') {
    if (Test-Path -LiteralPath $work) { throw 'Use a new directory to preserve evidence.' }
    New-Item -ItemType Directory -Path $work | Out-Null
    $id = 'InstallRepeat' + [Guid]::NewGuid().ToString('N')
    $registryKey = 'Software\BlueLink' + $id
    $packageUpgrade = [Guid]::NewGuid().ToString('B').ToUpperInvariant()
    $bundleUpgrade = [Guid]::NewGuid().ToString('B').ToUpperInvariant()
    $fixtures = @()
    $oldBa = Join-Path $work 'old-ba'
    Copy-Item -LiteralPath $BaselineBa -Destination $oldBa -Recurse
    Copy-Item -LiteralPath (Join-Path $oldBa 'WixToolset.Mba.Host.config') -Destination (Join-Path $oldBa 'BlueLink.SetupUI.BootstrapperCore.config')
    $BaselineBa = $oldBa
    $currentBa = Join-Path $root 'installer\BlueLink.SetupUI\bin\Release\net472\win-x64'
    $definitions = @(
        @{Name='baseline'; Source=$BaselineStage; Ba=$BaselineBa; Version='0.2.17'; Fixed=$false},
        @{Name='broken'; Source=$BrokenStage; Ba=$BaselineBa; Version='0.2.17'; Fixed=$false},
        @{Name='candidate'; Source=$CandidateStage; Ba=$currentBa; Version='0.2.17'; Fixed=$true},
        @{Name='rollback'; Source=$CandidateStage; Ba=$currentBa; Version='0.2.18'; Fixed=$true}
    )
    foreach ($definition in $definitions) {
        $name = $definition.Name
        $directory = Join-Path $work $name
        $stage = Join-Path $directory 'stage'
        New-Item -ItemType Directory -Path $stage -Force | Out-Null
        Copy-Item -Path (Join-Path $definition.Source '*') -Destination $stage -Recurse -Force
        $configPath = Join-Path $stage 'Uninstall.exe.config'
        [xml]$config = Get-Content -LiteralPath $configPath -Raw
        foreach ($setting in $config.configuration.appSettings.add) {
            if ($setting.key -eq 'ProductRegistryKey') { $setting.value = $registryKey }
            if ($setting.key -eq 'BundleUpgradeCode') { $setting.value = $bundleUpgrade }
        }
        $config.Save($configPath)
        $manifestPath = Join-Path $stage '.bluelink-install.json'
        $generated = Join-Path $directory 'Payload.wxs'
        & (Join-Path $PSScriptRoot 'generate-wix-payload.ps1') -AppPublishDir (Join-Path $stage 'app') -BootstrapDir (Join-Path $stage 'bootstrap') -ManifestPath $manifestPath -OutputFile $generated -Version $definition.Version -GuidNamespace "$id/" *> (Join-Path $directory 'payload.log')
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $product = [Guid]::NewGuid().ToString('B').ToUpperInvariant()
        $provider = "$id.$name"
        $source = Get-Content -LiteralPath (Join-Path $root 'installer\BlueLink.Package\Package.wxs') -Raw
        if ($name -eq 'rollback') {
            $injection = '<CustomAction Id="FailIsolatedUpgrade" Error="Intentional isolated rollback verification" />' +
                '<InstallExecuteSequence><Custom Action="FailIsolatedUpgrade" After="InstallFiles" Condition="NOT Installed" /></InstallExecuteSequence>'
            $source = $source.Replace('    <MediaTemplate', $injection + '    <MediaTemplate')
        }
        $sourcePath = Join-Path $directory 'Package.wxs'
        $source | Set-Content -LiteralPath $sourcePath -Encoding utf8
        $msi = Join-Path $directory 'Fixture.msi'
        $variables = [ordered]@{
            ProductName="BlueLink $id"; ProductVersion=$definition.Version; ProductCode=$product
            PackageUpgradeCode=$packageUpgrade; BundleUpgradeCode=$bundleUpgrade; BundleProviderKey=$provider
            ProductRegistryKey=$registryKey; LauncherExe=(Join-Path $stage 'BlueLink.exe')
            LauncherConfig=(Join-Path $stage 'BlueLink.exe.config'); UninstallExe=(Join-Path $stage 'Uninstall.exe')
            UninstallConfig=$configPath; ManifestPath=$manifestPath; StartMenuComponentCondition='0'
            MajorUpgradeSchedule=$(if($definition.Fixed){'afterInstallInitialize'}else{'afterInstallExecute'})
            AllowSameVersionUpgrades=$(if($definition.Fixed){'yes'}else{'no'})
        }
        foreach ($component in @('Application','Uninstaller','Manifest','Download','StartMenu','Desktop','AutoStart')) {
            $variables[$component + 'ComponentGuid'] = [Guid]::NewGuid().ToString('B')
        }
        $arguments = @('build',$sourcePath,$generated,'-arch','x64','-pdbtype','none','-o',$msi)
        foreach ($entry in $variables.GetEnumerator()) { $arguments += @('-d', "$($entry.Key)=$($entry.Value)") }
        Invoke-Wix $arguments (Join-Path $directory 'msi-build.log')
        $exe = Join-Path $directory 'Fixture.exe'
        $variables = [ordered]@{
            BundleName="BlueLink $id"; ProductVersion=$definition.Version; ProductCode=$product
            PackageUpgradeCode=$packageUpgrade; BundleUpgradeCode=$bundleUpgrade; BundleProviderKey=$provider
            ProductRegistryKey=$registryKey; MsiPath=$msi; BaOutput=$definition.Ba
            ManifestPath=$manifestPath; PayloadFingerprint=$manifest.PayloadFingerprint
            IconPath=(Join-Path $root 'design\brand\app-icons\windows\bluelink.ico')
            IncludePrerequisites='no'; RuntimeVersion='8.0.30'; RuntimeSize='not included'
        }
        $arguments = @('build',(Join-Path $root 'installer\BlueLink.Bundle\Bundle.wxs'),'-arch','x64','-pdbtype','none','-o',$exe,
            '-ext','E:\BlueLink\.nuget-mirror-test\wixtoolset.bal.wixext\4.0.6\wixext4\WixToolset.Bal.wixext.dll',
            '-ext','E:\BlueLink\.nuget-mirror-test\wixtoolset.netfx.wixext\4.0.6\wixext4\WixToolset.Netfx.wixext.dll')
        foreach ($entry in $variables.GetEnumerator()) { $arguments += @('-d', "$($entry.Key)=$($entry.Value)") }
        Invoke-Wix $arguments (Join-Path $directory 'bundle-build.log')
        $fixtures += [ordered]@{Name=$name; Exe=$exe; Sha256=(Get-FileHash -LiteralPath $exe).Hash; ProductCode=$product; Provider=$provider; Stage=$stage}
        Write-Host "Built isolated fixture: $name"
    }
    [ordered]@{Id=$id; RegistryKey=$registryKey; PackageUpgradeCode=$packageUpgrade; BundleUpgradeCode=$bundleUpgrade; Fixtures=$fixtures} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $metadataPath -Encoding utf8
    return
}
$data = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
if ($data.Id -notmatch '^InstallRepeat[0-9a-f]{32}$' -or $data.RegistryKey -ne ('Software\BlueLink'+$data.Id)) { throw 'Invalid isolated family.' }
foreach ($identity in @($data.PackageUpgradeCode,$data.BundleUpgradeCode)) {
    if ($identity -in @('{1968FCA3-785D-49B1-9328-62EC6101FB9A}','{BEF13DA0-A4EB-44EB-82F2-0A608304CC25}','{BE8A6A43-710C-4B6E-92DC-07C4FAFC22B9}','{6B6EB4AB-16E2-4E54-BBBE-556061F7D4A7}')) { throw 'Refusing Review or production identity.' }
}
$registry = 'HKCU:\' + $data.RegistryKey
$install = Join-Path $work 'installed\BlueLink'
if (-not $ResumeFromBaseline -and ((Test-Path -LiteralPath $registry) -or (Test-Path -LiteralPath $install))) { throw 'Existing test state; preserve it for investigation.' }
$fixtures = @{}
foreach ($fixture in $data.Fixtures) {
    if ($fixture.Exe -ne (Join-Path $work ($fixture.Name+'\Fixture.exe')) -or
        (Get-FileHash -LiteralPath $fixture.Exe).Hash -ne $fixture.Sha256 -or
        $fixture.Provider -ne ($data.Id+'.'+$fixture.Name)) { throw 'Fixture identity/path/hash mismatch.' }
    $fixtures[$fixture.Name] = $fixture
}
function Get-ProtectedState {
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($dir in @('D:\BlueLink',(Join-Path $env:LOCALAPPDATA 'BlueLink'))) {
        if (Test-Path -LiteralPath $dir) {
            foreach ($file in Get-ChildItem -LiteralPath $dir -Recurse -File -Force) {
                $lines.Add($file.FullName+'|'+(Get-FileHash -LiteralPath $file.FullName).Hash)
            }
        }
    }
    foreach ($key in @('HKCU:\Software\BlueLink','HKCU:\Software\BlueLink.Review')) {
        if (Test-Path -LiteralPath $key) { $lines.Add((Get-ItemProperty -LiteralPath $key | ConvertTo-Json -Compress)) }
    }
    return ($lines | Sort-Object) -join [Environment]::NewLine
}
Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public static class RepeatMsiState { [DllImport("msi.dll", CharSet=CharSet.Unicode)] public static extern int MsiQueryProductState(string productCode); }'
$protectedBefore = Get-ProtectedState
$steps = [Collections.Generic.List[object]]::new()
$common = @('-quiet','-norestart',('InstallFolder="'+$install+'"'),'CreateDesktopShortcut=0','AutoStart=0')
function Invoke-Fixture([string]$Name,[string]$Step,[bool]$ExpectSuccess=$true,[switch]$Uninstall) {
    $log = Join-Path $work ($Step+'.log')
    if (Test-Path -LiteralPath $log) { Copy-Item -LiteralPath $log -Destination ($log+'.previous-'+(Get-Date -Format 'HHmmss')) }
    $arguments = $common + @('-log',('"'+$log+'"'))
    if ($Uninstall) { $arguments += '-uninstall' }
    $process = Start-Process -FilePath $fixtures[$Name].Exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(180000)) { throw "Fixture timed out: $Step, PID $($process.Id). Preserve for investigation." }
    $exitCode = $process.ExitCode
    $process.Dispose()
    if ((Get-ProtectedState) -ne $protectedBefore) { throw 'Protected application or user data changed.' }
    $steps.Add([ordered]@{Step=$Step; ExitCode=$exitCode; ExpectedSuccess=$ExpectSuccess})
    $steps | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'steps.json') -Encoding utf8
    Write-Host "$Step exited $exitCode"
    if ($ExpectSuccess -and $exitCode -notin @(0,3010)) { throw "$Step failed: $exitCode" }
    if (-not $ExpectSuccess -and $exitCode -eq 0) { throw "$Step unexpectedly succeeded." }
}
function Assert-Payload([string]$Name) {
    $fixture = $fixtures[$Name]
    $expected = Get-Content -LiteralPath (Join-Path $fixture.Stage '.bluelink-install.json') -Raw | ConvertFrom-Json
    $installed = Get-Content -LiteralPath (Join-Path $install '.bluelink-install.json') -Raw | ConvertFrom-Json
    if ($expected.PayloadFingerprint -ne $installed.PayloadFingerprint) { throw 'Installed manifest mismatch.' }
    foreach ($file in $expected.PayloadFiles) {
        if ((Get-FileHash -LiteralPath (Join-Path $install $file.Path)).Hash -ne $file.Sha256) { throw "Installed bytes mismatch: $($file.Path)" }
    }
    if ([RepeatMsiState]::MsiQueryProductState($fixture.ProductCode) -ne 5) { throw 'Expected MSI is not installed.' }
    foreach ($other in $fixtures.Values) {
        if ($other.ProductCode -ne $fixture.ProductCode -and [RepeatMsiState]::MsiQueryProductState($other.ProductCode) -eq 5) { throw 'Multiple related MSIs remain installed.' }
    }
    $registered = Get-ItemProperty -LiteralPath $registry
    if ($registered.MsiProductCode -ne $fixture.ProductCode -or $registered.BundleProviderKey -ne $fixture.Provider -or
        ([IO.Path]::GetFullPath($registered.InstallFolder)).TrimEnd('\') -ne $install) { throw 'Installed registration mismatch.' }
    if (Test-Path -LiteralPath (Join-Path $install 'Download\keep.txt')) {
        if ((Get-Content -LiteralPath (Join-Path $install 'Download\keep.txt') -Raw) -ne 'preserve user file') { throw 'Download changed.' }
    }
}
function Assert-Removed {
    if (Test-Path -LiteralPath $registry) { throw 'Product registry remains.' }
    foreach ($fixture in $fixtures.Values) {
        if ([RepeatMsiState]::MsiQueryProductState($fixture.ProductCode) -eq 5) { throw 'MSI remains installed after removal.' }
    }
    foreach ($entry in Get-ChildItem -LiteralPath $install -Force) {
        if ($entry.Name -ne 'Download') { throw "Program remains: $($entry.Name)" }
    }
    if ((Get-Content -LiteralPath (Join-Path $install 'Download\keep.txt') -Raw) -ne 'preserve user file') { throw 'Uninstall changed Download.' }
}
if ($ResumeFromBaseline) {
    Assert-Payload baseline
    Copy-Item -LiteralPath (Join-Path $work 'steps.json') -Destination (Join-Path $work ('steps-before-retry-'+(Get-Date -Format 'HHmmss')+'.json'))
} else {
    Invoke-Fixture baseline '01-baseline'
    Assert-Payload baseline
    [IO.File]::WriteAllText((Join-Path $install 'Download\keep.txt'), 'preserve user file')
}
Invoke-Fixture broken '02-reproduce-original-failure' $false
if ((Test-Path -LiteralPath (Join-Path $install 'BlueLink.exe')) -or (Test-Path -LiteralPath $registry)) { throw 'Original removal bug was not reproduced.' }
if ([RepeatMsiState]::MsiQueryProductState($fixtures.broken.ProductCode) -ne 5) { throw 'Expected partial MSI registration was not reproduced.' }
Invoke-Fixture candidate '03-recover-partial-install'
Assert-Payload candidate
Invoke-Fixture candidate '04-repeat-same-package'
Assert-Payload candidate
Invoke-Fixture rollback '05-rollback-failed-upgrade' $false
$rollbackLog = Get-Content -LiteralPath (Join-Path $work '05-rollback-failed-upgrade_000_BlueLinkMsi.log') -Raw
if (-not $rollbackLog.Contains('Intentional isolated rollback verification') -or -not $rollbackLog.Contains('RemoveExistingProducts')) { throw 'Rollback fault did not execute inside the MSI upgrade.' }
Assert-Payload candidate
Invoke-Fixture candidate '06-uninstall' $true -Uninstall
Assert-Removed
Invoke-Fixture baseline '07-baseline-with-download'
Assert-Payload baseline
Invoke-Fixture candidate '08-healthy-same-version-upgrade'
Assert-Payload candidate
Invoke-Fixture candidate '09-uninstall' $true -Uninstall
Assert-Removed
Invoke-Fixture candidate '10-fresh-candidate'
Assert-Payload candidate
Invoke-Fixture candidate '11-final-uninstall' $true -Uninstall
Assert-Removed
$left = @(Get-ChildItem -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' | ForEach-Object {
    Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue
} | Where-Object { $_.BundleProviderKey -like ($data.Id+'.*') })
if ($left.Count) { throw 'Isolated Burn registration remains.' }
[ordered]@{Passed=$true; Operations=$steps; ProtectedApplicationAndDataUnchanged=$true; DownloadPreserved=$true; ComputerUse=$false} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $work 'results.json') -Encoding utf8
Write-Host 'Silent real MSI/Burn repeat-install matrix passed.'
