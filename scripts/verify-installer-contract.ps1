$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$required = @(
    'installer\BlueLink.SetupUI\BlueLink.SetupUI.csproj',
    'installer\BlueLink.SetupUI\InstallerWindow.xaml',
    'installer\BlueLink.SetupUI\BlueLinkBootstrapper.cs',
    'shared\InstallDirectoryOwnership.cs',
    'installer\BlueLink.Installation.Tests\BlueLink.Installation.Tests.csproj',
    'installer\BlueLink.Launcher\BlueLink.Launcher.csproj',
    'installer\BlueLink.Launcher\RuntimeWindow.xaml',
    'installer\BlueLink.Uninstall\BlueLink.Uninstall.csproj',
    'installer\BlueLink.Uninstall\UninstallWindow.xaml',
    'installer\BlueLink.Package\BlueLink.Package.wixproj',
    'installer\BlueLink.Package\Package.wxs',
    'installer\BlueLink.Bundle\BlueLink.Bundle.wixproj',
    'installer\BlueLink.Bundle\Bundle.wxs'
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relative))) {
        throw "Custom installer contract file missing: $relative"
    }
}
if (Test-Path -LiteralPath (Join-Path $root 'installer\BlueLink.iss')) {
    throw 'Legacy Inno installer script must not exist.'
}

