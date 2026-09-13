param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('welcome','location','invalid-path','low-space','progress','failure','complete','cancel','running','runtime-required','runtime-failure','runtime-downloading','runtime-verifying','runtime-elevation','runtime-completed','uninstall-keep','uninstall-delete','uninstall-confirm-keep','uninstall-confirm-delete','uninstall-progress','uninstall-complete-keep','uninstall-complete-delete','uninstall-failure')]
    [string]$Scene
)
$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
$taskExecutable = Join-Path $taskWorkspace 'installer/BlueLink.Installation.VisualTests/bin/Release/net472/win-x64/BlueLink.Installation.VisualTests.exe'
if (-not (Test-Path -LiteralPath $taskExecutable)) { throw 'Build the installer visual verification project first.' }
if (Get-Process -Name BlueLink.Installation.VisualTests -ErrorAction SilentlyContinue) { throw 'Close the previous installer UI acceptance instance first.' }
$taskOutput = Join-Path $taskWorkspace '.acceptance/prototype-cua/20260906/installer-profile'
# Explicit native UI acceptance; this executable never creates a Burn engine or uninstall executor.
$taskProcess = Start-Process -FilePath $taskExecutable -WorkingDirectory $taskWorkspace -PassThru `
    -ArgumentList "--desktop-ui-scene=$Scene", ('"' + $taskOutput + '"')
[pscustomobject]@{ ProcessId = $taskProcess.Id; Scene = $Scene; Output = $taskOutput }
