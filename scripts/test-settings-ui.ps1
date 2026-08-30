param(
    [string]$ExecutablePath,
    [string]$ArtifactRoot
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $root 'windows\BlueLink.App\bin\Debug\net8.0-windows10.0.19041.0\BlueLink.exe'
}
$ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $ExecutablePath)) {
    throw "Settings UI test executable was not found: $ExecutablePath"
}
if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    $ArtifactRoot = Join-Path $root ('artifacts\qa\settings-v1\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$ArtifactRoot = [IO.Path]::GetFullPath($ArtifactRoot)
New-Item -ItemType Directory -Path $ArtifactRoot -Force | Out-Null

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Assert-SourceContract {
    $settingsXaml = Join-Path $root 'windows\BlueLink.App\SettingsWindow.xaml'
    $settingsCode = Join-Path $root 'windows\BlueLink.App\SettingsWindow.xaml.cs'
    $settingsTheme = Join-Path $root 'windows\BlueLink.App\Themes\SettingsWindow.xaml'
    $appXaml = Join-Path $root 'windows\BlueLink.App\App.xaml'
    $componentsTheme = Join-Path $root 'windows\BlueLink.App\Themes\Components.xaml'
    $previewXaml = Join-Path $root 'windows\BlueLink.App\ImagePreviewWindow.xaml'
    $project = Join-Path $root 'windows\BlueLink.App\BlueLink.App.csproj'
    foreach ($path in @($settingsXaml, $settingsCode, $settingsTheme, $appXaml, $componentsTheme, $previewXaml, $project)) {
        Assert-True (Test-Path -LiteralPath $path) "Required settings source is missing: $path"
    }

    $xaml = Get-Content -LiteralPath $settingsXaml -Raw -Encoding UTF8
    $theme = Get-Content -LiteralPath $settingsTheme -Raw -Encoding UTF8
    $app = Get-Content -LiteralPath $appXaml -Raw -Encoding UTF8
    $components = Get-Content -LiteralPath $componentsTheme -Raw -Encoding UTF8
    $preview = Get-Content -LiteralPath $previewXaml -Raw -Encoding UTF8
    $projectText = Get-Content -LiteralPath $project -Raw -Encoding UTF8
    [xml]$xaml | Out-Null
    [xml]$theme | Out-Null
    Assert-True ($xaml -match '^<ui:FluentWindow\b') 'SettingsWindow must use WPF UI FluentWindow.'
    Assert-True ($xaml -match '<ui:TitleBar\b') 'SettingsWindow must expose the official WPF UI TitleBar.'
    Assert-True ($xaml -match '<ui:NavigationView\b') 'SettingsWindow must expose the official WPF UI NavigationView.'
    Assert-True ($xaml -match 'x:Name="SaveInfoBar"') 'SettingsWindow must expose non-blocking save feedback.'
    foreach ($page in @('general', 'connection', 'files', 'privacy', 'about')) {
        Assert-True ($xaml -match ('TargetPageTag="' + $page + '"')) "Missing settings navigation page: $page"
    }
    Assert-True ($xaml -notmatch '<\s*ControlTemplate\b') 'SettingsWindow contains a forbidden ControlTemplate.'
    Assert-True ($theme -notmatch '<\s*ControlTemplate\b') 'SettingsWindow theme contains a forbidden ControlTemplate.'
    Assert-True ($theme -notmatch '<Style(?=[^>]*TargetType)(?![^>]*x:Key)') 'SettingsWindow theme contains a forbidden implicit base-control Style.'
    Assert-True ($app -match '<ui:ThemesDictionary Theme="Light"\s*/>') 'App must load the WPF UI theme dictionary globally.'
    Assert-True ($app -match '<ui:ControlsDictionary\s*/>') 'App must load the WPF UI controls dictionary globally.'
    Assert-True ($theme -notmatch '<ui:(ThemesDictionary|ControlsDictionary)') 'Settings must not shadow WPF UI dictionaries in window scope.'
    Assert-True ($theme -match 'BasedOn="\{StaticResource DefaultUiToggleSwitchStyle\}"') 'ToggleSwitch must inherit the official named WPF UI style.'
    Assert-True ($theme -match 'BasedOn="\{StaticResource DefaultComboBoxStyle\}"') 'ComboBox must inherit the official named WPF UI style.'
    Assert-True ($theme -match 'VerticalContentAlignment" Value="Center"') 'ComboBox content must be vertically centered.'
    Assert-True ($components -notmatch 'BlueLinkScrollThumbStyle|BlueLinkScrollRepeatStyle') 'Legacy hand-written ScrollBar helper styles must be absent.'
    Assert-True ($components -match '<Style TargetType="ScrollBar" BasedOn="\{StaticResource \{x:Type ScrollBar\}\}"') 'ScrollBar composition must inherit the official WPF UI style.'
    Assert-True ($components -notmatch '<\s*ControlTemplate\b') 'Business component styles must not contain a hand-written base ControlTemplate.'
    Assert-True ($preview -notmatch '<ScrollViewer\b|#101624|#0D1320') 'Image preview must not use black ScrollViewer-based navigation.'
    Assert-True ($preview -match 'x:Name="Navigator"' -and $preview -match 'x:Name="NavigatorCrop"') 'Image preview must expose its navigator and visible crop rectangle.'
    Assert-True ($preview -notmatch '<\s*ControlTemplate\b') 'Image preview must not contain a hand-written base ControlTemplate.'
    Assert-True ($projectText -match '<PackageReference Include="WPF-UI" Version="4\.3\.0"') 'BlueLink.App must pin WPF-UI 4.3.0.'

    $allowedWindowsClientSources = @(
        'windows/BlueLink.App/BlueLink.App.csproj',
        'windows/BlueLink.App/App.xaml',
        'windows/BlueLink.App/App.xaml.cs',
        'windows/BlueLink.App/AllTransfersWindow.xaml',
        'windows/BlueLink.App/AllTransfersWindow.xaml.cs',
        'windows/BlueLink.App/BlueLinkDialog.xaml',
        'windows/BlueLink.App/BlueLinkDialog.xaml.cs',
        'windows/BlueLink.App/ImagePreviewViewportMath.cs',
        'windows/BlueLink.App/ImagePreviewWindow.xaml',
        'windows/BlueLink.App/ImagePreviewWindow.xaml.cs',
        'windows/BlueLink.App/ShellFileLocator.cs',
        'windows/BlueLink.App/SettingsWindow.xaml',
        'windows/BlueLink.App/SettingsWindow.xaml.cs',
        'windows/BlueLink.App/MainWindow.xaml',
        'windows/BlueLink.App/MainWindow.xaml.cs',
        'windows/BlueLink.App/Themes/Components.xaml',
        'windows/BlueLink.App/Themes/Controls.xaml',
        'windows/BlueLink.App/Themes/SettingsWindow.xaml',
        'windows/BlueLink.App/TrustConfirmationWindow.xaml',
        'windows/BlueLink.App/TrustConfirmationWindow.xaml.cs'
    )
    $status = & git -C $root status --porcelain=v1 --untracked-files=all
    foreach ($line in $status) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $path = $line.Substring(3).Trim('"').Replace('\', '/')
        if ($path -notlike 'windows/BlueLink.App/*') { continue }
        Assert-True ($allowedWindowsClientSources -contains $path) "Settings implementation changed an out-of-scope Windows client source: $path"
    }
}

Assert-SourceContract

function Get-ControlEventName([string]$executable, [string]$suffix) {
    $installFolder = Split-Path -Parent $executable
    if ((Split-Path -Leaf $installFolder).Equals('app', [StringComparison]::OrdinalIgnoreCase)) {
        $installFolder = Split-Path -Parent $installFolder
    }
    $normalized = [IO.Path]::GetFullPath($installFolder).TrimEnd('\', '/').ToUpperInvariant()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($normalized)) }
    finally { $sha.Dispose() }
    $locationKey = -join ($hash[0..11] | ForEach-Object { $_.ToString('X2') })
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    "Local\BlueLink.Desktop.Control.$sid.$locationKey.$suffix"
}

function Signal-ControlEvent([string]$eventName, [int]$timeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    do {
        try {
            $signal = [Threading.EventWaitHandle]::OpenExisting($eventName)
            try { return $signal.Set() }
            finally { $signal.Dispose() }
        }
        catch [Threading.WaitHandleCannotBeOpenedException] {
            Start-Sleep -Milliseconds 150
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for the BlueLink control event: $eventName"
}

if (-not ('BlueLinkSettingsUiNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class BlueLinkSettingsUiNative
{
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder value, int maximumCharacters);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    public static void ClickAt(int x, int y)
    {
        SetCursorPos(x, y);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }

    public static void MoveTo(int x, int y)
    {
        SetCursorPos(x, y);
    }

    public static void WheelAt(int x, int y, int delta)
    {
        SetCursorPos(x, y);
        mouse_event(0x0800, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
    }

    public static IntPtr FindTopLevelWindow(int requestedProcessId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, state) =>
        {
            uint processId;
            GetWindowThreadProcessId(hwnd, out processId);
            if (processId != requestedProcessId) return true;
            var title = new StringBuilder(256);
            GetWindowText(hwnd, title, title.Capacity);
            if (!String.Equals(title.ToString(), "蓝联 / BlueLink", StringComparison.Ordinal)) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
'@
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

function Wait-WindowHandle([int]$processId, [int]$timeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    do {
        $handle = [BlueLinkSettingsUiNative]::FindTopLevelWindow($processId)
        if ($handle -ne [IntPtr]::Zero) { return $handle }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Timed out waiting for the isolated BlueLink top-level window.'
}

function Find-ByName($rootElement, [string]$name) {
    $condition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::NameProperty, $name)
    $rootElement.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-AllByName($rootElement, [string]$name) {
    $condition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::NameProperty, $name)
    $rootElement.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-WindowByNameAndProcess([string]$name, [int]$processId) {
    $condition = New-Object Windows.Automation.AndCondition(
        (New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Window)),
        (New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::NameProperty, $name)),
        (New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)))
    [Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [Windows.Automation.TreeScope]::Children, $condition)
}

function Find-SettingsWindowByProcess([int]$processId, $ownerWindow) {
    $condition = New-Object Windows.Automation.AndCondition(
        (New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::ControlTypeProperty,
            [Windows.Automation.ControlType]::Window)),
        (New-Object Windows.Automation.PropertyCondition(
            [Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)))
    $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children, $condition)
    foreach ($window in $windows) {
        if ($window.Current.Name -ne '蓝联 / BlueLink') { return $window }
    }
    if ($null -ne $ownerWindow) {
        $ownedWindows = $ownerWindow.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
        foreach ($window in $ownedWindows) {
            if ($window.Current.Name -ne '蓝联 / BlueLink') { return $window }
        }
    }
    return $null
}

function Wait-Element([scriptblock]$resolve, [int]$timeoutSeconds, [string]$description) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    do {
        $element = & $resolve
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "UI Automation timed out waiting for $description."
}

function Find-ActionableByName($rootElement, [string]$name) {
    $items = Find-AllByName $rootElement $name
    foreach ($item in $items) {
        try {
            $null = $item.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
            return $item
        } catch { }
        try {
            $null = $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)
            return $item
        } catch { }
    }
    foreach ($item in $items) {
        $candidate = $item
        for ($depth = 0; $depth -lt 6 -and $null -ne $candidate; $depth++) {
            $candidate = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($candidate)
            if ($null -eq $candidate) { break }
            try {
                $null = $candidate.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
                return $candidate
            } catch { }
            try {
                $null = $candidate.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)
                return $candidate
            } catch { }
        }
    }
    $visible = @($items | Where-Object {
        -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0 -and $_.Current.BoundingRectangle.Height -gt 0
    } | Sort-Object { $_.Current.BoundingRectangle.Left })
    if ($visible.Count -gt 0) { return $visible[0] }
    return $null
}

function Find-ToggleByName($rootElement, [string]$name) {
    $items = Find-AllByName $rootElement $name
    foreach ($item in $items) {
        try {
            $null = $item.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
            return $item
        } catch { }
    }
    return $null
}

function Find-VerticalScrollBar($rootElement) {
    $condition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ControlTypeProperty,
        [Windows.Automation.ControlType]::ScrollBar)
    $items = $rootElement.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)
    foreach ($item in $items) {
        $bounds = $item.Current.BoundingRectangle
        if ($item.Current.IsOffscreen -or $bounds.Height -le $bounds.Width -or $bounds.Height -lt 80) { continue }
        try {
            $pattern = [Windows.Automation.RangeValuePattern]$item.GetCurrentPattern(
                [Windows.Automation.RangeValuePattern]::Pattern)
            if ($pattern.Current.Maximum -gt $pattern.Current.Minimum) { return $item }
        } catch { }
    }
    return $null
}

function Invoke-Element($element, [string]$description) {
    if ($null -eq $element) { throw "UI Automation did not find $description." }
    try {
        $pattern = $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
        ([Windows.Automation.InvokePattern]$pattern).Invoke()
        return
    } catch { }
    try {
        $pattern = $element.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)
        ([Windows.Automation.SelectionItemPattern]$pattern).Select()
        return
    } catch { }
    $bounds = $element.Current.BoundingRectangle
    if ($bounds.Width -gt 0 -and $bounds.Height -gt 0 -and -not $element.Current.IsOffscreen) {
        [BlueLinkSettingsUiNative]::ClickAt(
            [int][Math]::Round($bounds.Left + $bounds.Width / 2),
            [int][Math]::Round($bounds.Top + $bounds.Height / 2))
        return
    }
    throw "$description exposes no invokable pattern or visible click bounds."
}

