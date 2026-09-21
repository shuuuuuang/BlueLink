param([Parameter(Mandatory=$true)][string]$WorkDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$work=[IO.Path]::GetFullPath($WorkDirectory)
$boundary=(Join-Path $root '.acceptance').TrimEnd('\')+'\'
if (-not $work.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $work) -notlike 'msi-directory-test-*' -or (Test-Path -LiteralPath $work)) { throw 'Use a new isolated .acceptance/msi-directory-test-* directory.' }
for($parent=[IO.DirectoryInfo]::new($work);$null -ne $parent;$parent=$parent.Parent){if($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked test directory.'}}
New-Item -ItemType Directory -Path $work | Out-Null
$token=[Guid]::NewGuid().ToString('N')
$registryKey='Software\BlueLink.DirectoryTest'+$token
$upgrade=[Guid]::NewGuid().ToString('B')
$bundle=[Guid]::NewGuid().ToString('B')
$oldProduct=[Guid]::NewGuid().ToString('B')
$newProduct=[Guid]::NewGuid().ToString('B')
$installed=Join-Path $work 'custom location/蓝联'
$explicit=Join-Path $work 'explicit choice/BlueLink'
$source=Join-Path $work 'fixture.txt'
[IO.File]::WriteAllText($source,'Isolated non-executable MSI directory test fixture.')
$payload=Join-Path $work 'Payload.wxs'
$fileId=[Guid]::NewGuid().ToString('B')
[IO.File]::WriteAllText($payload,"<Wix xmlns=`"http://wixtoolset.org/schemas/v4/wxs`"><Fragment><ComponentGroup Id=`"PublishedPayloadComponents`"><Component Id=`"ProbeComponent`" Directory=`"APPFOLDER`" Guid=`"$fileId`"><File Source=`"$source`" Name=`"directory-probe.txt`" KeyPath=`"yes`" /></Component></ComponentGroup></Fragment></Wix>")
$cache=Join-Path $root '.nuget-installer'
$wix=Join-Path $cache 'wixtoolset.sdk/4.0.6/tools/net472/x64/wix.exe'
foreach($variant in @('old','new')){
    $variables=[ordered]@{
        ProductName="BlueLink Directory Test $token"; ProductVersion='0.2.17'; ProductCode=$(if($variant -eq 'old'){$oldProduct}else{$newProduct})
        PackageUpgradeCode=$upgrade; BundleUpgradeCode=$bundle; BundleProviderKey="DirectoryTest.$token"; ProductRegistryKey=$registryKey
        LauncherExe=$source; LauncherConfig=$source; UninstallExe=$source; UninstallConfig=$source; ManifestPath=$source
        StartMenuComponentCondition='0'; MajorUpgradeSchedule='afterInstallInitialize'; AllowSameVersionUpgrades='yes'; NativeMsiUi='yes'; BundledDesktopRuntime='1'
    }
    foreach($component in @('Application','Uninstaller','Manifest','Download','StartMenu','Desktop','AutoStart')){$variables[$component+'ComponentGuid']=[Guid]::NewGuid().ToString('B')}
    $arguments=@('build',(Join-Path $root 'installer/BlueLink.Package/Package.wxs'),(Join-Path $root 'installer/BlueLink.Package/NativeUI.wxs'),$payload,'-arch','x64','-culture','zh-CN','-loc',(Join-Path $root 'installer/BlueLink.Package/NativeUI.zh-CN.wxl'),'-pdbtype','none','-o',"$work\$variant.msi",'-ext',(Join-Path $cache 'wixtoolset.ui.wixext/4.0.6/wixext4/WixToolset.UI.wixext.dll'))
    foreach($entry in $variables.GetEnumerator()){$arguments+=@('-d',"$($entry.Key)=$($entry.Value)")}
    & $wix @arguments *> "$work\build-$variant.log"
    if($LASTEXITCODE -ne 0){throw "Fixture build failed: $work\build-$variant.log"}
}
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class BlueLinkDirectoryProbe {
 [DllImport("msi.dll", CharSet=CharSet.Unicode)] static extern uint MsiOpenPackageExW(string path,uint flags,out uint handle);
 [DllImport("msi.dll", CharSet=CharSet.Unicode)] static extern uint MsiSetPropertyW(uint handle,string name,string value);
 [DllImport("msi.dll", CharSet=CharSet.Unicode)] static extern uint MsiGetPropertyW(uint handle,string name,StringBuilder value,ref uint size);
 [DllImport("msi.dll", CharSet=CharSet.Unicode)] static extern uint MsiDoActionW(uint handle,string action);
 [DllImport("msi.dll", CharSet=CharSet.Unicode)] static extern int MsiEvaluateConditionW(uint handle,string condition);
 [DllImport("msi.dll")] static extern uint MsiSetInternalUI(uint level,IntPtr window);
 [DllImport("msi.dll")] static extern uint MsiCloseHandle(uint handle);
 static void Check(uint result){if(result!=0)throw new Exception("MSI probe failed: "+result);}
 static string Get(uint handle,string key){uint size=32767;var value=new StringBuilder((int)size);Check(MsiGetPropertyW(handle,key,value,ref size));return value.ToString();}
 public static string[] Read(string msi,string explicitDirectory){
   uint handle=0;var oldUi=MsiSetInternalUI(2,IntPtr.Zero);
   try {
     Check(MsiOpenPackageExW(msi,1,out handle));
     if(explicitDirectory!=null)Check(MsiSetPropertyW(handle,"INSTALLFOLDER",explicitDirectory));
     Check(MsiDoActionW(handle,"AppSearch"));
     if(MsiEvaluateConditionW(handle,"NOT INSTALLFOLDER AND BLUELINK_PREVIOUS_INSTALLFOLDER")==1)Check(MsiDoActionW(handle,"UsePreviousBlueLinkFolder"));
     foreach(var action in new[]{"CostInitialize","FileCost","CostFinalize"})Check(MsiDoActionW(handle,action));
     return new[]{Get(handle,"INSTALLFOLDER"),Get(handle,"BLUELINK_PREVIOUS_INSTALLFOLDER")};
   } finally {if(handle!=0)MsiCloseHandle(handle);MsiSetInternalUI(oldUi,IntPtr.Zero);}
 }
}
'@
function Same-Path([string]$Actual,[string]$Expected){if(-not [IO.Path]::GetFullPath($Actual).TrimEnd('\').Equals([IO.Path]::GetFullPath($Expected).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)){throw "Directory mismatch: $Actual vs $Expected"}}
function Quiet-Msi([string]$Arguments){$process=Start-Process msiexec.exe -ArgumentList $Arguments -WindowStyle Hidden -PassThru -Wait; if($process.ExitCode -ne 0){throw "Isolated MSI failed: $($process.ExitCode)"}}
$candidate=Join-Path $work 'new.msi'
$fresh=[BlueLinkDirectoryProbe]::Read($candidate,$null)
Same-Path $fresh[0] (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/BlueLink')
if($fresh[1]){throw 'Fresh fixture discovered unrelated installation.'}
Write-Output 'PASS: fresh channel uses normal default and does not discover production/Review.'
Quiet-Msi "/i `"$work\old.msi`" /qn /norestart INSTALLFOLDER=`"$installed`" CREATE_DESKTOP_SHORTCUT=0 AUTOSTART=0 /l*v `"$work\first-install.log`""
$sentinel=Join-Path $installed 'Download/preserve.txt'
[IO.File]::WriteAllText($sentinel,$token)
$found=[BlueLinkDirectoryProbe]::Read($candidate,$null)
Same-Path $found[0] $installed
Same-Path $found[1] $installed
Write-Output 'PASS: a new product finds the registered custom path before directory costing.'
$chosen=[BlueLinkDirectoryProbe]::Read($candidate,$explicit)
Same-Path $chosen[0] $explicit
Write-Output 'PASS: explicit directory has priority over an existing registered location.'
Quiet-Msi "/i `"$candidate`" /qn /norestart CREATE_DESKTOP_SHORTCUT=0 AUTOSTART=0 /l*v `"$work\upgrade.log`""
if(-not (Test-Path -LiteralPath (Join-Path $installed 'app/directory-probe.txt'))){throw 'Upgrade did not use previous directory.'}
if([IO.File]::ReadAllText($sentinel) -ne $token){throw 'Upgrade changed test user file.'}
$key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registryKey)
try{Same-Path ([string]$key.GetValue('InstallFolder')) $installed; if($key.GetValue('MsiProductCode') -ne $newProduct){throw 'New product was not installed.'}}finally{$key.Dispose()}
Write-Output 'PASS: real quiet upgrade without INSTALLFOLDER uses old custom directory and preserves Download.'
Quiet-Msi "/x $newProduct /qn /norestart /l*v `"$work\uninstall.log`""
if(Test-Path -LiteralPath (Join-Path $installed 'BlueLink.exe')){throw 'Fixture files remain after removal.'}
if([IO.File]::ReadAllText($sentinel) -ne $token){throw 'Uninstall changed user file.'}
$removed=[BlueLinkDirectoryProbe]::Read($candidate,$null)
if($removed[1]){throw 'Removed fixture is still discovered.'}
Write-Output 'PASS: silent uninstall retains user file and clears directory discovery.'
@{OldProduct=$oldProduct;NewProduct=$newProduct;RegistryKey=$registryKey;InstallDirectory=$installed;ProbeOnlyDefault=$fresh[0]} | ConvertTo-Json | Set-Content "$work\fixture.json"
