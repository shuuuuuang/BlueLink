param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-zA-Z0-9_-]{1,48}$')][string]$Name,
    [ValidateSet('message-arrival','tray-preview','usb-off','usb-waiting','usb-authorization','usb-permissiondenied','usb-negotiating','usb-highspeed','usb-fullspeed','usb-fallback','usb-unavailable','usb-drivermissing','usb-policyblocked','usb-unsupported','update-checking','update-check-failed','update-available','update-downloading','update-download-failed','update-ready','help-connection','help-messages','help-faq','feedback-empty','feedback-filled-off','feedback-filled-on','feedback-validation','feedback-generating-off','feedback-generating-on','feedback-failure-off','feedback-failure-on','feedback-ready-off','feedback-ready-on','settings-trusted','security-confirm','security-waiting','security-rejected','security-timeout','security-identity-changed','toast-info','toast-success','toast-warning','toast-error','toast-stacked','first-use','zero-connected','bluetooth-off','connected','connected-empty','offline-empty','nearby-empty',
        'refresh-scanning','refresh-complete','refresh-interactive','connect-loading','connect-failed','connect-success','message-empty','message-statuses',
        'drop-send','drop-blocked','files-device-transfer','files-device-transfer-paused','files-current','files-global','files-offline','files-bluetooth-off','message-history','search-results','search-empty',
        'files-menu-waiting','files-menu-sending','files-menu-send-paused','files-menu-receiving','files-menu-receive-paused','files-menu-send-failed','files-menu-receive-failed','files-menu-send-complete','files-menu-receive-complete','message-menu-waiting','message-menu-sending','message-menu-send-paused','message-menu-receiving','message-menu-receive-paused','message-menu-send-failed','message-menu-receive-failed','message-menu-send-complete','message-menu-receive-complete','image-menu-sending','image-menu-receiving','image-menu-send-complete','image-menu-receive-complete')]
    [string]$Scene = 'connected'
)
$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path -Parent $PSScriptRoot
$taskExecutable = Join-Path $taskWorkspace 'windows/BlueLink.App/bin/Release/net8.0-windows10.0.19041.0/BlueLink.exe'
if (!(Test-Path -LiteralPath $taskExecutable)) { throw 'Build the Windows Release application first.' }
if (Get-Process -Name BlueLink -ErrorAction SilentlyContinue) { throw 'A BlueLink process is still running. Close the previous QA instance first.' }
$env:DOTNET_ROOT = 'D:\Tool\dotnet-sdk-8'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
# The user explicitly requested an interactive desktop acceptance window.
$taskProcess = Start-Process -FilePath $taskExecutable -WorkingDirectory $taskWorkspace -PassThru `
    -ArgumentList "--desktop-ui-test=$Name", "--desktop-ui-scene=$Scene"
[pscustomobject]@{ ProcessId = $taskProcess.Id; Name = $Name; Scene = $Scene; DataRoot = (Join-Path $env:TEMP "BlueLinkDesktopUI-$Name") }
