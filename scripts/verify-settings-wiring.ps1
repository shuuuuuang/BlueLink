param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
function Require-Text([string]$RelativePath, [string[]]$Patterns) {
    $content = Get-Content -LiteralPath (Join-Path $ProjectRoot $RelativePath) -Raw
    foreach ($pattern in $Patterns) {
        if (-not $content.Contains($pattern)) { throw "设置接线缺失：$RelativePath -> $pattern" }
    }
}

Require-Text 'windows\BlueLink.App\MainViewModel.cs' @(
    'SessionLog.Enabled = Settings.DiagnosticsEnabled',
    'Settings.SaveChatHistory',
    'Settings.SaveTransferHistory'
)
Require-Text 'windows\BlueLink.App\MainWindow.xaml' @(
    'Settings.ShowImageThumbnails',
    '<MultiDataTrigger>'
)
Require-Text 'android\app\src\main\java\com\bluelink\android\runtime\BlueLinkRuntime.kt' @(
    'if (appSettings.saveChatHistory)',
    'if (appSettings.saveTransferHistory)',
    'if (!appSettings.diagnosticsEnabled',
    'if (appSettings.keepBackgroundSessions) return',
    'sessionSupervisor.disconnectAll'
)
Require-Text 'android\app\src\main\java\com\bluelink\android\MainActivity.kt' @(
    'showImageThumbnails = settings.showImageThumbnails',
    'runtime.onAppBackgrounded()'
)
Require-Text 'android\app\src\main\java\com\bluelink\android\service\BluetoothSessionService.kt' @(
    'START_NOT_STICKY',
    'ic_notification_bluelink'
)

Write-Host 'Settings runtime wiring verified: Windows 4 policies, Android 5 policies.'
