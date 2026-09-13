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
if ($xaml -match 'x:Name="WindowSurface"[^>]*(CornerRadius|SizeChanged)' -or
    $xaml -match 'x:Name="BrandPanel"[^>]*CornerRadius') {
    throw 'Installer outer window content must not add a second rounded silhouette around FluentWindow.'
}
$setupProject = Get-Content -LiteralPath (Join-Path $root 'installer\BlueLink.SetupUI\BlueLink.SetupUI.csproj') -Raw
if (-not $setupProject.Contains('<PackageReference Include="WPF-UI" Version="4.3.0"')) {
    throw 'Installer must pin WPF-UI 4.3.0.'
}

$bundle = Get-Content -LiteralPath (Join-Path $root 'installer\BlueLink.Bundle\Bundle.wxs') -Raw
foreach ($token in @('WixManagedBootstrapperApplicationHost', 'InstallFolder', 'CreateDesktopShortcut', 'AutoStart', 'BlueLinkMsi', 'DotNetCoreSearch', 'RuntimeType="desktop"', 'Platform="x64"', 'MajorVersion="8"', 'DesktopRuntime8X64', 'Compressed="no"', 'Cache="remove"', 'Permanent="yes"', 'Vital="yes"', 'RepairCondition="NOT DesktopRuntime8Version"', 'RuntimePerMachine', 'PackageUpgradeCode', 'ARPINSTALLLOCATION', 'DisplayProductVersion', 'ExpectedMsiProductCode', 'ExpectedPayloadFingerprint')) {
    if (-not $bundle.Contains($token)) { throw "Burn bundle contract missing: $token" }
}

$bootstrapper = Get-Content -LiteralPath (Join-Path $root 'installer\BlueLink.SetupUI\BlueLinkBootstrapper.cs') -Raw
foreach ($token in @('GetFormattedString("InstallFolder"', 'SetVariableString("InstallFolder"', 'SetInstallFolder(this.installFolder)', 'ShowFreshInstall', 'ShowOverwriteContext', 'ShowUninstall', 'GetNumeric("WixBundleInstalled"', 'PlanRelatedBundle', 'PlanRestoreRelatedBundle', 'plannedRelatedBundles.Add', 'InstallerExecutionPolicy.ShouldExecuteRelatedBundlePlan', 'PlanMsiPackage', 'e.ShouldExecute', 'InstallerExecutionPolicy.IsMsiExecutionPlanValid', 'InstallerExecutionPolicy.ShouldPlanRuntimePackage', 'suppressing .NET Desktop Runtime package', 'VerifyInstallPostconditions', 'InstallDirectoryOwnership.VerifyInstalledPayload', 'e.RecommendedState', 'InstallerExecutionPolicy.ShouldPlanRelatedBundleRemoval', 'InstallerExecutionPolicy.ShouldCleanLegacyBundleAfterApply', 'CleanupLegacyRelatedBundles', 'QuietUninstallString', 'Package Cache', 'Display.Embedded', 'RelationType.None', 'ResolveExistingInstallFolder', 'Registry.CurrentUser', 'MsiRelatedProductLocator.FindInstallFolders', 'StopInstalledApplication', 'InstalledApplicationController.Stop')) {
    if (-not $bootstrapper.Contains($token)) { throw "Installer path propagation contract missing: $token" }
}
$processController = Get-Content -LiteralPath (Join-Path $root 'shared\InstalledApplicationController.cs') -Raw
foreach ($token in @('WindowsAppControlChannel.SignalExit', 'Process.GetProcessesByName("BlueLink")', 'process.CloseMainWindow()', 'process.Kill()', 'Path.Combine(root, "app", "BlueLink.exe")')) {
    if (-not $processController.Contains($token)) { throw "Installed-process control contract missing: $token" }
}

$installerWindow = Get-Content -LiteralPath (Join-Path $root 'installer\BlueLink.SetupUI\InstallerWindow.xaml.cs') -Raw
foreach ($token in @('SetDisplayVersion', 'InstallerExecutionPolicy.GetDisplayVersion', 'NormalizeInstallFolder', 'ValidateInstallDirectoryContents', 'CleanupProductRollbackFiles', 'InstallDirectoryOwnership.ValidateInstallable', 'directory.Name.Equals("BlueLink"')) {
    if (-not $installerWindow.Contains($token)) { throw "Installer overwrite/path contract missing: $token" }
}
if ($installerWindow.Contains('FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion')) {
    throw 'Installer welcome page must not expose SDK informational ProductVersion metadata.'
}
$ownership = Get-Content -LiteralPath (Join-Path $root 'shared\InstallDirectoryOwnership.cs') -Raw
foreach ($token in @('DataContractJsonSerializer', 'TrimStart(''\uFEFF'')', 'OwnedPaths', 'PayloadFingerprint', 'PayloadFiles', 'VerifyInstalledPayload', 'SHA256.Create', 'NormalizeOwnedPaths', 'IsProductRollbackFile', 'Uri.IsHexDigit', 'Download\\', 'ForeignContent')) {
    if (-not $ownership.Contains($token)) { throw "Installer ownership contract missing: $token" }
}
if ($installerWindow.Contains('IndexOf("\"ProductId\"') -or $ownership.Contains('IndexOf("\"ProductId\"')) {
    throw 'Installer must parse the ownership manifest semantically instead of matching JSON formatting.'
}
$installerPrompt = Get-Content -LiteralPath (Join-Path $root 'installer\BlueLink.SetupUI\InstallerPromptWindow.xaml.cs') -Raw
foreach ($token in @('new InstallerDialogWindow', 'SaveSnapshot(owner, dialog, autoCancelSnapshotPath)', 'dialog.ShowDialog()', 'dialog.Confirmed')) {
    if (-not $installerPrompt.Contains($token)) { throw "Installer shared dialog contract missing: $token" }
}
if ($installerPrompt.Contains('TestableMessageBox') -or $installerPrompt.Contains('InvokeCloseButton')) {
    throw 'Installer contains the removed MessageBox test subclass or internal close shortcut.'
}

