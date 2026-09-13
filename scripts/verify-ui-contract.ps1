param(
    [ValidateSet('Full', 'SettingsOnly')]
    [string]$Scope = 'Full',
    [string]$RuntimeProbeReport = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Assert-Contains([string]$Path, [string]$Pattern, [string]$Description) {
    $fullPath = Join-Path $root $Path
    if (-not (Test-Path -LiteralPath $fullPath)) { throw "Missing file: $Path" }
    $content = Get-Content -LiteralPath $fullPath -Raw -Encoding UTF8
    if ($content -notmatch $Pattern) { throw "UI contract failed: $Description ($Path)" }
    Write-Host "[PASS] $Description"
}

function Assert-NotContains([string]$Path, [string]$Pattern, [string]$Description) {
    $fullPath = Join-Path $root $Path
    if (-not (Test-Path -LiteralPath $fullPath)) { throw "Missing file: $Path" }
    $content = Get-Content -LiteralPath $fullPath -Raw -Encoding UTF8
    if ($content -match $Pattern) { throw "UI contract failed: $Description ($Path)" }
    Write-Host "[PASS] $Description"
}

if ($Scope -eq 'SettingsOnly') {
    $settingsXaml = 'windows\BlueLink.App\SettingsPage.xaml'
    $settingsCode = 'windows\BlueLink.App\SettingsPage.xaml.cs'
    $settingsTheme = 'windows\BlueLink.App\Themes\SettingsWindow.xaml'
    $settingsFiles = @($settingsXaml, $settingsTheme)
    foreach ($path in @($settingsXaml, $settingsCode, $settingsTheme, 'windows\BlueLink.App\BlueLink.App.csproj')) {
        if (-not (Test-Path -LiteralPath (Join-Path $root $path))) { throw "Missing file: $path" }
    }

    $templateViolations = foreach ($path in $settingsFiles) {
        Select-String -LiteralPath (Join-Path $root $path) -Pattern '<ControlTemplate\b|ControlTemplate\s*=' -AllMatches
    }
    if ($templateViolations) {
        $details = ($templateViolations | ForEach-Object { "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }) -join [Environment]::NewLine
        throw "Hand-written base ControlTemplate is forbidden in settings XAML:`n$details"
    }
    Write-Host '[PASS] settings XAML contains zero hand-written ControlTemplate declarations'

    Assert-Contains 'windows\BlueLink.App\BlueLink.App.csproj' '<PackageReference Include="WPF-UI" Version="4\.3\.0"' 'Windows client pins WPF-UI 4.3.0'
    Assert-Contains 'windows\BlueLink.App\BlueLink.App.csproj' '<BlueLinkWpfUiMigrationScope>SettingsOnly</BlueLinkWpfUiMigrationScope>' 'Windows client declares the frozen SettingsOnly migration scope'
    Assert-Contains $settingsXaml '^<ui:FluentWindow\b' 'Settings uses WPF UI FluentWindow'
    Assert-Contains $settingsXaml '<ui:TitleBar\b' 'Settings uses the official WPF UI TitleBar'
    Assert-Contains $settingsXaml '<ui:NavigationView\b' 'Settings uses WPF UI NavigationView'
    Assert-Contains $settingsXaml '<ui:NavigationViewItem\b' 'Settings uses official WPF UI navigation items'
    Assert-NotContains $settingsXaml '<Tab(Control|Item)\b|<ui:TabView\b' 'Settings does not regress to top tabs'
    Assert-NotContains $settingsXaml '<ui:InfoBar\b' 'Settings has no permanent status banner'
    Assert-Contains $settingsCode 'ShowToast\(' 'Settings uses the shared transient feedback host'
    foreach ($page in @('general', 'connection', 'files', 'privacy', 'about')) {
        Assert-Contains $settingsXaml ('TargetPageTag="' + $page + '"') "Settings exposes the $page navigation page"
    }
    Assert-NotContains $settingsTheme '<Style(?=[^>]*TargetType)(?![^>]*x:Key)' 'Settings theme contains no implicit base-control Style'
    Assert-NotContains $settingsCode 'System\.Windows\.MessageBox|MessageBox\.Show\s*\(' 'Settings code contains no native/system MessageBox calls'

    if (-not [string]::IsNullOrWhiteSpace($RuntimeProbeReport)) {
        throw 'SettingsOnly runtime verification is performed by scripts\test-settings-ui.ps1; a global control-template report is not accepted for this scope.'
    }
    Write-Host 'WPF UI SettingsOnly contract verification passed.'
    return
}

# Figma is the design source of truth; validate production UI contracts below.

$productXamlRoots = @(
    'windows\BlueLink.App',
    'installer\BlueLink.SetupUI',
    'installer\BlueLink.Launcher',
    'installer\BlueLink.Uninstall'
)
$productXaml = foreach ($relativeRoot in $productXamlRoots) {
    Get-ChildItem -LiteralPath (Join-Path $root $relativeRoot) -Filter '*.xaml' -File -Recurse |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Extension -in '.cs', '.xaml' }
}
$templateViolations = $productXaml | Select-String -Pattern '<ControlTemplate\b|ControlTemplate\s*=' -AllMatches
if ($templateViolations) {
    $details = ($templateViolations | ForEach-Object { "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }) -join [Environment]::NewLine
    throw "Hand-written base ControlTemplate is forbidden in product XAML:`n$details"
}
Write-Host '[PASS] product XAML contains zero hand-written ControlTemplate declarations'

$removedControls = Join-Path $root 'windows\BlueLink.App\Themes\Controls.xaml'
if (Test-Path -LiteralPath $removedControls) { throw 'Themes\Controls.xaml must be deleted, not renamed or retained.' }
Write-Host '[PASS] legacy Themes\Controls.xaml is absent'

Assert-Contains 'windows\BlueLink.App\BlueLink.App.csproj' '<PackageReference Include="WPF-UI" Version="4\.3\.0"' 'Windows client pins WPF-UI 4.3.0'
Assert-Contains 'windows\BlueLink.App\App.xaml' '<ui:ThemesDictionary Theme="Light"\s*/>' 'Windows client loads official WPF UI theme dictionary'
Assert-Contains 'windows\BlueLink.App\App.xaml' '<ui:ControlsDictionary\s*/>' 'Windows client loads official WPF UI controls dictionary'
Assert-Contains 'windows\BlueLink.App\App.xaml' 'Themes/Components\.xaml' 'Windows client loads business composition styles'
Assert-NotContains 'windows\BlueLink.App\App.xaml' 'Themes/Controls\.xaml' 'Windows client no longer loads legacy control templates'

foreach ($project in @(
    'installer\BlueLink.SetupUI\BlueLink.SetupUI.csproj',
    'installer\BlueLink.Launcher\BlueLink.Launcher.csproj',
    'installer\BlueLink.Uninstall\BlueLink.Uninstall.csproj'
)) {
    Assert-Contains $project '<PackageReference Include="WPF-UI" Version="4\.3\.0"' "$project pins WPF-UI 4.3.0"
}

foreach ($window in @(
    'windows\BlueLink.App\MainWindow.xaml',
    'windows\BlueLink.App\ImagePreviewWindow.xaml',
    'windows\BlueLink.App\MessageSearchWindow.xaml'
)) {
    Assert-Contains $window '^<ui:FluentWindow\b' "$window uses WPF UI FluentWindow"
    Assert-Contains $window '<ui:TitleBar\b' "$window uses WPF UI TitleBar"
}

Assert-Contains 'windows\BlueLink.App\SettingsPage.xaml' '^<UserControl\b' 'Settings is a page inside the main window'
Assert-Contains 'windows\BlueLink.App\MainWindow.xaml' 'x:Name="SettingsHost"' 'Main window hosts settings content'
Assert-NotContains 'windows\BlueLink.App\SettingsPage.xaml' '<ui:TitleBar\b|<ui:FluentWindow\b' 'Settings shares the main title bar and geometry'
Assert-Contains 'windows\BlueLink.App\SettingsPage.xaml' '<ui:NavigationView\b' 'Settings uses WPF UI NavigationView'
Assert-Contains 'windows\BlueLink.App\SettingsPage.xaml' '<ui:NavigationViewItem\b' 'Settings uses official WPF UI navigation items'
Assert-NotContains 'windows\BlueLink.App\SettingsPage.xaml' '<Tab(Control|Item)\b|<ui:TabView\b' 'Settings does not regress to top tabs'
Assert-NotContains 'windows\BlueLink.App\SettingsPage.xaml' '<ui:InfoBar\b' 'Settings has no permanent status banner'
Assert-Contains 'windows\BlueLink.App\SettingsPage.xaml.cs' 'ShowToast\(' 'Settings uses the shared transient feedback host'

Assert-Contains 'windows\BlueLink.App\MainWindow.xaml' 'AllowDrop="True"' 'Windows chat accepts Explorer file drops'
Assert-Contains 'windows\BlueLink.App\MainWindow.xaml' 'PreviewMouseMove="Attachment_PreviewMouseMove"' 'Windows attachments support drag-out'
Assert-Contains 'windows\BlueLink.App\MainWindow.xaml' 'AttachmentDeleteMenu_Click' 'Chat/file context menus include record deletion'
Assert-NotContains 'windows\BlueLink.App\MainWindow.xaml' 'TransferColumn|TransferGapColumn|CollapsedTransferRail' 'Home removes the legacy third-column transfer rail'
Assert-Contains 'windows\BlueLink.App\MainWindow.xaml' 'ShowConversationPlaceholder' 'Home exposes the no-conversation state'
Assert-Contains 'windows\BlueLink.App\MainWindow.Home.cs' 'ShowGlobalFiles' 'Home exposes global files independently of a conversation'
Assert-Contains 'windows\BlueLink.App\MainWindow.xaml' 'x:Name="FileDeviceFilter"' 'File workspace exposes real device scope filtering'
Assert-Contains 'windows\BlueLink.App\MainWindow.xaml' 'x:Name="FileSearchInput"' 'File workspace exposes filename search'
Assert-NotContains 'windows\BlueLink.App\MainWindow.xaml' 'ClearCompletedTransfers_Click' 'Transfer panel omits clear-completed action'
Assert-Contains 'windows\BlueLink.App\MainWindow.xaml' 'x:Name="MessageList"[\s\S]*BasedOn="\{StaticResource TransparentListItemStyle\}"[\s\S]*Margin" Value="0"' 'Chat items do not reserve an unconditional scrollbar gutter'
Assert-Contains 'windows\BlueLink.App\MainViewModel.cs' 'ComposerPlaceholder\s*=>' 'Offline subtitle behavior is projected by the view model'

Assert-Contains 'windows\BlueLink.App\ImagePreviewWindow.xaml' 'Fit_Click' 'Image preview retains fit-to-window'
Assert-Contains 'windows\BlueLink.App\ImagePreviewWindow.xaml' 'Content="1:1"' 'Image preview retains actual-size action'
Assert-Contains 'windows\BlueLink.App\ImagePreviewWindow.xaml' 'RotateLeft_Click' 'Image preview retains left rotation'
Assert-Contains 'windows\BlueLink.App\ImagePreviewWindow.xaml' 'RotateRight_Click' 'Image preview retains right rotation'
Assert-NotContains 'windows\BlueLink.App\ImagePreviewWindow.xaml' 'Locate_Click|SaveAs_Click|PreviewFileActions|Esc 关闭' 'Image preview omits duplicate file actions and Escape hint'
Assert-NotContains 'windows\BlueLink.App\ImagePreviewWindow.xaml' 'OpenOriginal' 'Image preview omits open-original action'
Assert-Contains 'windows\BlueLink.App\ImagePreviewWindow.xaml' 'x:Name="TitleBarFileName"' 'Image preview title bar hosts the actual file name'
Assert-NotContains 'windows\BlueLink.App\ImagePreviewWindow.xaml' 'FileDetailText|ToggleMaximize_Click|FullscreenButton' 'Image preview omits metadata row and custom fullscreen action'
Assert-Contains 'windows\BlueLink.App\ImagePreviewWindow.xaml' 'x:Name="ImageSurface"' 'Image preview uses an unconstrained transform surface'
Assert-Contains 'windows\BlueLink.App\ImagePreviewWindow.xaml' 'x:Name="ImageTransform"' 'Image preview uses one testable transform matrix'

$allProductSource = Get-ChildItem -LiteralPath (Join-Path $root 'windows\BlueLink.App'), (Join-Path $root 'installer\BlueLink.SetupUI'), (Join-Path $root 'installer\BlueLink.Launcher'), (Join-Path $root 'installer\BlueLink.Uninstall'), (Join-Path $root 'installer\SharedUI') -File -Recurse |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Extension -in '.cs', '.xaml' }
$nativeDialogViolations = $allProductSource | Select-String -Pattern '\bMessageBox\b' -AllMatches
if ($nativeDialogViolations) { throw 'Default MessageBox type reference remains in product source. Use the shared dialog shell.' }
Write-Host '[PASS] product source contains no native/system MessageBox calls'

Assert-Contains 'windows\BlueLink.App\BlueLinkDialog.xaml.cs' 'ConfirmationWindow' 'Windows prompts share the reviewed confirmation shell'
Assert-NotContains 'windows\BlueLink.App\BlueLinkDialog.xaml.cs' 'new Wpf\.Ui\.Controls\.MessageBox' 'Windows prompts do not recreate the inconsistent default MessageBox'
Assert-Contains 'windows\BlueLink.App\BlueLinkDialog.xaml.cs' 'dialog\.ShowDialog\(\)' 'Windows prompts use the visible modal workflow'
Assert-Contains 'windows\BlueLink.App\BlueLinkDialog.xaml.cs' 'using var overlay' 'Windows prompts always dispose the owner overlay'
Assert-Contains 'installer\BlueLink.SetupUI\InstallerPromptWindow.xaml.cs' 'new InstallerDialogWindow' 'Overwrite prompt uses the shared installer dialog shell'
Assert-NotContains 'installer\BlueLink.SetupUI\InstallerPromptWindow.xaml.cs' 'TestableMessageBox|InvokeCloseButton' 'Overwrite UI test has no derived fake or internal close shortcut'
Assert-Contains 'windows\BlueLink.App\TrustConfirmationWindow.xaml' '<ui:FluentWindow' 'Trust confirmation composes official WPF UI controls'
Assert-Contains 'windows\BlueLink.App\TrustConfirmationWindow.xaml.cs' '_request\.Confirm\(\)' 'Trust confirmation invokes the live handshake decision'

Assert-Contains 'windows\BlueLink.App\Themes\Components.xaml' 'FocusVisualStyle" Value="\{x:Null\}"' 'Composition styles suppress default focus adorners'
Assert-Contains 'windows\BlueLink.App\Themes\Components.xaml' 'MaxDropDownHeight" Value="320"' 'ComboBox dropdown height is bounded'
Assert-Contains 'windows\BlueLink.App\Themes\Components.xaml' 'Height" Value="{StaticResource ClientControlHeight}"' 'Common input and action height uses compact density token'

if (-not [string]::IsNullOrWhiteSpace($RuntimeProbeReport)) {
    $resolvedProbe = [IO.Path]::GetFullPath($RuntimeProbeReport)
    if (-not (Test-Path -LiteralPath $resolvedProbe)) { throw "Runtime probe report missing: $resolvedProbe" }
    $probe = Get-Content -LiteralPath $resolvedProbe -Raw | ConvertFrom-Json
    $failed = @($probe.controls | Where-Object { -not $_.StyleResolved -or -not $_.TemplateResolved -or -not $_.TemplateApplied -or $_.VisualChildren -lt 1 -or -not $_.FocusVisualSuppressed })
    if ($failed.Count -gt 0) {
        throw "Runtime WPF UI template probe failed: $($failed.Name -join ', ')"
    }
    Write-Host "[PASS] runtime style/template probe passed for $($probe.controls.Count) critical controls"
}

Write-Host 'WPF UI contract verification passed.'