$xaml = Get-Content -LiteralPath (Join-Path $root 'installer\BlueLink.SetupUI\InstallerWindow.xaml') -Raw
$orderedPages = @('WelcomePage', 'LocationPage', 'RuntimePage', 'ProgressPage', 'CompletePage', 'UninstallPage')
$last = -1
foreach ($page in $orderedPages) {
    $index = $xaml.IndexOf("x:Name=`"$page`"", [System.StringComparison]::Ordinal)
    if ($index -lt 0 -or $index -le $last) { throw "Installer page order is invalid at $page" }
    $last = $index
}
foreach ($requiredToken in @('ui:FluentWindow', 'Style="{StaticResource {x:Type ui:FluentWindow}}"', 'ui:TitleBar', 'ui:Button', 'ui:TextBox', 'InstallProgressBar')) {
    if (-not $xaml.Contains($requiredToken)) { throw "Installer WPF-UI control contract missing: $requiredToken" }
}
if ($xaml.Contains('ControlTemplate')) { throw 'Installer must not contain handwritten base-control templates.' }
if ($xaml.Contains('MaintenancePage')) {
    throw 'The removed installed-product maintenance page has returned.'
}
$setupProject = Get-Content -LiteralPath (Join-Path $root 'installer\BlueLink.SetupUI\BlueLink.SetupUI.csproj') -Raw
if (-not $setupProject.Contains('<PackageReference Include="WPF-UI" Version="4.3.0"')) {
    throw 'Installer must pin WPF-UI 4.3.0.'
}

$bundle = Get-Content -LiteralPath (Join-Path $root 'installer\BlueLink.Bundle\Bundle.wxs') -Raw
foreach ($token in @('WixManagedBootstrapperApplicationHost', 'InstallFolder', 'CreateDesktopShortcut', 'AutoStart', 'BlueLinkMsi', 'DotNetCoreSearch', 'RuntimeType="desktop"', 'Platform="x64"', 'MajorVersion="8"', 'DesktopRuntime8X64', 'Compressed="no"', 'Cache="remove"', 'Permanent="yes"', 'Vital="yes"', 'RuntimePerMachine', 'PackageUpgradeCode', 'ARPINSTALLLOCATION')) {
    if (-not $bundle.Contains($token)) { throw "Burn bundle contract missing: $token" }
}

$bootstrapper = Get-Content -LiteralPath (Join-Path $root 'installer\BlueLink.SetupUI\BlueLinkBootstrapper.cs') -Raw
foreach ($token in @('GetFormattedString("InstallFolder"', 'SetVariableString("InstallFolder"', 'SetInstallFolder(this.installFolder)', 'ShowOverwriteContext', 'ShowUninstall', 'PlanRelatedBundle', 'PlanRestoreRelatedBundle', 'e.RecommendedState', 'InstallerExecutionPolicy.ShouldPlanRelatedBundleRemoval', 'Display.Embedded', 'RelationType.None', 'ResolveExistingInstallFolder', 'Registry.CurrentUser', 'MsiRelatedProductLocator.FindInstallFolders', 'StopInstalledApplication', 'InstalledApplicationController.Stop')) {
    if (-not $bootstrapper.Contains($token)) { throw "Installer path propagation contract missing: $token" }
}
$processController = Get-Content -LiteralPath (Join-Path $root 'shared\InstalledApplicationController.cs') -Raw
foreach ($token in @('WindowsAppControlChannel.SignalExit', 'Process.GetProcessesByName("BlueLink")', 'process.CloseMainWindow()', 'process.Kill()', 'Path.Combine(root, "app", "BlueLink.exe")')) {
    if (-not $processController.Contains($token)) { throw "Installed-process control contract missing: $token" }
}

$installerWindow = Get-Content -LiteralPath (Join-Path $root 'installer\BlueLink.SetupUI\InstallerWindow.xaml.cs') -Raw
foreach ($token in @('NormalizeInstallFolder', 'ValidateInstallDirectoryContents', 'CleanupProductRollbackFiles', 'InstallDirectoryOwnership.ValidateInstallable', 'directory.Name.Equals("BlueLink"')) {
    if (-not $installerWindow.Contains($token)) { throw "Installer overwrite/path contract missing: $token" }
}
$ownership = Get-Content -LiteralPath (Join-Path $root 'shared\InstallDirectoryOwnership.cs') -Raw
foreach ($token in @('DataContractJsonSerializer', 'TrimStart(''\uFEFF'')', 'OwnedPaths', 'NormalizeOwnedPaths', 'IsProductRollbackFile', 'Uri.IsHexDigit', 'Download\\', 'ForeignContent')) {
    if (-not $ownership.Contains($token)) { throw "Installer ownership contract missing: $token" }
}
if ($installerWindow.Contains('IndexOf("\"ProductId\"') -or $ownership.Contains('IndexOf("\"ProductId\"')) {
    throw 'Installer must parse the ownership manifest semantically instead of matching JSON formatting.'
}
$installerPrompt = Get-Content -LiteralPath (Join-Path $root 'installer\BlueLink.SetupUI\InstallerPromptWindow.xaml.cs') -Raw
foreach ($token in @('new Wpf.Ui.Controls.MessageBox', 'Application.Current?.TryFindResource(typeof(Wpf.Ui.Controls.MessageBox))', 'AutomationElement.FromHandle', 'InvokePattern.Pattern')) {
    if (-not $installerPrompt.Contains($token)) { throw "Installer official MessageBox contract missing: $token" }
}
if ($installerPrompt.Contains('TestableMessageBox') -or $installerPrompt.Contains('InvokeCloseButton')) {
    throw 'Installer contains the removed MessageBox test subclass or internal close shortcut.'
}

$package = Get-Content -LiteralPath (Join-Path $root 'installer\BlueLink.Package\Package.wxs') -Raw
foreach ($token in @('LauncherComponent', 'UninstallerComponent', 'InstallManifestComponent', 'APPFOLDER', 'BOOTSTRAPFOLDER', 'DownloadFolder', 'PublishedPayloadComponents', 'MsiProductCode', 'BundleUpgradeCode', 'BundleProviderKey', 'ARPSYSTEMCOMPONENT', 'ARPINSTALLLOCATION')) {
    if (-not $package.Contains($token)) { throw "MSI installed-structure contract missing: $token" }
}

$build = Get-Content -LiteralPath (Join-Path $root 'scripts\build-windows-release.ps1') -Raw
if ($build -match '(?i)inno|iscc|BlueLink\.iss') { throw 'Windows release script still references Inno Setup.' }
foreach ($token in @('BlueLink.SetupUI.csproj', 'BlueLink.Launcher.csproj', 'BlueLink.Uninstall.csproj', 'BlueLink.Package.wixproj', 'BlueLink.Bundle.wixproj', '--self-contained false', 'PublishSingleFile=false', 'PublishTrimmed=false', 'generate-wix-payload.ps1', 'Get-DeterministicGuid', 'BundleProviderKey')) {
    if (-not $build.Contains($token)) { throw "Release pipeline does not build: $token" }
}

Write-Host 'Custom WiX installer contract verified.'