# Audit the production entry points, not only a separately rendered component.
$uninstaller = Get-Content -LiteralPath (Join-Path $root 'installer\BlueLink.Uninstall\UninstallWindow.xaml.cs') -Raw
foreach ($token in @('var dialog = CreateRemovalConfirmation()', 'InstallerConfirmationDialog.Create(this', 'ReadRegistration(this.installRoot)', 'PathsEqual(folder, expectedInstallRoot)')) {
    if (-not $uninstaller.Contains($token)) { throw "Standalone uninstall entry-point contract missing: $token" }
}
if (-not $installerPrompt.Contains('InstallerConfirmationDialog.Create(owner, title, message)')) { throw 'Burn confirmation bypasses the shared prototype dialog.' }
foreach ($project in @('BlueLink.SetupUI', 'BlueLink.Uninstall')) {
    $definition = Get-Content -LiteralPath (Join-Path $root "installer\$project\$project.csproj") -Raw
    if (-not $definition.Contains('SharedUI\InstallerConfirmationDialog.cs')) { throw "Shared confirmation is not linked by $project" }
}

$package = Get-Content -LiteralPath (Join-Path $root 'installer\BlueLink.Package\Package.wxs') -Raw
foreach ($token in @('LauncherComponent', 'UninstallerComponent', 'InstallManifestComponent', 'APPFOLDER', 'BOOTSTRAPFOLDER', 'DownloadFolder', 'PublishedPayloadComponents', 'MsiProductCode', 'BundleUpgradeCode', 'BundleProviderKey', 'ARPSYSTEMCOMPONENT', 'ARPINSTALLLOCATION')) {
    if (-not $package.Contains($token)) { throw "MSI installed-structure contract missing: $token" }
}

# Review artifacts intentionally reuse VERSION, so every rebuilt MSI must upgrade
# the previous product inside the transaction before writing its shared paths.
$reviewBuild = Get-Content -LiteralPath (Join-Path $root 'scripts\build-windows-review.ps1') -Raw
foreach ($token in @('-p:MajorUpgradeSchedule=afterInstallInitialize', '-p:AllowSameVersionUpgrades=yes', '-p:ManifestPath=$manifest')) {
    if (-not $reviewBuild.Contains($token)) { throw "Review repeat-install contract missing: $token" }
}
foreach ($token in @('$(var.MajorUpgradeSchedule)', '$(var.AllowSameVersionUpgrades)')) {
    if (-not $package.Contains($token)) { throw "MSI upgrade configuration is disconnected: $token" }
}
foreach ($token in @('InstallDirectoryValidator = this.ValidateInstallDirectory', 'this.ValidateInstallDirectory(this.installFolder)', 'CanRecoverRegisteredInstall', 'incoming-install-manifest.json')) {
    if (-not $bootstrapper.Contains($token)) { throw "MSI recovery entry-point contract missing: $token" }
}
if (-not $bundle.Contains('Name="incoming-install-manifest.json" SourceFile="$(var.ManifestPath)"')) { throw 'Recovery manifest is not included in the actual bundle.' }

$build = Get-Content -LiteralPath (Join-Path $root 'scripts\build-windows-release.ps1') -Raw
if ($build -match '(?i)inno|iscc|BlueLink\.iss') { throw 'Windows release script still references Inno Setup.' }
foreach ($token in @('BlueLink.SetupUI.csproj', 'BlueLink.Launcher.csproj', 'BlueLink.Uninstall.csproj', 'BlueLink.Package.wixproj', 'BlueLink.Bundle.wixproj', '--self-contained false', 'PublishSingleFile=false', 'PublishTrimmed=false', 'generate-wix-payload.ps1', 'Get-DeterministicGuid', 'BundleProviderKey', 'release-payload-lock.json', 'PayloadFingerprint', 'Increment VERSION')) {
    if (-not $build.Contains($token)) { throw "Release pipeline does not build: $token" }
}
foreach ($token in @('Invoke-OverwriteDialogAutomation', 'AutomationElement]::RootElement.FindAll', 'InvokePattern]$cancel.GetCurrentPattern', 'AddSeconds(45)', '关闭并继续安装')) {
    if (-not $build.Contains($token)) { throw "External overwrite UI Automation contract missing: $token" }
}

Write-Host 'Custom WiX installer contract verified.'
