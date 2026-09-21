param(
    [Parameter(Mandatory=$true)][string]$WorkDirectory,
    [Parameter(Mandatory=$true)][string]$CandidateStage,
    [Parameter(Mandatory=$true)][string]$CandidateBa,
    [Parameter(Mandatory=$true)][string]$BaselineBa,
    [switch]$NativeMsiUi,
    [switch]$Run
)
# Exact-MSI-first regression. All installed identities and paths are generated
# exclusively for this test; never use the Review or production upgrade family.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$work = [IO.Path]::GetFullPath($WorkDirectory)
$boundary = (Join-Path $root '.acceptance').TrimEnd('\') + '\'
if (-not $work.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase) -or
    (Split-Path -Leaf $work) -notlike 'install-msi-first-*' -or (Test-Path -LiteralPath $work)) { throw 'Use a new .acceptance/install-msi-first-* directory.' }
for ($dir = [IO.DirectoryInfo]::new($work); $null -ne $dir; $dir = $dir.Parent) {
    if ($dir.Exists -and ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked test path.' }
}
$cache = Join-Path $root '.nuget-installer'
$wix = Join-Path $cache 'wixtoolset.sdk/4.0.6/tools/net472/x64/wix.exe'
function Invoke-Wix([string[]]$Arguments, [string]$Log) {
    & $wix @Arguments *> $Log
    if ($LASTEXITCODE -ne 0) { throw "WiX failed: $Log" }
}
New-Item -ItemType Directory -Path $work | Out-Null
$id = 'MsiFirst' + [Guid]::NewGuid().ToString('N')
$registryKey = 'Software\BlueLink.' + $id
$packageUpgrade = [Guid]::NewGuid().ToString('B').ToUpperInvariant()
$bundleUpgrade = [Guid]::NewGuid().ToString('B').ToUpperInvariant()
$product = [Guid]::NewGuid().ToString('B').ToUpperInvariant()
$stage = Join-Path $work 'stage'
Copy-Item -LiteralPath $CandidateStage -Destination $stage -Recurse
$configPath = Join-Path $stage 'Uninstall.exe.config'
[xml]$config = Get-Content -LiteralPath $configPath -Raw
foreach ($setting in $config.configuration.appSettings.add) {
    if ($setting.key -eq 'ProductRegistryKey') { $setting.value = $registryKey }
    if ($setting.key -eq 'BundleUpgradeCode') { $setting.value = $bundleUpgrade }
}
$config.Save($configPath)
$manifestPath = Join-Path $stage '.bluelink-install.json'
$generated = Join-Path $work 'Payload.wxs'
& (Join-Path $PSScriptRoot 'generate-wix-payload.ps1') -AppPublishDir (Join-Path $stage 'app') -BootstrapDir (Join-Path $stage 'bootstrap') -ManifestPath $manifestPath -OutputFile $generated -Version '0.2.17' -GuidNamespace "$id/" *> (Join-Path $work 'payload.log')
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$msi = Join-Path $work 'Fixture.msi'
$variables = [ordered]@{
    ProductName="BlueLink $id"; ProductVersion='0.2.17'; ProductCode=$product
    PackageUpgradeCode=$packageUpgrade; BundleUpgradeCode=$bundleUpgrade; BundleProviderKey="$id.msi"
    ProductRegistryKey=$registryKey; LauncherExe=(Join-Path $stage 'BlueLink.exe')
    LauncherConfig=(Join-Path $stage 'BlueLink.exe.config'); UninstallExe=(Join-Path $stage 'Uninstall.exe')
    UninstallConfig=$configPath; ManifestPath=$manifestPath; StartMenuComponentCondition='0'
    MajorUpgradeSchedule='afterInstallInitialize'; AllowSameVersionUpgrades='yes'
}
foreach ($component in @('Application','Uninstaller','Manifest','Download','StartMenu','Desktop','AutoStart')) {
    $variables[$component + 'ComponentGuid'] = [Guid]::NewGuid().ToString('B')
}
if ($NativeMsiUi) {
    $variables.NativeMsiUi = 'yes'
    $variables.BundledDesktopRuntime = '1'
}
$arguments = @('build',(Join-Path $root 'installer/BlueLink.Package/Package.wxs'),$generated,'-arch','x64','-pdbtype','none','-o',$msi)
if ($NativeMsiUi) {
    $arguments += @((Join-Path $root 'installer/BlueLink.Package/NativeUI.wxs'),'-culture','zh-CN', '-loc', (Join-Path $root 'installer/BlueLink.Package/NativeUI.zh-CN.wxl'),
        '-ext',(Join-Path $cache 'wixtoolset.ui.wixext/4.0.6/wixext4/WixToolset.UI.wixext.dll'),
        '-ext',(Join-Path $cache 'wixtoolset.netfx.wixext/4.0.6/wixext4/WixToolset.Netfx.wixext.dll'))
}
foreach ($entry in $variables.GetEnumerator()) { $arguments += @('-d', "$($entry.Key)=$($entry.Value)") }
Invoke-Wix $arguments (Join-Path $work 'msi-build.log')
foreach ($variant in @('baseline','candidate')) {
    $ba = if ($variant -eq 'baseline') { $BaselineBa } else { $CandidateBa }
    $variables = [ordered]@{
        BundleName="BlueLink $id"; ProductVersion='0.2.17'; ProductCode=$product
        PackageUpgradeCode=$packageUpgrade; BundleUpgradeCode=$bundleUpgrade; BundleProviderKey="$id.$variant"
        ProductRegistryKey=$registryKey; MsiPath=$msi; BaOutput=$ba
        ManifestPath=$manifestPath; PayloadFingerprint=$manifest.PayloadFingerprint
        IconPath=(Join-Path $root 'design/brand/app-icons/windows/bluelink.ico')
        IncludePrerequisites='no'; RuntimeVersion='8.0.30'; RuntimeSize='bundled'
        TargetArchitecture='x64'; BundledDesktopRuntime='1'
    }
    $arguments = @('build',(Join-Path $root 'installer/BlueLink.Bundle/Bundle.wxs'),'-arch','x64','-pdbtype','none','-o',(Join-Path $work "$variant.exe"),
        '-ext',(Join-Path $cache 'wixtoolset.bal.wixext/4.0.6/wixext4/WixToolset.Bal.wixext.dll'),
        '-ext',(Join-Path $cache 'wixtoolset.netfx.wixext/4.0.6/wixext4/WixToolset.Netfx.wixext.dll'))
    foreach ($entry in $variables.GetEnumerator()) { $arguments += @('-d', "$($entry.Key)=$($entry.Value)") }
    Invoke-Wix $arguments (Join-Path $work "$variant-build.log")
}
$install = Join-Path $work 'installed/BlueLink'
@{ ProductCode=$product; PackageUpgradeCode=$packageUpgrade; BundleUpgradeCode=$bundleUpgrade; RegistryKey=$registryKey; Install=$install } |
    ConvertTo-Json | Set-Content (Join-Path $work 'fixture.json') -Encoding utf8
if (-not $Run) { Write-Output "Fixtures built: $work"; return }
function Invoke-Quiet([string]$Exe, [string]$Arguments, [int]$Expected=0) {
    $process = Start-Process -FilePath $Exe -ArgumentList $Arguments -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne $Expected) { throw "Unexpected exit $($process.ExitCode), expected $Expected for $Exe. Preserve fixture for investigation: $work" }
}
# The MSI and EXEs above were freshly generated with unique GUIDs. No installed
# Review/production identity is passed to Windows Installer or the Burn engine.
Invoke-Quiet 'msiexec.exe' "/i `"$msi`" /qn /norestart INSTALLFOLDER=`"$install`" ARPINSTALLLOCATION=`"$install`" CREATE_DESKTOP_SHORTCUT=0 AUTOSTART=0 /l*v `"$work/msi-install.log`""
$sentinel = Join-Path $install 'Download/msi-first-preserve.txt'
[IO.File]::WriteAllText($sentinel, $id)
Invoke-Quiet (Join-Path $work 'baseline.exe') "/quiet /norestart InstallFolder=`"$install`" CreateDesktopShortcut=0 AutoStart=0 /log `"$work/baseline.log`"" 1603
if (-not (Select-String -LiteralPath "$work/baseline.log" -SimpleMatch 'refusing apply because BlueLinkMsi plan is not executable')) { throw 'Did not reproduce original failure.' }
# Remove only this fixture's owned program file to prove repair copies it back.
$dll = Join-Path $install 'app/BlueLink.dll'
if (-not ([IO.Path]::GetFullPath($dll)).StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Repair probe escaped test boundary.' }
Remove-Item -LiteralPath $dll
Invoke-Quiet (Join-Path $work 'candidate.exe') "/quiet /norestart InstallFolder=`"$install`" CreateDesktopShortcut=0 AutoStart=0 /log `"$work/candidate.log`""
if (-not (Select-String -LiteralPath "$work/candidate.log" -SimpleMatch 'exact BlueLinkMsi is present; requesting package repair.')) { throw 'Repair branch was not reached.' }
if ((Get-FileHash -LiteralPath $dll).Hash -ne (Get-FileHash -LiteralPath (Join-Path $stage 'app/BlueLink.dll')).Hash) { throw 'Repair payload mismatch.' }
if ([IO.File]::ReadAllText($sentinel) -ne $id) { throw 'Repair changed fixture user file.' }
Invoke-Quiet (Join-Path $work 'candidate.exe') "/quiet /norestart InstallFolder=`"$install`" CreateDesktopShortcut=0 AutoStart=0 /log `"$work/repeat.log`""
Invoke-Quiet (Join-Path $work 'candidate.exe') "/uninstall /quiet /norestart /log `"$work/uninstall.log`""
if (Test-Path -LiteralPath $dll) { throw 'Fixture program was not removed.' }
if ([IO.File]::ReadAllText($sentinel) -ne $id) { throw 'Uninstall changed fixture user file.' }
Write-Output 'PASS: MSI first, old-BA failure reproduced, new-BA repair, restored exact DLL, repeat repair, isolated uninstall, Download retained.'
