param(
    [string]$DotnetPath = 'D:\Tool\dotnet-sdk-8\dotnet.exe',
    [string]$OutputDirectory = '',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path -LiteralPath $DotnetPath)) { $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot ('.acceptance\windows-background\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$previousCliHome = $env:DOTNET_CLI_HOME
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-home'
Push-Location $projectRoot
try {
    if (-not $SkipBuild) {
        & $DotnetPath build 'windows\BlueLink.TransferVerification\BlueLink.TransferVerification.csproj' -c Release --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false *> (Join-Path $OutputDirectory 'build.log')
        if ($LASTEXITCODE -ne 0) { throw "Windows build failed. See $OutputDirectory\build.log" }
    }
    $verification = Join-Path $projectRoot 'windows\BlueLink.TransferVerification\bin\Release\net8.0-windows10.0.19041.0\BlueLink.TransferVerification.dll'
    $failureLog = Join-Path $OutputDirectory 'failure-handler.log'
    & $DotnetPath $verification --failure-reporting-self-test *> $failureLog
    $failureCode = $LASTEXITCODE
    $failureText = Get-Content -LiteralPath $failureLog -Raw
    if ($failureCode -ne 1 -or $failureText -notmatch '^Verification failed:' -or $failureText -match 'Unhandled exception') {
        throw "Verification error handler failed its regression. See $failureLog"
    }
    foreach ($mode in @('background-only', 'offscreen-layout')) {
        $argument = if ($mode -eq 'offscreen-layout') { '--offscreen-layout=' + (Join-Path $OutputDirectory 'layout') } else { '--background-only' }
        & $DotnetPath $verification $argument *> (Join-Path $OutputDirectory ($mode + '.log'))
        if ($LASTEXITCODE -ne 0) { throw "Windows verification failed. See $OutputDirectory\$mode.log" }
        if (Select-String -LiteralPath (Join-Path $OutputDirectory ($mode + '.log')) -Pattern '^Unhandled exception\.|^Fatal error\.' -Quiet) {
            throw "Windows verification reported a runtime failure. See $OutputDirectory\$mode.log"
        }
        Get-Content -LiteralPath (Join-Path $OutputDirectory ($mode + '.log'))
    }
    & (Join-Path $PSScriptRoot 'verify-ui-contract.ps1') -MigrationScope Full *> (Join-Path $OutputDirectory 'ui-contract.log')
    Write-Host "Background Windows verification completed: $OutputDirectory"
} finally {
    Pop-Location
    $env:DOTNET_CLI_HOME = $previousCliHome
}