function Assert-VisibleElement($window, [string]$name) {
    $element = Wait-Element { Find-ByName $window $name } 8 "visible element '$name'"
    Assert-True (-not $element.Current.IsOffscreen) "Settings element is off-screen: $name"
    $bounds = $element.Current.BoundingRectangle
    Assert-True ($bounds.Width -gt 0 -and $bounds.Height -gt 0) "Settings element has empty bounds: $name"
    $element
}

function Capture-Window($window, [string]$path) {
    $bounds = $window.Current.BoundingRectangle
    $left = [int][Math]::Floor($bounds.Left)
    $top = [int][Math]::Floor($bounds.Top)
    $width = [Math]::Max(1, [int][Math]::Ceiling($bounds.Width))
    $height = [Math]::Max(1, [int][Math]::Ceiling($bounds.Height))
    $bitmap = New-Object Drawing.Bitmap($width, $height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($left, $top, 0, 0, $bitmap.Size)
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Capture-ControlVisual($element, [string]$path, [string]$description) {
    $bounds = $element.Current.BoundingRectangle
    $left = [int][Math]::Floor($bounds.Left)
    $top = [int][Math]::Floor($bounds.Top)
    $width = [Math]::Max(1, [int][Math]::Ceiling($bounds.Width))
    $height = [Math]::Max(1, [int][Math]::Ceiling($bounds.Height))
    Assert-True ($width -ge 32 -and $height -ge 16) "$description has collapsed bounds: ${width}x${height}"
    $bitmap = New-Object Drawing.Bitmap($width, $height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($left, $top, 0, 0, $bitmap.Size)
        $minimum = 255
        $maximum = 0
        for ($x = 0; $x -lt $width; $x++) {
            for ($y = 0; $y -lt $height; $y++) {
                $color = $bitmap.GetPixel($x, $y)
                $minimum = [Math]::Min($minimum, [Math]::Min($color.R, [Math]::Min($color.G, $color.B)))
                $maximum = [Math]::Max($maximum, [Math]::Max($color.R, [Math]::Max($color.G, $color.B)))
            }
        }
        Assert-True (($maximum - $minimum) -ge 24) "$description became visually flat or transparent."
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Open-Settings($mainWindow, [int]$processId) {
    $button = Wait-Element { Find-ActionableByName $mainWindow '设置' } 10 'the real main-window Settings button'
    Invoke-Element $button 'the real main-window Settings button'
    Wait-Element { Find-SettingsWindowByProcess $processId $mainWindow } 15 'the BlueLink Settings window'
}

function Select-SettingsPage($settingsWindow, [string]$navigationName, [string]$visibleControlName) {
    $item = Wait-Element { Find-ActionableByName $settingsWindow $navigationName } 8 "navigation item '$navigationName'"
    Invoke-Element $item "navigation item '$navigationName'"
    Start-Sleep -Milliseconds 250
    $null = Assert-VisibleElement $settingsWindow $visibleControlName
}

$process = $null
$isolatedDataRoot = $null
$exitEventName = $null
try {
    $showEventName = Get-ControlEventName $ExecutablePath 'Show'
    $exitEventName = Get-ControlEventName $ExecutablePath 'Exit'
    $process = Start-Process -FilePath $ExecutablePath -ArgumentList '--control-channel-smoke-test' -PassThru
    $isolatedDataRoot = Join-Path ([IO.Path]::GetTempPath()) ("BlueLinkStartupSmoke-{0}" -f $process.Id)
    $null = Signal-ControlEvent $showEventName 20
    $mainHandle = Wait-WindowHandle $process.Id 20
    [BlueLinkSettingsUiNative]::SetForegroundWindow($mainHandle) | Out-Null
    Start-Sleep -Milliseconds 500

    $mainWindow = [Windows.Automation.AutomationElement]::FromHandle($mainHandle)
    Assert-True ($null -ne $mainWindow) 'UI Automation could not attach to the isolated BlueLink main window.'
    $settingsWindow = Open-Settings $mainWindow $process.Id

    $footerNames = @('恢复默认值', '取消设置', '保存设置')
    foreach ($footerName in $footerNames) { $null = Assert-VisibleElement $settingsWindow $footerName }

    Select-SettingsPage $settingsWindow '通用' '关闭主窗口时'
    $startupToggle = Wait-Element { Find-ToggleByName $settingsWindow '启动时扫描附近设备' } 8 'the startup scan ToggleSwitch'
    $windowBounds = $settingsWindow.Current.BoundingRectangle
    [BlueLinkSettingsUiNative]::MoveTo(
        [int][Math]::Round($windowBounds.Left + 20),
        [int][Math]::Round($windowBounds.Top + 20))
    Start-Sleep -Milliseconds 250
    Capture-ControlVisual $startupToggle (Join-Path $ArtifactRoot 'toggle-normal.png') 'ToggleSwitch normal state'
    $toggleBounds = $startupToggle.Current.BoundingRectangle
    [BlueLinkSettingsUiNative]::MoveTo(
        [int][Math]::Round($toggleBounds.Left + $toggleBounds.Width / 2),
        [int][Math]::Round($toggleBounds.Top + $toggleBounds.Height / 2))
    Start-Sleep -Milliseconds 450
    Capture-ControlVisual $startupToggle (Join-Path $ArtifactRoot 'toggle-hover.png') 'ToggleSwitch hover state'

    $pages = @(
        @{ Navigation = '通用'; Visible = '关闭主窗口时'; File = '01-general' },
        @{ Navigation = '连接与设备'; Visible = '重新扫描附近设备'; File = '02-connection-devices' },
        @{ Navigation = '文件与存储'; Visible = '文件保存位置'; File = '03-files-storage' },
        @{ Navigation = '隐私与数据'; Visible = '保存聊天记录'; File = '04-privacy-data' },
        @{ Navigation = '关于'; Visible = '蓝联 BlueLink'; File = '05-about' }
    )
    $dpi = [BlueLinkSettingsUiNative]::GetDpiForWindow($mainHandle)
    if ($dpi -eq 0) { $dpi = 96 }
    $scalePercent = [int][Math]::Round($dpi * 100.0 / 96.0)

    foreach ($page in $pages) {
        Select-SettingsPage $settingsWindow $page.Navigation $page.Visible
        $capture = Join-Path $ArtifactRoot ("{0}-{1}pct.png" -f $page.File, $scalePercent)
        Capture-Window $settingsWindow $capture
    }

    Select-SettingsPage $settingsWindow '文件与存储' '文件保存位置'
    $scrollBar = Wait-Element { Find-VerticalScrollBar $settingsWindow } 8 'a functional vertical settings ScrollBar'
    $scrollPattern = [Windows.Automation.RangeValuePattern]$scrollBar.GetCurrentPattern(
        [Windows.Automation.RangeValuePattern]::Pattern)
    $beforeScroll = $scrollPattern.Current.Value
    $targetScroll = [Math]::Min($scrollPattern.Current.Maximum,
        $beforeScroll + [Math]::Max(1, $scrollPattern.Current.LargeChange))
    if ([Math]::Abs($targetScroll - $beforeScroll) -lt 0.001) {
        $targetScroll = [Math]::Max($scrollPattern.Current.Minimum,
            $beforeScroll - [Math]::Max(1, $scrollPattern.Current.LargeChange))
    }
    $scrollPattern.SetValue($targetScroll)
    Start-Sleep -Milliseconds 300
    Assert-True ([Math]::Abs($scrollPattern.Current.Value - $beforeScroll) -gt 0.001) 'Settings ScrollBar did not change its value.'
    $scrollPattern.SetValue($scrollPattern.Current.Minimum)
    Start-Sleep -Milliseconds 200
    $scrollBounds = $scrollBar.Current.BoundingRectangle
    [BlueLinkSettingsUiNative]::ClickAt(
        [int][Math]::Round($scrollBounds.Left + $scrollBounds.Width / 2),
        [int][Math]::Round($scrollBounds.Bottom - 18))
    Start-Sleep -Milliseconds 300
    Assert-True ($scrollPattern.Current.Value -gt $scrollPattern.Current.Minimum) 'Clicking the ScrollBar track did not page the settings view.'
    $scrollPattern.SetValue($scrollPattern.Current.Minimum)
    Start-Sleep -Milliseconds 200
    [BlueLinkSettingsUiNative]::WheelAt(
        [int][Math]::Round($windowBounds.Left + $windowBounds.Width * 0.7),
        [int][Math]::Round($windowBounds.Top + $windowBounds.Height * 0.55),
        -120)
    Start-Sleep -Milliseconds 300
    Assert-True ($scrollPattern.Current.Value -gt $scrollPattern.Current.Minimum) 'Mouse wheel did not move the settings ScrollBar.'

    Select-SettingsPage $settingsWindow '文件与存储' '文件大小上限 MiB'
    $limitText = Assert-VisibleElement $settingsWindow '文件大小上限 MiB'
    $valuePattern = [Windows.Automation.ValuePattern]$limitText.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    $valuePattern.SetValue('0')
    Invoke-Element (Find-ActionableByName $settingsWindow '保存设置') 'Save Settings button for invalid-value validation'
    Start-Sleep -Milliseconds 450
    Assert-True ($null -ne (Find-SettingsWindowByProcess $process.Id $mainWindow)) 'Invalid receive limit did not keep the Settings window open.'
    $invalidPattern = [Windows.Automation.ValuePattern]$limitText.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    Assert-True ($invalidPattern.Current.Value -eq '0') 'Invalid receive limit was unexpectedly rewritten or saved.'
    Capture-Window $settingsWindow (Join-Path $ArtifactRoot ("06-invalid-value-feedback-{0}pct.png" -f $scalePercent))

    Invoke-Element (Find-ActionableByName $settingsWindow '恢复默认值') 'Restore Defaults button'
    Start-Sleep -Milliseconds 250
    $restoredPattern = [Windows.Automation.ValuePattern]$limitText.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    Assert-True ($restoredPattern.Current.Value -eq '500') 'Restore Defaults did not restore the 500 MiB draft value.'
    Invoke-Element (Find-ActionableByName $settingsWindow '取消设置') 'Cancel Settings button'
    Start-Sleep -Milliseconds 400
    Assert-True ($null -eq (Find-SettingsWindowByProcess $process.Id $mainWindow)) 'Settings window did not close after Cancel.'

    @(
        'BlueLink settings UI Automation acceptance: PASS',
        "Executable=$ExecutablePath",
        "ProcessDpi=$dpi",
        "ScalePercent=$scalePercent",
        'Main settings button invoked=True',
        'Five navigation pages invoked=True',
        'Footer controls visible=True',
        'Toggle normal and hover visual contrast=True',
        'Vertical ScrollBar RangeValue changed=True',
        'Vertical ScrollBar track click changed value=True',
        'Mouse wheel changed ScrollBar value=True',
        'Invalid receive limit blocked=True',
        'Restore Defaults remained a draft until Save=True',
        'Cancel closed the dialog=True',
        'Valid persistence save=NOT VERIFIED (isolated control-channel smoke intentionally skips database initialization)'
    ) | Set-Content -LiteralPath (Join-Path $ArtifactRoot 'settings-ui-result.txt') -Encoding UTF8

    Write-Host "BlueLink settings UI Automation acceptance passed: $ArtifactRoot"
}
finally {
    if ($null -ne $process) {
        $process.Refresh()
        if (-not $process.HasExited) {
            if (-not [string]::IsNullOrWhiteSpace($exitEventName)) {
                try { $null = Signal-ControlEvent $exitEventName 3 } catch { }
            }
            if (-not $process.WaitForExit(8000)) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                $process.WaitForExit(5000) | Out-Null
            }
        }
        $process.Dispose()
    }
    if (-not [string]::IsNullOrWhiteSpace($isolatedDataRoot) -and (Test-Path -LiteralPath $isolatedDataRoot)) {
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $resolvedDataRoot = [IO.Path]::GetFullPath($isolatedDataRoot).TrimEnd('\')
        Assert-True ($resolvedDataRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) 'Refusing to clean a smoke-test data directory outside the system temp root.'
        Assert-True ((Split-Path -Leaf $resolvedDataRoot) -match '^BlueLinkStartupSmoke-\d+$') 'Refusing to clean an unexpected smoke-test data directory.'
        Remove-Item -LiteralPath $resolvedDataRoot -Recurse -Force
    }
}
