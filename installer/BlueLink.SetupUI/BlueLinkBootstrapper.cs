namespace BlueLink.SetupUI
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Win32;
    using System.Windows;
    using System.Windows.Interop;
    using System.Windows.Threading;
    using BlueLink.Shared;
    using WixToolset.Mba.Core;

    public sealed class BlueLinkBootstrapper : BootstrapperApplication
    {
        private readonly IBootstrapperCommand command;
        private Dispatcher dispatcher;
        private InstallerWindow window;
        private bool installed;
        private bool existingInstallation;
        private bool closing;
        private bool uninstalling;
        private bool cancelRequested;
        private int result;
        private string lastError;
        private string installFolder;
        private bool runtimeOnlyPlan;
        private bool runtimeAvailable;
        private string runtimeVersion;
        private string runtimeSize;
        private string productRegistryKey;
        private string packageUpgradeCode;
        private string firstEmbeddedSafeVersion;
        private string displayProductVersion;
        private string expectedMsiProductCode;
        private string expectedPayloadFingerprint;
        private LaunchAction plannedLaunchAction;
        private bool blueLinkMsiPlanObserved;
        private bool blueLinkMsiShouldExecute;
        private ActionState blueLinkMsiAction;
        private readonly Dictionary<string, string> relatedBundleVersions =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> plannedRelatedBundles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> plannedRelatedBundleRestores =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private DispatcherTimer progressWatchdog;
        private DateTime lastEngineActivityUtc;
        private int lastOverallPercentage;
        private string currentExecutePhase;
        private bool applyInProgress;
        private bool stalledProgressReported;
        private bool programRemoved;
        private bool deleteSelectedUserData;
        private bool runtimeJustInstalled;

        public BlueLinkBootstrapper(IEngine engine, IBootstrapperCommand command) : base(engine)
        {
            this.command = command;
            this.installFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "BlueLink");

            this.DetectBegin += this.OnDetectBegin;
            this.DetectRelatedBundle += this.OnDetectRelatedBundle;
            this.DetectComplete += this.OnDetectComplete;
            this.PlanRelatedBundle += this.OnPlanRelatedBundle;
            this.PlanRestoreRelatedBundle += this.OnPlanRestoreRelatedBundle;
            this.PlanPackageBegin += this.OnPlanPackageBegin;
            this.PlanMsiPackage += this.OnPlanMsiPackage;
            this.PlanComplete += this.OnPlanComplete;
            this.Progress += this.OnProgress;
            this.CacheAcquireProgress += (sender, args) => { args.Cancel = cancelRequested; if (runtimeOnlyPlan) window.ShowRuntimeProgress("downloading", args.Progress, args.Total); };
            this.CacheVerifyBegin += (sender, args) => { args.Cancel = cancelRequested; if (runtimeOnlyPlan) window.ShowRuntimeProgress("verifying"); };
            this.ExecutePackageBegin += this.OnExecutePackageBegin;
            this.ExecutePackageComplete += this.OnExecutePackageComplete;
            this.ExecuteProgress += this.OnExecuteProgress;
            this.Error += this.OnError;
            this.ApplyComplete += this.OnApplyComplete;
        }

        protected override void Run()
        {
            var baData = new BootstrapperApplicationData();
            var mbaCommand = this.command.ParseCommandLine();
            mbaCommand.SetOverridableVariables(baData.Bundle.OverridableVariables, this.engine);
            this.productRegistryKey = this.GetString("ProductRegistryKey", @"Software\BlueLink");
            this.packageUpgradeCode = this.GetString("PackageUpgradeCode", "{BE8A6A43-710C-4B6E-92DC-07C4FAFC22B9}");
            this.firstEmbeddedSafeVersion = this.GetString("EmbeddedRelatedBundleSafeVersion", "0.2.12");
            this.displayProductVersion = this.GetString("DisplayProductVersion", "未知");
            this.expectedMsiProductCode = this.GetString("ExpectedMsiProductCode", String.Empty);
            this.expectedPayloadFingerprint = this.GetString("ExpectedPayloadFingerprint", String.Empty);
            var defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "BlueLink");
            var requestedFolder = this.GetFormattedString("InstallFolder", defaultFolder);
            this.installFolder = this.ResolveExistingInstallFolder(requestedFolder);
            this.existingInstallation = ContainsInstalledApplication(this.installFolder);
            // Burn variables can contain formatted folder tokens.  Resolve the
            // token once and persist the concrete path before every plan so
            // quiet installs and later repairs never pass the literal
            // "[LocalAppDataFolder]..." text to MSI.
            this.engine.SetVariableString("InstallFolder", this.installFolder, false);

            this.dispatcher = Dispatcher.CurrentDispatcher;
            this.progressWatchdog = new DispatcherTimer(DispatcherPriority.Background, this.dispatcher)
            {
                Interval = TimeSpan.FromSeconds(5),
            };
            this.progressWatchdog.Tick += this.OnProgressWatchdog;
            this.window = new InstallerWindow();
            this.window.InstallDirectoryValidator = this.ValidateInstallDirectory;
            this.window.SetDisplayVersion(this.displayProductVersion);
            this.window.SetLogPath(GetString("WixBundleLog", String.Empty));
            this.window.SetInstallFolder(this.installFolder);
            this.window.InstallRequested += this.Install;
            this.window.RemoveApplicationRequested += async () =>
            {
                this.deleteSelectedUserData = window.DeleteUserData;
                if (this.programRemoved) { await CompleteDataRemovalAsync(); return; }
                this.uninstalling = true;
                this.PrepareAndPlan(LaunchAction.Uninstall, "正在卸载蓝联…");
            };
            this.window.CancelRequested += this.Cancel;
            this.window.ApplyCancelRequested += () => this.cancelRequested = true;
            this.window.RuntimeInstallRequested += this.InstallRuntimePrerequisite;
            this.window.RuntimeRedetectRequested += () => this.engine.Detect();
            this.window.RuntimeContinueRequested += () => { runtimeJustInstalled = false; window.ShowFreshInstall(); };
            this.window.Closed += (s, e) =>
            {
                if (!this.closing) this.Cancel();
                this.dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            };

            if (this.command.Display == Display.Full || this.command.Display == Display.Passive) this.window.Show();
            this.engine.Detect();
            Dispatcher.Run();

            var exitCode = this.result;
            if ((exitCode & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000)) exitCode &= 0xFFFF;
            Application.Current?.Shutdown();
            this.engine.Quit(exitCode);
        }

        private void OnDetectBegin(object sender, DetectBeginEventArgs e)
        {
            // Registration is not final until DetectComplete. WixBundleInstalled
            // is refreshed by Burn during detect and is the authoritative state
            // for deciding between a first install and an exact-bundle repair.
            this.installed = false;
        }

        private void OnDetectRelatedBundle(object sender, DetectRelatedBundleEventArgs e)
        {
            if (!String.IsNullOrWhiteSpace(e.ProductCode))
                this.relatedBundleVersions[e.ProductCode] = e.Version;
            this.engine.Log(LogLevel.Standard,
                "BlueLink BA: detected related bundle " + e.ProductCode +
                ", version " + (e.Version ?? "unknown") + ", relation " + e.RelationType + ".");
        }

        private void OnDetectComplete(object sender, DetectCompleteEventArgs e)
        {
            if (e.Status < 0)
            {
                this.result = e.Status;
                this.window.ShowFailure("无法检测现有安装，错误代码：0x" + e.Status.ToString("X8"));
                return;
            }

            this.installed = this.GetNumeric("WixBundleInstalled", 0) != 0;
            this.engine.Log(LogLevel.Standard,
                "BlueLink BA: current bundle installed after detect: " + this.installed + ".");

            this.runtimeVersion = this.GetString("DesktopRuntimePackageVersion", "8.0.x");
            this.runtimeSize = this.GetString("DesktopRuntimePackageSize", "读取实际包大小");
            this.runtimeAvailable = !String.IsNullOrWhiteSpace(this.GetString("DesktopRuntime8Version", String.Empty));
            if (runtimeJustInstalled && runtimeAvailable && command.Display == Display.Full) { window.ShowRuntimeProgress("completed"); return; }

            var snapshotPath = this.GetString("SetupSnapshotPath", String.Empty);
            if (!String.IsNullOrWhiteSpace(snapshotPath))
            {
                var snapshotPage = this.GetString("SetupSnapshotPage", "welcome");
                this.window.CapturePage(snapshotPage, snapshotPath);
                this.CloseWindow();
                return;
            }

            var overwriteCancelSnapshotPath = this.GetString("SetupOverwriteCancelSnapshotPath", String.Empty);
            if (!String.IsNullOrWhiteSpace(overwriteCancelSnapshotPath))
            {
                try { this.ValidateInstallDirectory(this.installFolder); }
                catch (Exception failure)
                {
                    this.result = 1603;
                    this.window.ShowFailure(failure.Message);
                    this.CloseWindow();
                    return;
                }
                this.window.ShowOverwriteContext();
                try
                {
                    this.window.Confirm("蓝联正在运行",
                        "覆盖安装需要先关闭正在运行的蓝联。确认后，安装向导将关闭应用并继续安装。",
                        overwriteCancelSnapshotPath);
                }
                catch (Exception failure)
                {
                    this.result = 1603;
                    this.engine.Log(LogLevel.Error,
                        "BlueLink BA: overwrite UI Automation smoke failed: " + failure);
                }
                this.CloseWindow();
                return;
            }

            if (this.command.Display == Display.Full && !this.runtimeAvailable &&
                this.command.Action != LaunchAction.Uninstall)
            {
                this.window.ShowRuntimeRequired(this.runtimeVersion, this.runtimeSize);
                return;
            }

            if (this.command.Display != Display.Full)
            {
                var action = this.command.Action == LaunchAction.Unknown
                    ? LaunchAction.Install : this.command.Action;
                if (InstallerExecutionPolicy.ShouldConvertInstallToRepair(
                        this.installed, action.ToString()))
                    action = LaunchAction.Repair;
                this.uninstalling = action == LaunchAction.Uninstall;
                var status = action == LaunchAction.Uninstall ? "正在卸载蓝联…" : action == LaunchAction.Repair ? "正在修复蓝联…" : "正在安装蓝联…";
                if (action == LaunchAction.Install || action == LaunchAction.Repair)
                {
                    try { this.ValidateInstallDirectory(this.installFolder); }
                    catch (Exception failure)
                    {
                        this.result = 1603;
                        this.window.ShowFailure(failure.Message);
                        this.CloseWindow();
                        return;
                    }
                }
                this.PrepareAndPlan(action, status);
                return;
            }

            if (this.command.Action == LaunchAction.Uninstall)
            {
                this.uninstalling = true;
                this.window.ShowUninstall();
            }
            else
            {
                this.window.SetInstallFolder(this.installFolder);
                this.window.ShowFreshInstall();
            }
        }

        private void OnPlanRelatedBundle(object sender, PlanRelatedBundleEventArgs e)
        {
            string detectedVersion;
            this.relatedBundleVersions.TryGetValue(e.BundleId, out detectedVersion);
            var canRunEmbedded = this.CanExecuteRelatedBundle(detectedVersion);
            var firstPlanForBundle = this.plannedRelatedBundles.Add(e.BundleId);

            // Bundles before 0.2.12 contain a BA which reaches ApplyComplete in
            // embedded mode but never exits. Invoking one from this transaction
            // permanently blocks the parent Burn engine. MSI MajorUpgrade owns
            // the transactional payload replacement; only bundles whose BA has
            // the embedded-exit fix may use Burn's recommended related plan.
            e.State = InstallerExecutionPolicy.ShouldExecuteRelatedBundlePlan(canRunEmbedded, firstPlanForBundle)
                ? e.RecommendedState : RequestState.None;
            this.engine.Log(LogLevel.Standard,
                "BlueLink BA: related bundle " + e.BundleId + ", version " +
                (detectedVersion ?? "unknown") + ", requested state " + e.State +
                (!firstPlanForBundle ? "; duplicate related-bundle plan suppressed." :
                 canRunEmbedded ? "." : "; legacy embedded execution suppressed to prevent upgrade deadlock."));
        }

        private void OnPlanRestoreRelatedBundle(object sender, PlanRestoreRelatedBundleEventArgs e)
        {
            string detectedVersion;
            this.relatedBundleVersions.TryGetValue(e.BundleId, out detectedVersion);
            var canRunEmbedded = this.CanExecuteRelatedBundle(detectedVersion);
            var firstRestoreForBundle = this.plannedRelatedBundleRestores.Add(e.BundleId);
            e.State = InstallerExecutionPolicy.ShouldExecuteRelatedBundlePlan(canRunEmbedded, firstRestoreForBundle)
                ? e.RecommendedState : RequestState.None;
            this.engine.Log(LogLevel.Standard,
                "BlueLink BA: related bundle rollback restore " + e.BundleId +
                ", version " + (detectedVersion ?? "unknown") + ", requested state " + e.State +
                (!firstRestoreForBundle ? "; duplicate related-bundle restore suppressed." :
                 canRunEmbedded ? "." : "; legacy restore suppressed to prevent rollback deadlock."));
        }

        private bool CanExecuteRelatedBundle(string detectedVersion) =>
            InstallerExecutionPolicy.ShouldPlanRelatedBundleRemoval(
                this.uninstalling, detectedVersion, this.firstEmbeddedSafeVersion);

        private void OnPlanPackageBegin(object sender, PlanPackageBeginEventArgs e)
        {
            if (e.PackageId.Equals("DesktopRuntime8X64", StringComparison.OrdinalIgnoreCase))
            {
                if (!InstallerExecutionPolicy.ShouldPlanRuntimePackage(
                        this.runtimeOnlyPlan, this.runtimeAvailable, this.uninstalling))
                {
                    e.State = RequestState.None;
                    this.engine.Log(LogLevel.Standard,
                        "BlueLink BA: suppressing .NET Desktop Runtime package because the required runtime is already available.");
                }
                else if (!this.runtimeAvailable)
                {
                    e.State = RequestState.Present;
                    this.engine.Log(LogLevel.Standard,
                        "BlueLink BA: planning .NET Desktop Runtime because the required runtime is missing.");
                }
                return;
            }

            if (this.runtimeOnlyPlan && e.PackageId.Equals("BlueLinkMsi", StringComparison.OrdinalIgnoreCase))
                e.State = RequestState.None;
        }

        private void OnPlanMsiPackage(object sender, PlanMsiPackageEventArgs e)
        {
            if (!e.PackageId.Equals("BlueLinkMsi", StringComparison.OrdinalIgnoreCase)) return;
            this.blueLinkMsiPlanObserved = true;
            if (e.ShouldExecute && e.Action != ActionState.None)
            {
                this.blueLinkMsiShouldExecute = true;
                this.blueLinkMsiAction = e.Action;
            }
            this.engine.Log(LogLevel.Standard,
                "BlueLink BA: planned BlueLinkMsi, should execute: " + e.ShouldExecute +
                ", action: " + e.Action + ".");
        }

        private void InstallRuntimePrerequisite()
        {
            this.runtimeOnlyPlan = true;
            this.uninstalling = false;
            this.Plan(LaunchAction.Install, "正在下载并安装 .NET Desktop Runtime " + this.runtimeVersion + "…");
        }

        private void Install(string folder, bool createDesktopShortcut, bool autoStart)
        {
            this.installFolder = Path.GetFullPath(folder);
            try { this.ValidateInstallDirectory(this.installFolder); }
            catch (Exception failure) { this.window.ShowFailure(failure.Message); return; }
            this.engine.SetVariableString("InstallFolder", this.installFolder, false);
            this.engine.SetVariableNumeric("CreateDesktopShortcut", createDesktopShortcut ? 1 : 0);
            this.engine.SetVariableNumeric("AutoStart", autoStart ? 1 : 0);
            this.uninstalling = false;
            var action = this.installed ? LaunchAction.Repair : LaunchAction.Install;
            this.PrepareAndPlan(action, this.existingInstallation || this.installed
                ? "正在重新安装蓝联…" : "正在安装蓝联…");
        }

        private void PrepareAndPlan(LaunchAction action, string status)
        {
            this.engine.Log(LogLevel.Standard, "BlueLink BA: PrepareAndPlan begin: " + action);
            if ((action == LaunchAction.Install || action == LaunchAction.Repair || action == LaunchAction.Uninstall) &&
                !this.StopInstalledApplication(this.command.Display == Display.Full && action != LaunchAction.Uninstall)) return;
            this.engine.Log(LogLevel.Standard, "BlueLink BA: process stop check complete: " + action);
            this.Plan(action, status);
        }

        private bool StopInstalledApplication(bool requestConfirmation)
        {
            // Only close the executable that belongs to the selected install.
            // A portable copy with the same process name may be running from a
            // different directory and must not be terminated by this bundle.
            this.engine.Log(LogLevel.Standard, "BlueLink BA: checking exact installed processes in " + this.installFolder);
            if (!InstalledApplicationController.IsRunning(this.installFolder))
            {
                this.engine.Log(LogLevel.Standard, "BlueLink BA: no exact installed process is running.");
                return true;
            }
            this.engine.Log(LogLevel.Standard, "BlueLink BA: exact installed process found.");
            if (requestConfirmation)
            {
                var cancelSnapshotPath = this.GetString("SetupOverwriteCancelSnapshotPath", String.Empty);
                if (!this.window.Confirm("蓝联正在运行",
                        "覆盖安装需要先关闭正在运行的蓝联。确认后，安装向导将关闭应用并继续安装。",
                        cancelSnapshotPath))
                {
                    if (!String.IsNullOrWhiteSpace(cancelSnapshotPath)) this.CloseWindow();
                    return false;
                }
            }
            string error;
            this.engine.Log(LogLevel.Standard, "BlueLink BA: requesting exact installed process shutdown.");
            if (InstalledApplicationController.Stop(this.installFolder,
                    TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), out error))
            {
                this.engine.Log(LogLevel.Standard, "BlueLink BA: exact installed process shutdown complete.");
                return true;
            }
            this.engine.Log(LogLevel.Error, "BlueLink BA: exact installed process shutdown failed: " + error);
            this.window.ShowFailure("关闭正在运行的蓝联失败：" + error);
            if (this.command.Display != Display.Full)
            {
                this.result = 1603;
                this.CloseWindow();
            }
            return false;
        }

        private string ResolveExistingInstallFolder(string fallback)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(this.productRegistryKey))
                {
                    var registered = key?.GetValue("InstallFolder") as string;
                    if (ContainsInstalledApplication(registered)) return Path.GetFullPath(registered);
                }
            }
            catch { }

            if (ContainsInstalledApplication(fallback)) return Path.GetFullPath(fallback);

            foreach (var registered in MsiRelatedProductLocator.FindInstallFolders(this.packageUpgradeCode))
            {
                if (ContainsInstalledApplication(registered) || this.CanRecoverRegisteredInstall(registered))
                    return Path.GetFullPath(registered);
            }

            // The process fallback exists only for legacy production installs
            // that predate the exact registration values. Isolated acceptance
            // identities must never adopt a production process or folder.
            if (!this.productRegistryKey.Equals(@"Software\BlueLink", StringComparison.OrdinalIgnoreCase))
                return fallback;

            foreach (var process in Process.GetProcessesByName("BlueLink"))
            {
                try
                {
                    var executable = process.MainModule.FileName;
                    var folder = Path.GetDirectoryName(executable);
                    if (ContainsInstalledApplication(folder)) return Path.GetFullPath(folder);
                }
                catch { }
                finally { process.Dispose(); }
            }
            return Path.GetFullPath(fallback);
        }

        private bool CanRecoverRegisteredInstall(string folder)
        {
            var manifest = Path.Combine(Path.GetDirectoryName(typeof(BlueLinkBootstrapper).Assembly.Location),
                "incoming-install-manifest.json");
            return MsiRelatedProductLocator.FindInstallFolders(this.packageUpgradeCode).Any(registered =>
                InstallDirectoryOwnership.CanRecoverRegisteredInstall(folder, registered, manifest));
        }

        private void ValidateInstallDirectory(string folder)
        {
            if (this.CanRecoverRegisteredInstall(folder))
            {
                this.engine.Log(LogLevel.Standard, "BlueLink BA: validated exact MSI recovery directory: " + folder);
                return;
            }
            InstallerWindow.ValidateInstallDirectoryContents(folder);
        }

        private static bool ContainsInstalledApplication(string folder)
        {
            if (String.IsNullOrWhiteSpace(folder)) return false;
            try
            {
                return InstallDirectoryOwnership.Inspect(folder).IsRecognizedInstall;
            }
            catch { return false; }
        }

        private static bool PathsEqual(string left, string right)
        {
            try { return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private void Plan(LaunchAction action, string status)
        {
            this.plannedLaunchAction = action;
            this.plannedRelatedBundles.Clear();
            this.plannedRelatedBundleRestores.Clear();
            this.blueLinkMsiPlanObserved = false;
            this.blueLinkMsiShouldExecute = false;
            this.blueLinkMsiAction = ActionState.None;
            this.lastError = null;
            this.currentExecutePhase = null;
            this.lastOverallPercentage = 0;
            this.stalledProgressReported = false;
            this.engine.Log(LogLevel.Standard, "BlueLink BA: updating apply UI before plan: " + action);
            this.window.ShowInstalling(status);
            if (runtimeOnlyPlan) window.ShowRuntimeProgress("downloading");
            this.engine.Log(LogLevel.Standard, "BlueLink BA: calling engine.Plan: " + action);
            this.engine.Plan(action);
            this.engine.Log(LogLevel.Standard, "BlueLink BA: engine.Plan returned: " + action);
        }

        private void OnPlanComplete(object sender, PlanCompleteEventArgs e)
        {
            if (e.Status < 0)
            {
                this.result = e.Status;
                this.window.ShowFailure("无法准备安装操作，错误代码：0x" + e.Status.ToString("X8"));
                return;
            }

            var requireMsiExecution = !this.runtimeOnlyPlan && !this.uninstalling &&
                (this.plannedLaunchAction == LaunchAction.Install ||
                 this.plannedLaunchAction == LaunchAction.Repair);
            if (!InstallerExecutionPolicy.IsMsiExecutionPlanValid(
                    requireMsiExecution,
                    this.blueLinkMsiPlanObserved,
                    this.blueLinkMsiShouldExecute,
                    this.blueLinkMsiAction.ToString()))
            {
                this.result = 1603;
                var message = "安装引擎没有计划执行蓝联程序文件包，已停止本次操作，避免出现安装成功但文件未更新。";
                this.engine.Log(LogLevel.Error, "BlueLink BA: refusing apply because BlueLinkMsi plan is not executable. " +
                    "Observed=" + this.blueLinkMsiPlanObserved + ", ShouldExecute=" +
                    this.blueLinkMsiShouldExecute + ", Action=" + this.blueLinkMsiAction + ".");
                if (this.command.Display == Display.None) this.CloseWindow();
                else this.window.ShowFailure(message);
                return;
            }

            var handle = IntPtr.Zero;
            if (this.window != null)
            {
                this.dispatcher.Invoke(new Action(() => handle = new WindowInteropHelper(this.window).EnsureHandle()));
            }
            this.applyInProgress = true;
            this.lastEngineActivityUtc = DateTime.UtcNow;
            this.stalledProgressReported = false;
            this.dispatcher.BeginInvoke(new Action(() => this.progressWatchdog.Start()));
            this.engine.Apply(handle);
        }

        private void OnProgress(object sender, ProgressEventArgs e)
        {
            e.Cancel = this.cancelRequested;
            var status = this.cancelRequested ? "正在取消安装…" :
                this.runtimeOnlyPlan ? "正在下载并验证 Microsoft .NET Desktop Runtime…" :
                this.currentExecutePhase ?? "正在准备新版程序文件…";
            this.ReportEngineActivity(e.OverallPercentage, status);
        }

        private void OnExecutePackageBegin(object sender, ExecutePackageBeginEventArgs e)
        {
            if (runtimeOnlyPlan) window.ShowRuntimeProgress("installing");
            var relatedBundle = this.relatedBundleVersions.ContainsKey(e.PackageId);
            this.currentExecutePhase = InstallerExecutionPolicy.GetExecutePhase(
                e.PackageId, this.runtimeOnlyPlan, this.uninstalling, relatedBundle);
            this.ReportEngineActivity(this.lastOverallPercentage, this.currentExecutePhase);
            this.engine.Log(LogLevel.Standard,
                "BlueLink BA: execute package begin " + e.PackageId + ", phase: " + this.currentExecutePhase);
        }

        private void OnExecutePackageComplete(object sender, ExecutePackageCompleteEventArgs e)
        {
            this.engine.Log(e.Status >= 0 ? LogLevel.Standard : LogLevel.Error,
                "BlueLink BA: execute package complete " + e.PackageId +
                ", status 0x" + e.Status.ToString("X8") + ".");
            if (e.Status >= 0 && e.PackageId.Equals("BlueLinkMsi", StringComparison.OrdinalIgnoreCase))
                this.currentExecutePhase = "正在验证安装结果…";
            this.ReportEngineActivity(this.lastOverallPercentage, this.currentExecutePhase);
        }

        private void OnExecuteProgress(object sender, ExecuteProgressEventArgs e)
        {
            e.Cancel = this.cancelRequested;
            var status = this.cancelRequested ? "正在取消安装…" :
                this.currentExecutePhase ?? InstallerExecutionPolicy.GetExecutePhase(
                    String.Empty, this.runtimeOnlyPlan, this.uninstalling, false);
            this.ReportEngineActivity(e.OverallPercentage, status);
        }

        private void OnError(object sender, WixToolset.Mba.Core.ErrorEventArgs e)
        {
            this.lastError = e.ErrorMessage;
            this.engine.Log(LogLevel.Error, e.ErrorMessage ?? "Unknown installer error");
        }

        private async void OnApplyComplete(object sender, ApplyCompleteEventArgs e)
        {
            this.applyInProgress = false;
            _ = this.dispatcher.BeginInvoke(new Action(() => this.progressWatchdog.Stop()));
            this.result = e.Status;

            var embeddedRelatedExecution = InstallerExecutionPolicy.IsEmbeddedRelatedExecution(
                this.command.Display == Display.Embedded,
                this.command.Relation != RelationType.None);
            if (embeddedRelatedExecution)
            {
                // An embedded BA is a child process controlled by the parent
                // Burn engine. It must never show a maintenance/completion page
                // or verify that the shared install directory is empty: the new
                // bundle may already own that directory. Return the engine result
                // and terminate the dispatcher immediately.
                this.engine.Log(LogLevel.Standard,
                    "BlueLink BA: embedded related apply complete; returning 0x" +
                    e.Status.ToString("X8") + " to the parent engine and exiting.");
                this.CloseWindow();
                return;
            }

            if (e.Status >= 0 && this.uninstalling && e.Restart != ApplyRestart.None)
            {
                this.result = 3010;
                if (command.Display == Display.None) CloseWindow();
                else window.ShowUninstallRestartRequired();
                return;
            }

            if (this.runtimeOnlyPlan)
            {
                this.runtimeOnlyPlan = false;
                if (e.Status >= 0)
                {
                    this.result = 0;
                    this.runtimeJustInstalled = true;
                    this.engine.Detect();
                }
                else
                {
                    this.window.ShowRuntimeRequired(this.runtimeVersion, this.runtimeSize,
                        (this.lastError ?? "运行时下载或安装失败。") + " 错误代码：0x" + e.Status.ToString("X8"));
                }
                return;
            }
            if (e.Status >= 0)
            {
                try
                {
                    var removed = InstallerWindow.CleanupProductRollbackFiles(this.installFolder);
                    if (removed > 0) this.engine.Log(LogLevel.Standard, "Removed " + removed + " stale BlueLink rollback file(s).");
                }
                catch (Exception failure)
                {
                    this.engine.Log(LogLevel.Error, "Unable to remove stale BlueLink rollback files: " + failure.Message);
                }
            }
            if (e.Status >= 0 && InstallerExecutionPolicy.ShouldVerifyStandaloneUninstall(
                    this.uninstalling, embeddedRelatedExecution))
            {
                string verificationError;
                if (!this.VerifyUninstallPostconditions(out verificationError))
                {
                    this.result = 1603;
                    if (this.command.Display == Display.None)
                    {
                        this.CloseWindow();
                        return;
                    }
                    this.window.ShowFailure(verificationError);
                    return;
                }
            }
            if (e.Status >= 0 && !this.uninstalling)
            {
                string verificationError;
                if (!this.VerifyInstallPostconditions(out verificationError))
                {
                    this.result = 1603;
                    this.engine.Log(LogLevel.Error, "BlueLink BA: install postcondition verification failed: " + verificationError);
                    if (this.command.Display == Display.None)
                    {
                        this.CloseWindow();
                        return;
                    }
                    this.window.ShowFailure(verificationError);
                    return;
                }

                this.ReportEngineActivity(this.lastOverallPercentage, "正在清理旧安装注册…");
                var cleanup = await Task.Run(() => this.CleanupLegacyRelatedBundles());
                if (!cleanup.Success)
                {
                    this.result = 1603;
                    this.engine.Log(LogLevel.Error,
                        "BlueLink BA: legacy bundle cleanup failed: " + cleanup.Error);
                    if (this.command.Display == Display.None)
                    {
                        this.CloseWindow();
                        return;
                    }
                    this.window.ShowFailure(cleanup.Error);
                    return;
                }
                if (!this.VerifyInstallPostconditions(out verificationError))
                {
                    this.result = 1603;
                    this.engine.Log(LogLevel.Error,
                        "BlueLink BA: legacy cleanup changed the verified payload: " + verificationError);
                    if (this.command.Display == Display.None)
                    {
                        this.CloseWindow();
                        return;
                    }
                    this.window.ShowFailure("旧安装注册清理后，新版程序文件校验失败：" + verificationError);
                    return;
                }
            }
            if (this.command.Display == Display.None)
            {
                this.CloseWindow();
                return;
            }

            if (e.Status >= 0 && this.uninstalling)
            {
                this.programRemoved = true;
                await CompleteDataRemovalAsync();
            }
            else if (e.Status >= 0) this.window.ShowCompleted(false, this.installFolder);
            else this.window.ShowFailure((this.lastError ?? "安装操作失败。") + "\n错误代码：0x" + e.Status.ToString("X8"));
        }

        private async Task CompleteDataRemovalAsync()
        {
            try
            {
                if (this.deleteSelectedUserData)
                {
                    window.ShowInstalling("正在清理选定的用户数据，保留接收文件…");
                    await Task.Run(() => UserDataCleanup.DeleteSelectedData(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), installFolder));
                }
                window.ShowCompleted(true, installFolder);
            }
            catch (Exception failure)
            {
                result = 1603;
                engine.Log(LogLevel.Error, "User data cleanup failed after uninstall: " + failure);
                window.ShowFailure("程序已移除，但所选用户数据清理未完成：" + failure.Message);
            }
        }

        private LegacyCleanupResult CleanupLegacyRelatedBundles()
        {
            foreach (var related in this.relatedBundleVersions)
            {
                if (!InstallerExecutionPolicy.ShouldCleanLegacyBundleAfterApply(
                        this.uninstalling, true, related.Value, this.firstEmbeddedSafeVersion))
                    continue;

                string executable;
                string arguments;
                string error;
                if (!TryResolveRegisteredBundle(related.Key, out executable, out arguments, out error))
                {
                    if (String.IsNullOrWhiteSpace(error)) continue;
                    return LegacyCleanupResult.Fail(error);
                }

                try
                {
                    this.engine.Log(LogLevel.Standard,
                        "BlueLink BA: starting exact legacy bundle cleanup for " + related.Key +
                        ", version " + related.Value + ".");
                    using (var process = Process.Start(new ProcessStartInfo(executable, arguments)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }))
                    {
                        if (process == null)
                            return LegacyCleanupResult.Fail("无法启动旧版蓝联的精确卸载入口，旧安装注册尚未清理。");
                        if (!process.WaitForExit(60000))
                        {
                            try { process.Kill(); } catch { }
                            return LegacyCleanupResult.Fail("清理旧版蓝联安装注册超时，已停止本次完成流程。");
                        }
                        if (process.ExitCode != 0 && process.ExitCode != 1605 && process.ExitCode != 3010)
                            this.engine.Log(LogLevel.Error,
                                "BlueLink BA: legacy cleanup process returned 0x" +
                                process.ExitCode.ToString("X8") +
                                "; verifying the exact registration and installed payload before deciding the result.");
                    }
                }
                catch (Exception failure)
                {
                    return LegacyCleanupResult.Fail("无法清理旧版蓝联安装注册：" + failure.Message);
                }

                for (var attempt = 0; attempt < 20; attempt++)
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(
                               @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + related.Key))
                    {
                        if (key == null) break;
                    }
                    Thread.Sleep(100);
                }
                using (var remaining = Registry.CurrentUser.OpenSubKey(
                           @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + related.Key))
                {
                    if (remaining != null)
                        return LegacyCleanupResult.Fail("旧版蓝联的 Burn 安装注册仍然存在，未将覆盖安装标记为成功。");
                }
            }
            return LegacyCleanupResult.Ok();
        }

        private static bool TryResolveRegisteredBundle(
            string bundleId, out string executable, out string arguments, out string error)
        {
            executable = null;
            arguments = null;
            error = null;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                           @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + bundleId))
                {
                    if (key == null) return false;
                    var quiet = key.GetValue("QuietUninstallString") as string;
                    var match = Regex.Match(quiet ?? String.Empty,
                        "^\\s*\"(?<path>[^\"]+)\"\\s*(?<args>.*)$",
                        RegexOptions.CultureInvariant);
                    if (!match.Success ||
                        match.Groups["args"].Value.IndexOf("/uninstall", StringComparison.OrdinalIgnoreCase) < 0 ||
                        match.Groups["args"].Value.IndexOf("/quiet", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        error = "旧版蓝联注册中没有可验证的静默卸载入口。";
                        return false;
                    }
                    var candidate = Path.GetFullPath(match.Groups["path"].Value);
                    var cacheRoot = Path.GetFullPath(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Package Cache")).TrimEnd('\\') + "\\";
                    if (!candidate.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase) ||
                        !File.Exists(candidate))
                    {
                        error = "旧版蓝联的缓存卸载入口缺失或不在受控的 Package Cache 中。";
                        return false;
                    }
                    executable = candidate;
                    arguments = match.Groups["args"].Value;
                    return true;
                }
            }
            catch (Exception failure)
            {
                error = "读取旧版蓝联精确卸载注册失败：" + failure.Message;
                return false;
            }
        }

        private sealed class LegacyCleanupResult
        {
            public bool Success { get; private set; }
            public string Error { get; private set; }
            public static LegacyCleanupResult Ok() => new LegacyCleanupResult { Success = true };
            public static LegacyCleanupResult Fail(string error) =>
                new LegacyCleanupResult { Success = false, Error = error };
        }

        private void ReportEngineActivity(int percentage, string phase)
        {
            this.lastEngineActivityUtc = DateTime.UtcNow;
            this.lastOverallPercentage = Math.Max(0, Math.Min(100, percentage));
            this.stalledProgressReported = false;
            this.window.SetProgress(this.lastOverallPercentage, phase);
        }

        private void OnProgressWatchdog(object sender, EventArgs e)
        {
            if (!this.applyInProgress || this.stalledProgressReported ||
                DateTime.UtcNow - this.lastEngineActivityUtc < TimeSpan.FromSeconds(45)) return;

            this.stalledProgressReported = true;
            var phase = this.currentExecutePhase ?? "正在等待安装引擎…";
            this.engine.Log(LogLevel.Standard,
                "BlueLink BA: no installer progress event for 45 seconds during phase: " + phase);
            this.window.SetProgress(this.lastOverallPercentage,
                phase + " 此阶段耗时较长，安装向导仍在等待安装引擎响应…");
        }

        private bool VerifyUninstallPostconditions(out string error)
        {
            // MSI execution has completed, but process/file notifications can trail
            // by a few hundred milliseconds. Verify the observable end state before
            // presenting success; an exit code alone is not an uninstall result.
            for (var attempt = 0; attempt < 12; attempt++)
            {
                if (!InstalledApplicationController.IsRunning(this.installFolder) &&
                    !OwnedProgramFilesRemain(this.installFolder))
                {
                    error = null;
                    return true;
                }
                Thread.Sleep(150);
            }
            if (InstalledApplicationController.IsRunning(this.installFolder))
                error = "卸载进程已结束，但蓝联仍在运行，因此未将本次操作标记为成功。";
            else
                error = "卸载进程已结束，但程序文件仍未删除，因此未将本次操作标记为成功。";
            return false;
        }

        private bool VerifyInstallPostconditions(out string error)
        {
            error = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                string registeredProductCode = null;
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(this.productRegistryKey))
                        registeredProductCode = key?.GetValue("MsiProductCode") as string;
                }
                catch (Exception failure)
                {
                    error = "无法读取安装后的 MSI 注册信息：" + failure.Message;
                }

                if (!String.IsNullOrWhiteSpace(this.expectedMsiProductCode) &&
                    String.Equals(registeredProductCode, this.expectedMsiProductCode,
                        StringComparison.OrdinalIgnoreCase) &&
                    InstallDirectoryOwnership.VerifyInstalledPayload(
                        this.installFolder,
                        InstallerExecutionPolicy.GetDisplayVersion(this.displayProductVersion),
                        this.expectedPayloadFingerprint,
                        out error))
                    return true;

                if (String.IsNullOrWhiteSpace(error))
                    error = "安装完成后的 MSI 产品标识与当前安装包不一致。程序文件未被正确替换。";
                Thread.Sleep(150);
            }
            return false;
        }

        private static bool OwnedProgramFilesRemain(string root)
        {
            if (String.IsNullOrWhiteSpace(root)) return true;
            try
            {
                var full = Path.GetFullPath(root);
                return new[]
                {
                    Path.Combine(full, "BlueLink.exe"),
                    Path.Combine(full, "BlueLink.exe.config"),
                    Path.Combine(full, "Uninstall.exe"),
                    Path.Combine(full, "Uninstall.exe.config"),
                    Path.Combine(full, ".bluelink-install.json"),
                }.Any(File.Exists) || Directory.Exists(Path.Combine(full, "app")) ||
                    Directory.Exists(Path.Combine(full, "bootstrap"));
            }
            catch { return true; }
        }

        private string GetString(string name, string fallback)
        {
            try
            {
                var value = this.engine.GetVariableString(name);
                return String.IsNullOrWhiteSpace(value) ? fallback : value;
            }
            catch { return fallback; }
        }

        private long GetNumeric(string name, long fallback)
        {
            try { return this.engine.GetVariableNumeric(name); }
            catch { return fallback; }
        }

        private string GetFormattedString(string name, string fallback)
        {
            var value = this.GetString(name, fallback);
            try { return this.engine.FormatString(value); }
            catch { return value; }
        }

        private void Cancel()
        {
            if (this.result == 0) this.result = 1602;
            this.CloseWindow();
        }

        private void CloseWindow()
        {
            this.closing = true;
            this.applyInProgress = false;
            this.dispatcher.BeginInvoke(new Action(() =>
            {
                if (this.progressWatchdog != null) this.progressWatchdog.Stop();
                if (this.window != null && this.window.IsVisible) this.window.Close();
                this.dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }));
        }
    }
}
