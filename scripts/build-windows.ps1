param(
    [ValidateSet('Bundled','External','Both')][string]$InstallerRuntime = 'Both',
    [ValidateSet('x86','x64','arm64','all')][string[]]$Architecture = @('x64'),
    [ValidateSet('None','Installer','Portable','Both')][string]$Package = 'None'
)
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
if ($Package -ne 'None') {
    & (Join-Path $PSScriptRoot 'build-windows-packages.ps1') -Architecture $Architecture -Format $Package -InstallerRuntime $InstallerRuntime -DotnetPath $dotnet
    exit $LASTEXITCODE
}
$architectures = if ($Architecture -contains 'all') { @('x86','x64','arm64') } else { @($Architecture | Select-Object -Unique) }
$solution = Join-Path $root 'windows\BlueLink.sln'
foreach ($arch in $architectures) {
& $dotnet 'restore' $solution '--configfile' (Join-Path $root 'NuGet.config') '--ignore-failed-sources' '-p:NuGetAudit=false' '-r' "win-$arch"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet 'build' $solution '-c' 'Release' '--no-restore' '-r' "win-$arch"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

}
