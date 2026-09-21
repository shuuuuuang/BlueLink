param([Parameter(Mandatory=$true)][string]$MsiPath, [switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$path = (Resolve-Path -LiteralPath $MsiPath).Path
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($path, 0)
function Read-MsiRows([string]$Sql) {
    $view = $database.OpenView($Sql)
    try {
        [void]$view.Execute()
        while ($null -ne ($record = $view.Fetch())) {
            $cells = for ($index=1; $index -le $record.GetType().InvokeMember('FieldCount','GetProperty',$null,$record,$null); $index++) { $record.StringData($index) }
            Write-Output -NoEnumerate @($cells)
        }
    } finally { [void]$view.Close() }
}
function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    Write-Output "PASS: $Message"
}
try {
    $properties = @{}
    foreach ($row in (Read-MsiRows 'SELECT `Property`, `Value` FROM `Property`')) { $properties[$row[0]]=$row[1] }
    $dialogs = @(Read-MsiRows 'SELECT `Dialog` FROM `Dialog`' | ForEach-Object { $_[0] })
    foreach ($dialog in @('WelcomeDlg','InstallDirDlg','BlueLinkOptionsDlg','VerifyReadyDlg','ExitDialog','MaintenanceTypeDlg')) {
        Require ($dialogs -contains $dialog) "Native wizard contains $dialog"
    }
    Require ($properties.WIXUI_INSTALLDIR -eq 'INSTALLFOLDER') 'Directory choice targets the actual installation directory'
    Require ($properties.WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT -eq '立即运行蓝联') 'Completion offers launch choice'
    $controls = @(Read-MsiRows 'SELECT `Dialog_`, `Control`, `Property` FROM `Control`')
    Require (@($controls | Where-Object { $_[0] -eq 'BlueLinkOptionsDlg' -and $_[1] -eq 'DesktopShortcut' -and $_[2] -eq 'CREATE_DESKTOP_SHORTCUT' }).Count -eq 1) 'Desktop choice sets MSI shortcut property'
    $components = @(Read-MsiRows 'SELECT `Component`, `Condition` FROM `Component`')
    Require (@($components | Where-Object { $_[0] -eq 'DesktopShortcutComponent' -and $_[1] -eq 'CREATE_DESKTOP_SHORTCUT=1' }).Count -eq 1) 'MSI shortcut component obeys the choice'
    $events = @(Read-MsiRows 'SELECT `Dialog_`, `Control_`, `Event`, `Argument`, `Condition` FROM `ControlEvent`')
    Require (@($events | Where-Object { $_[0] -eq 'ExitDialog' -and $_[2] -eq 'DoAction' -and $_[3] -eq 'LaunchBlueLinkAfterInstall' -and $_[4] -match 'WIXUI_EXITDIALOGOPTIONALCHECKBOX=1' }).Count -eq 1) 'Launch is conditional on Finish checkbox'
    $layout = @(Read-MsiRows 'SELECT `Dialog_`, `Control`, `Width`, `Height`, `Text` FROM `Control`')
    $sidebarDialogs = @('WelcomeDlg','MaintenanceWelcomeDlg','ExitDialog','FatalError','PrepareDlg','ResumeDlg','UserExit')
    $sidebarControls = @($layout | Where-Object { $_[0] -in $sidebarDialogs -and $_[1] -eq 'Bitmap' -and $_[2] -eq '130' -and $_[4] -eq 'WixUI_Bmp_Dialog' })
    Require ($sidebarControls.Count -eq $sidebarDialogs.Count) 'Brand sidebar leaves the native body background unobscured on all seven pages'
    Require (@($layout | Where-Object { $_[0] -eq 'ExitDialog' -and $_[1] -eq 'OptionalCheckBox' -and [int]$_[3] -le 18 }).Count -eq 1) 'Launch checkbox is a compact native row without an oversized background block'
    $execute = @(Read-MsiRows 'SELECT `Action` FROM `InstallExecuteSequence`' | ForEach-Object { $_[0] })
    Require ($execute -notcontains 'LaunchBlueLinkAfterInstall') 'Quiet/Burn installation cannot launch through execute sequence'
    Require ($properties.ProductLanguage -eq '2052') 'MSI metadata selects Simplified Chinese'
    $search = @(Read-MsiRows 'SELECT `Signature_`, `Root`, `Key`, `Name` FROM `RegLocator`')
    Require (@($search | Where-Object { $_[0] -eq 'BlueLinkPreviousInstallFolder' -and $_[1] -eq '1' -and $_[3] -eq 'InstallFolder' }).Count -eq 1) 'Existing directory is read from this product channel in HKCU'
    foreach ($table in @('InstallUISequence','InstallExecuteSequence')) {
        $sequence = @(Read-MsiRows ('SELECT `Action`, `Condition`, `Sequence` FROM `' + $table + '`'))
        $restore = @($sequence | Where-Object { $_[0] -eq 'UsePreviousBlueLinkFolder' })
        $searchOrder = @($sequence | Where-Object { $_[0] -eq 'AppSearch' })
        $costOrder = @($sequence | Where-Object { $_[0] -eq 'CostInitialize' })
        Require ($restore.Count -eq 1 -and $restore[0][1] -eq 'NOT INSTALLFOLDER AND BLUELINK_PREVIOUS_INSTALLFOLDER' -and [int]$restore[0][2] -gt [int]$searchOrder[0][2] -and [int]$restore[0][2] -lt [int]$costOrder[0][2]) "$table restores discovered path before costing without overwriting explicit choices"
    }
    $expected = if ($FrameworkDependent) { '0' } else { '1' }
    Require ($properties.BLUELINK_BUNDLED_RUNTIME -eq $expected) 'Runtime metadata matches payload kind'
    if ($FrameworkDependent) {
        $conditions = @(Read-MsiRows 'SELECT `Condition` FROM `LaunchCondition`' | ForEach-Object { $_[0] })
        Require (@($conditions | Where-Object { $_ -match 'BLUELINK_DOTNET_CHECK' -and $_ -match 'REMOVE' }).Count -eq 1) 'Runtime gate also protects quiet install and permits removal'
    }
} finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}
