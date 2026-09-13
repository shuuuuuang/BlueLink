param(
    [string]$DotnetPath = '',
    [string]$OutputDirectory = '',
    [switch]$CompileInstaller,
    [ValidateSet('x86','x64','arm64','all')][string[]]$Architecture = @('x64'),
    [switch]$Offline,
    [switch]$SkipTests
)
# Keep the existing entry point while using the shared architecture/ownership/embedding gates.
$ErrorActionPreference = 'Stop'
$arguments = @{
    Architecture = $Architecture
    Format = $(if ($CompileInstaller) { 'Both' } else { 'Portable' })
    DotnetPath = $DotnetPath
    OutputDirectory = $OutputDirectory
    Offline = $Offline
    SkipTests = $SkipTests
}
& (Join-Path $PSScriptRoot 'build-windows-packages.ps1') @arguments
