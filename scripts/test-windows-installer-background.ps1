param(
    [string]$DotnetPath = 'D:\Tool\dotnet-sdk-8\dotnet.exe',
    [string]$OutputDirectory = '',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot ('.acceptance\windows-background\installer-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$previousCli = $env:DOTNET_CLI_HOME
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-home'
Push-Location $projectRoot
try {
    $project = 'installer\BlueLink.Installation.VisualTests\BlueLink.Installation.VisualTests.csproj'
    if (-not $SkipBuild) {
        & $DotnetPath build $project -c Release --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false *> (Join-Path $OutputDirectory 'build.log')
        if ($LASTEXITCODE -ne 0) { throw "Installer build failed. See $OutputDirectory\build.log" }
    }
    $verification = Join-Path $projectRoot 'installer\BlueLink.Installation.VisualTests\bin\Release\net472\win-x64\BlueLink.Installation.VisualTests.exe'
    & $verification --failure-reporting-self-test *> (Join-Path $OutputDirectory 'failure-handler.log')
    if ($LASTEXITCODE -ne 1 -or (Get-Content -LiteralPath (Join-Path $OutputDirectory 'failure-handler.log') -Raw) -notmatch '^Verification failed:') { throw 'Installer verification error boundary failed.' }
    & $verification (Join-Path $OutputDirectory 'layout') *> (Join-Path $OutputDirectory 'layout.log')
    if ($LASTEXITCODE -ne 0) { throw "Installer verification failed. See $OutputDirectory\layout.log" }
    Get-Content -LiteralPath (Join-Path $OutputDirectory 'layout.log')
    & (Join-Path $PSScriptRoot 'verify-installer-contract.ps1') *> (Join-Path $OutputDirectory 'contract.log')
    Write-Host "Installer background verification completed: $OutputDirectory"
} finally { Pop-Location; $env:DOTNET_CLI_HOME = $previousCli }
