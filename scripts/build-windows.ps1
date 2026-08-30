$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $root '.tooling\dotnet\dotnet.exe'
$toolDotnet = 'D:\Tool\dotnet-sdk-8\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet }
    elseif (Test-Path -LiteralPath $toolDotnet) { $toolDotnet }
    else { (Get-Command dotnet -ErrorAction Stop).Source }
$sdkList = & $dotnet '--list-sdks'
if (-not $sdkList) { throw '.NET SDK not found. Install the .NET 8 SDK (the runtime alone is insufficient).' }
$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root '.nuget-mirror-test'
$solution = Join-Path $root 'windows\BlueLink.sln'
& $dotnet 'restore' $solution '--configfile' (Join-Path $root 'NuGet.config') '--ignore-failed-sources' '-p:NuGetAudit=false'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet 'build' $solution '-c' 'Release' '--no-restore'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
