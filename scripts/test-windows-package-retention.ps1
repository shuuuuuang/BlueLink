$ErrorActionPreference='Stop'
$fixture=Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) '.acceptance') ('retention-test-'+[Guid]::NewGuid().ToString('N')+'/artifacts/windows')
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
for($i=1;$i -le 5;$i++) {
    $dir=Join-Path $fixture "set-$i"
    New-Item -ItemType Directory -Path "$dir/nested","$dir/Download" -Force | Out-Null
    $records=@()
    foreach($name in @('BlueLink-Review-test-Setup.exe','BlueLink-Review-test-Setup.msi')) {
        $file=Join-Path $dir $name
        Set-Content -LiteralPath $file -Value "fixture-$i"
        (Get-Item -LiteralPath $file).LastWriteTimeUtc=[DateTime]::UtcNow.AddDays(-6+$i)
        $records+=@{File=$name;Size=(Get-Item $file).Length;Sha256=(Get-FileHash $file).Hash}
    }
    Set-Content -LiteralPath "$dir/nested/BlueLink.Package.msi" -Value 'cached package'
    Set-Content -LiteralPath "$dir/Download/BlueLink-test.msi" -Value 'protected user fixture'
    Set-Content -LiteralPath "$dir/BlueLink-android.apk" -Value 'preserve android'
    $records|ConvertTo-Json|Set-Content -LiteralPath "$dir/build-manifest.json"
    @('abcd  BlueLink-Review-test-Setup.exe','efgh  BlueLink-android.apk')|Set-Content -LiteralPath "$dir/SHA256SUMS.txt"
}
$helper=Join-Path $PSScriptRoot 'prune-windows-packages.ps1'
$preview=& $helper -ArtifactRoot $fixture -SuccessfulDirectory "$fixture/set-5"
if($preview.RemovedFiles -ne 6 -or -not(Test-Path "$fixture/set-1/BlueLink-Review-test-Setup.exe")){throw 'Dry run failed'}
Add-Content -LiteralPath "$fixture/set-5/BlueLink-Review-test-Setup.exe" -Value 'corrupt'
$rejected=$false
try { & $helper -ArtifactRoot $fixture -SuccessfulDirectory "$fixture/set-5" -Apply | Out-Null }catch{$rejected=$true}
if(-not $rejected -or -not(Test-Path "$fixture/set-1/BlueLink-Review-test-Setup.exe")){throw 'Integrity guard failed'}
Set-Content -LiteralPath "$fixture/set-5/BlueLink-Review-test-Setup.exe" -Value 'fixture-5'
$result=& $helper -ArtifactRoot $fixture -SuccessfulDirectory "$fixture/set-5" -Apply
if(($result.Retained -join ',') -ne 'set-5,set-4,set-3' -or $result.RemovedFiles -ne 6){throw 'Retention ordering failed'}
if(Test-Path "$fixture/set-1/nested/BlueLink.Package.msi"){throw 'Stale cached MSI survived'}
if(-not(Test-Path "$fixture/set-1/Download/BlueLink-test.msi") -or -not(Test-Path "$fixture/set-1/BlueLink-android.apk")){throw 'Protected file changed'}
if((Get-Content "$fixture/set-1/SHA256SUMS.txt") -ne 'efgh  BlueLink-android.apk'){throw 'Mixed checksum cleanup failed'}
$again=& $helper -ArtifactRoot $fixture -SuccessfulDirectory "$fixture/set-5" -Apply
if($again.RemovedFiles -ne 0){throw 'Retention not idempotent'}
'PASS: dry run, manifest corruption stops deletion, newest 3, nested cache deletion, protected data, Android/checksum preservation, idempotence'
