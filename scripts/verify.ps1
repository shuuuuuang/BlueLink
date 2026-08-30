$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bundled = 'D:\Tool\gradle-8.5\bin\gradle.bat'
$gradle = if ($env:BLUELINK_GRADLE) { $env:BLUELINK_GRADLE }
    elseif (Get-Command gradle -ErrorAction SilentlyContinue) { (Get-Command gradle).Source }
    elseif (Test-Path -LiteralPath $bundled) { $bundled }
    else { Join-Path $root 'gradlew.bat' }
& $gradle '--no-daemon' '-p' $root 'verify'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$localDotnet = Join-Path $root '.tooling\dotnet\dotnet.exe'
$toolDotnet = 'D:\Tool\dotnet-sdk-8\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet }
    elseif (Test-Path -LiteralPath $toolDotnet) { $toolDotnet }
    else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root '.nuget-mirror-test'
$verificationProject = Join-Path $root 'windows\BlueLink.TransferVerification\BlueLink.TransferVerification.csproj'
& $dotnet 'restore' $verificationProject '--configfile' (Join-Path $root 'NuGet.config') '--ignore-failed-sources' '-p:NuGetAudit=false'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet 'run' '--project' $verificationProject '-c' 'Release' '--no-restore'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
