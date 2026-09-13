namespace BlueLink.SetupUI
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Input;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;
    using System.Windows.Threading;
    using BlueLink.Shared;
    using Wpf.Ui.Appearance;
    using Wpf.Ui.Controls;
    using Wpf.Ui.Markup;
    using WinForms = System.Windows.Forms;

    public partial class InstallerWindow : FluentWindow
    {
        private int pageIndex;
        private bool applying;
        private string installedFolder;
        private string runtimeStage = "required";
        private string logPath;
        private bool visualAcceptance;

        public void PrepareVisualAcceptance()
        {
            if (IsLoaded || IsVisible) throw new InvalidOperationException("Visual acceptance must be selected before showing the window.");
            visualAcceptance = true;
        }

        public InstallerWindow()
        {
            EnsureWpfUiResources();
            this.InitializeComponent();
            this.UninstallSurface.RemoveRequested += () => RemoveApplication_Click(this, null);
            this.UninstallSurface.CloseRequested += () => { if (!applying) Close(); };
            this.UninstallSurface.OpenFolderRequested += () => OpenPath(UninstallSurface.DataDeleted ? Path.Combine(installedFolder ?? InstallFolder, "Download") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueLink"));
            this.UninstallSurface.OpenLogRequested += () => { if (!String.IsNullOrWhiteSpace(logPath)) OpenPath(Path.GetDirectoryName(logPath)); };
            Closing += (sender, args) => { if (applying && !visualAcceptance) { args.Cancel = true; if (UninstallSurface.Visibility != Visibility.Visible && Confirm("取消安装", "确认取消当前安装操作？")) ApplyCancelRequested?.Invoke(); } };
            this.InstallFolderBox.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "BlueLink");
            this.InstallFolderBox.TextChanged += (s, e) => { this.UpdateFolderLabels(); ShowLocationError(null); };
            this.FallbackFolderText.Text = "若所选目录不可写，将使用 " + Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "BlueLink");
            this.UpdateFolderLabels();
            this.ShowInstallPage(0);
        }

        public event Action<string, bool, bool> InstallRequested;
        public event Action RemoveApplicationRequested;
        public event Action CancelRequested;
        public event Action ApplyCancelRequested;
        public event Action RuntimeInstallRequested;
        public event Action RuntimeRedetectRequested;
        public event Action RuntimeContinueRequested;

        internal Action<string> InstallDirectoryValidator { get; set; } = ValidateInstallDirectoryContents;

        public string InstallFolder => this.InstallFolderBox.Text.Trim();
        public void ShowLocationError(string message, string availableSpace = null)
        {
            LocationErrorText.Text = message ?? String.Empty;
            var invalid = !String.IsNullOrWhiteSpace(message);
            InstallFolderBox.BorderBrush = new SolidColorBrush(invalid ? Color.FromRgb(0xF0, 0x44, 0x44) : Color.FromRgb(0xD4, 0xDD, 0xEB));
            NextButton.IsEnabled = !invalid;
            if (availableSpace != null) AvailableSpaceText.Text = availableSpace;
            AvailableSpaceText.Foreground = new SolidColorBrush(availableSpace != null && invalid ? Color.FromRgb(0xF0, 0x44, 0x44) : Color.FromRgb(0x58, 0x69, 0x8D));
        }
        public bool CreateDesktopShortcut => this.DesktopShortcutCheckBox.IsChecked == true;
        public bool AutoStart => false;
        public bool DeleteUserData => UninstallSurface.DeleteUserData;
        public void SetLogPath(string path) { this.logPath = path; }

        public bool Confirm(string title, string message, string autoCancelSnapshotPath = null)
        {
            var result = false;
            Action show = () =>
            {
                this.DialogOverlay.Visibility = Visibility.Visible;
                try { result = InstallerPromptWindow.Confirm(this, title, message, autoCancelSnapshotPath); }
                finally { this.DialogOverlay.Visibility = Visibility.Collapsed; }
            };
            if (this.Dispatcher.CheckAccess()) show(); else this.Dispatcher.Invoke(show);
            return result;
        }

        public void SetInstallFolder(string folder)
        {
            if (String.IsNullOrWhiteSpace(folder)) return;
            this.Dispatcher.Invoke(() =>
            {
                this.InstallFolderBox.Text = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                this.InstallFolderBox.CaretIndex = this.InstallFolderBox.Text.Length;
            });
        }

        private string targetArchitecture = "x64";
        public void SetTargetArchitecture(string value) { targetArchitecture = value == "x86" || value == "arm64" ? value : "x64"; }

        public void SetDisplayVersion(string version)
        {
            var displayVersion = InstallerExecutionPolicy.GetDisplayVersion(version);
            this.Dispatcher.Invoke(() =>
                this.FooterInfoText.Text = "版本 " + displayVersion + "  ·  Windows 10/11 " + targetArchitecture);
        }

        public void ShowFreshInstall()
        {
            this.Dispatcher.Invoke(() => this.ShowInstallPage(0));
        }

        public void ShowOverwriteContext()
        {
            this.Dispatcher.Invoke(() => this.ShowInstallPage(1));
        }

        public void ShowUninstall()
        {
            this.Dispatcher.Invoke(() => { this.ShowOnly(this.UninstallPage); HeaderCaption.Text = "蓝联卸载向导"; InstallerTitleBar.Title = "蓝联卸载向导"; UninstallSurface.Visibility = Visibility.Visible; UninstallSurface.ShowOptions(); });
        }

        public void ShowRuntimeRequired(string version, string size, string message = null)
        {
            this.Dispatcher.Invoke(() =>
            {
                this.applying = false;
                this.RuntimeStatusBadge.Text = String.IsNullOrWhiteSpace(version) ? "尚未安装" : "尚未安装 · " + version;
                this.RuntimePackageSizeText.Text = "预计下载：" + (String.IsNullOrWhiteSpace(size) ? "读取实际包大小" : size);
                this.RuntimeInfoBar.Severity = String.IsNullOrWhiteSpace(message) ? InfoBarSeverity.Informational : InfoBarSeverity.Error;
                this.RuntimeInfoBar.Message = String.IsNullOrWhiteSpace(message)
                    ? "仅安装运行环境，不会更改你的聊天记录、接收文件或蓝联设置。"
                    : message + " 可重试，或前往 Microsoft 官方页面手动安装。";
                this.RuntimeOfficialDownloadButton.Visibility = String.IsNullOrWhiteSpace(message)
                    ? Visibility.Collapsed : Visibility.Visible;
                this.ShowOnly(this.RuntimePage);
                this.runtimeStage = "required";
                RuntimePrimary.IsEnabled = true;
                RuntimePrimary.Content = "下载并安装";
                RuntimeDownloadProgress.Visibility = Visibility.Collapsed;
                RuntimeHeading.Text = String.IsNullOrWhiteSpace(message) ? "需要安装 .NET 8 桌面运行时" : "运行时安装未完成";
                ApplyRuntimePresentation(String.IsNullOrWhiteSpace(message) ? "required" : "failed", 0, 0);
            });
        }

        public void ShowRuntimeProgress(string stage, long received = 0, long total = 0)
        {
            Dispatcher.Invoke(() =>
            {
                ShowOnly(RuntimePage); runtimeStage = stage;
                applying = stage != "completed";
                RuntimeHeading.Text = stage == "downloading" ? "正在下载 .NET 8 桌面运行时" : stage == "verifying" ? "正在验证 .NET 8 桌面运行时" : stage == "elevation" ? "需要管理员权限" : stage == "completed" ? "运行时已安装完成" : "正在安装 .NET 8 桌面运行时";
                RuntimeDescription.Text = stage == "downloading" ? "正在从 Microsoft 官方源下载安装包，请保持网络连接。" : stage == "verifying" ? "安装引擎正在验证下载文件的完整性与来源。" : stage == "elevation" ? "Windows 将请求系统权限以安装运行环境。" : stage == "completed" ? "已检测到所需运行环境，可以继续安装蓝联。" : "系统正在安装运行环境，请稍候。";
                var percent = total > 0 ? Math.Min(100, received * 100d / total) : 0;
                RuntimeStatusBadge.Text = stage == "downloading" && total > 0 ? "正在下载 · " + percent.ToString("0") + "%" : stage == "completed" ? "已安装" : "正在处理";
                RuntimeDownloadProgress.Visibility = Visibility.Visible;
                RuntimeDownloadProgress.IsIndeterminate = stage != "completed" && (stage != "downloading" || total <= 0);
                RuntimeDownloadProgress.Value = stage == "completed" ? 100 : percent;
                RuntimeInfoBar.Severity = stage == "completed" ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
                RuntimeInfoBar.Message = stage == "downloading" && total > 0 ? "已下载 " + (received / 1048576d).ToString("0.0") + " MiB / " + (total / 1048576d).ToString("0.0") + " MiB" : RuntimeDescription.Text;
                RuntimePrimary.Content = stage == "completed" ? "继续安装" : "正在处理";
                RuntimePrimary.IsEnabled = stage == "completed";
                ApplyRuntimePresentation(stage, received, total);
            });
        }

        private void RuntimeOfficialDownload_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo("https://dotnet.microsoft.com/download/dotnet/8.0/runtime")
            {
                UseShellExecute = true,
            });
        }

        public void ShowInstalling(string actionName)
        {
            this.applying = true;
            this.Dispatcher.Invoke(() =>
            {
                this.ProgressStatusText.Text = actionName;
                this.ProgressFolderText.Text = this.installedFolder ?? this.InstallFolder;
                if (UninstallSurface.Visibility == Visibility.Visible) { UninstallSurface.ShowProgress(this.installedFolder ?? this.InstallFolder, actionName); return; }
                this.ShowOnly(this.ProgressPage);
            });
        }

        public void SetProgress(int percentage, string status)
        {
            Action update = () =>
            {
                var value = Math.Max(0, Math.Min(100, percentage));
                this.InstallProgressBar.Value = value;
                this.ProgressPercentageText.Text = value + "%";
                this.StagePercentText.Text = value + "%";
                if (!String.IsNullOrWhiteSpace(status)) this.ProgressStatusText.Text = status;
                if (UninstallSurface.Visibility == Visibility.Visible) UninstallSurface.ShowProgress(this.installedFolder ?? this.InstallFolder, status, value);
            };
            if (this.Dispatcher.CheckAccess()) update();
            else this.Dispatcher.BeginInvoke(update);
        }

        private static void EnsureWpfUiResources()
        {
            var application = Application.Current ?? new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            var resources = application.Resources;
            if (resources.MergedDictionaries.OfType<ControlsDictionary>().Any()) return;
            resources.MergedDictionaries.Insert(0, new ThemesDictionary { Theme = ApplicationTheme.Light });
            resources.MergedDictionaries.Insert(1, new ControlsDictionary());
        }

        public void ShowCompleted(bool uninstalled, string folder)
        {
            this.applying = false;
            this.installedFolder = folder;
            this.Dispatcher.Invoke(() =>
            {
                if (uninstalled)
                {
                    UninstallSurface.Visibility = Visibility.Visible;
                    UninstallSurface.ShowComplete(DeleteUserData);
                    return;
                }
                else
                {
                    this.CompleteTitleText.Text = "蓝联安装完成";
                    this.CompleteInstallPathText.Text = folder;
                    this.CompleteDownloadPathText.Text = Path.Combine(folder, "Download");
                }
                this.ShowOnly(this.CompletePage);
            });
        }

        public void ShowFailure(string message)
        {
            this.applying = false;
            this.Dispatcher.Invoke(() =>
            {
                this.FailureText.Text = String.IsNullOrWhiteSpace(message) ? "安装程序遇到错误。请检查安装日志后重试。" : message;
                FailureLogPathText.Text = logPath ?? "安装日志尚未生成";
                FailureLogButton.IsEnabled = !String.IsNullOrWhiteSpace(logPath);
                if (UninstallSurface.Visibility == Visibility.Visible) { UninstallSurface.ShowFailure(this.FailureText.Text, logPath); return; }
                this.ShowOnly(this.FailurePage);
            });
        }

        private void ApplyRuntimePresentation(string stage, long received, long total)
        {
            var ready = stage == "completed";
            var links = stage == "required" || stage == "failed";
            RuntimeStatusBorder.Background = new SolidColorBrush(ready ? Color.FromRgb(0xE7, 0xF8, 0xEF) : Color.FromRgb(0xFF, 0xF0, 0xE4));
            RuntimeStatusBadge.Foreground = new SolidColorBrush(ready ? Color.FromRgb(0x13, 0xA6, 0x63) : Color.FromRgb(0xDA, 0x72, 0x18));
            if (stage == "verifying") RuntimeStatusBadge.Text = "正在验证";
            if (stage == "elevation") RuntimeStatusBadge.Text = "等待授权";
            RuntimeDownloadProgress.Visibility = stage == "downloading" || stage == "verifying" || stage == "installing" ? Visibility.Visible : Visibility.Collapsed;
            RuntimeLinks.Visibility = links ? Visibility.Visible : Visibility.Collapsed;
            RuntimeStageNote.Visibility = links ? Visibility.Collapsed : Visibility.Visible;
            RuntimeStageNote.Text = stage == "downloading" && total > 0 ? "已下载 " + (received / 1048576d).ToString("0.0") + " MiB / " + (total / 1048576d).ToString("0.0") + " MiB" : stage == "verifying" ? "验证完成后将请求管理员权限并继续安装" : stage == "elevation" ? "正在等待 Windows 管理员权限确认" : ready ? "运行环境已就绪" : "正在安装运行环境";
            if (stage == "downloading") RuntimeInfoBar.Message = "下载完成后将验证文件完整性与来源。";
            RuntimePrimary.Content = ready ? "继续安装" : stage == "downloading" ? "下载中" : stage == "verifying" ? "验证中" : stage == "elevation" ? "等待确认" : stage == "installing" ? "安装中" : "下载并安装";
        }

        public void ShowUninstallRestartRequired() => Dispatcher.Invoke(() =>
        {
            applying = false;
            UninstallSurface.Visibility = Visibility.Visible;
            UninstallSurface.ShowFailure("需要重启 Windows 才能完成文件清理。用户数据清理尚未执行。", null, true);
        });

        public void CapturePage(string page, string outputPath)
        {
            this.Dispatcher.Invoke(() =>
            {
                switch ((page ?? String.Empty).Trim().ToLowerInvariant())
                {
                    case "location":
                        this.ShowInstallPage(0);
                        this.Show();
                        this.UpdateLayout();
                        this.WaitForRenderCycle();
                        this.ShowInstallPage(1);
                        break;
                    case "progress": this.ShowInstalling("正在安装新版程序文件…"); this.SetProgress(56, "正在安装新版程序文件…"); break;
                    case "complete": this.ShowCompleted(false, this.InstallFolder); break;
                    case "uninstall": this.ShowUninstall(); break;
                    case "runtime": this.ShowRuntimeRequired("8.0.30", "55.0 MB"); break;
                    default: this.ShowInstallPage(0); break;
                }

                this.Show();
                this.UpdateLayout();
                this.WaitForRenderCycle();
                if (this.Template == null || VisualTreeHelper.GetChildrenCount(this) == 0)
                    throw new InvalidOperationException("安装向导窗口模板未加载。");

                FrameworkElement visual = this;
                if (visual.ActualWidth < 2 || visual.ActualHeight < 2)
                {
                    var requestedSize = new Size(
                        Math.Max(1, this.ActualWidth > 1 ? this.ActualWidth : this.Width),
                        Math.Max(1, this.ActualHeight > 1 ? this.ActualHeight : this.Height));
                    visual.Measure(requestedSize);
                    visual.Arrange(new Rect(requestedSize));
                }
                visual.UpdateLayout();
                var dpi = VisualTreeHelper.GetDpi(visual);
                var bitmap = new RenderTargetBitmap(
                    Math.Max(1, (int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX)),
                    Math.Max(1, (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY)),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen())
                {
                    context.DrawRectangle(this.Background ?? Brushes.White, null,
                        new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
                    context.DrawRectangle(new VisualBrush(visual), null,
                        new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
                }
                bitmap.Render(drawing);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(outputPath)) encoder.Save(stream);
            });
        }

        private void WaitForRenderCycle()
        {
                var renderFrame = new DispatcherFrame();
                var renderTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, this.Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(350)
                };
                renderTimer.Tick += (sender, args) =>
                {
                    renderTimer.Stop();
                    renderFrame.Continue = false;
                };
                renderTimer.Start();
                Dispatcher.PushFrame(renderFrame);
        }

        private void ShowInstallPage(int index)
        {
            this.pageIndex = Math.Max(0, Math.Min(1, index));
            this.ShowOnly(this.pageIndex == 0 ? this.WelcomePage : this.LocationPage);
        }

        private void ShowOnly(FrameworkElement page)
        {
            HeaderCaption.Text = "蓝联安装向导"; InstallerTitleBar.Title = "蓝联安装向导";
            UninstallSurface.Visibility = Visibility.Collapsed;
            foreach (var candidate in new[] { this.WelcomePage, this.LocationPage, this.RuntimePage, this.ProgressPage, this.CompletePage, this.UninstallPage, this.FailurePage })
                candidate.Visibility = candidate == page ? Visibility.Visible : Visibility.Collapsed;

            var split = page == this.WelcomePage || page == this.CompletePage;
            this.InstallerTitleBar.Visibility = Visibility.Visible;
            this.InstallerTitleBar.Opacity = 1;
            Panel.SetZIndex(this.InstallerTitleBar, 12);
            this.InstallerTitleBar.InvalidateMeasure();
            this.InstallerTitleBar.InvalidateVisual();
            this.BrandPanel.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
            this.PanelBrandHeader.Visibility = Visibility.Collapsed;
            this.BrandColumn.Width = new GridLength(page == this.CompletePage ? 260 : 280);
            this.BrandHeroLogo.Width = this.BrandHeroLogo.Height = 250;
            this.BrandHeroLogo.Margin = new Thickness(0, 42, 0, 0);
            this.BrandContentStack.Margin = new Thickness(0);
            BrandName.Margin = new Thickness(0, 24, 0, 0);
            Grid.SetColumn(this.ContentShell, split ? 1 : 0);
            Grid.SetColumnSpan(this.ContentShell, split ? 1 : 2);

            var navigation = page == this.WelcomePage || page == this.LocationPage || page == this.ProgressPage;
            this.NavigationButtons.Visibility = navigation ? Visibility.Visible : Visibility.Collapsed;
            this.CompletionButtons.Visibility = page == this.CompletePage ? Visibility.Visible : Visibility.Collapsed;
            this.RuntimeButtons.Visibility = page == this.RuntimePage ? Visibility.Visible : Visibility.Collapsed;
            this.UninstallButtons.Visibility = page == this.UninstallPage ? Visibility.Visible : Visibility.Collapsed;
            this.FailureButtons.Visibility = page == this.FailurePage ? Visibility.Visible : Visibility.Collapsed;
            this.FooterInfoText.Visibility = page == this.WelcomePage ? Visibility.Visible : Visibility.Collapsed;
            this.BackButton.Visibility = page == this.LocationPage ? Visibility.Visible : Visibility.Collapsed;
            this.BackButton.IsEnabled = page != this.ProgressPage;
            this.CancelButton.IsEnabled = true;
            this.NextButton.Visibility = page == this.ProgressPage ? Visibility.Collapsed : Visibility.Visible;
            this.NextButton.Content = page == this.LocationPage ? "安装" : "下一步";
            InstallerBodyScroll.ScrollToTop();
            FooterInfoText.Margin = new Thickness(0);
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (this.pageIndex == 0)
            {
                this.ShowInstallPage(1);
                return;
            }
            if (!this.NormalizeInstallFolderBox()) return;
            if (!this.ValidateInstallFolder()) return;
            this.installedFolder = this.InstallFolder;
            this.InstallRequested?.Invoke(this.InstallFolder, this.CreateDesktopShortcut, this.AutoStart);
        }

        private void Back_Click(object sender, RoutedEventArgs e) => this.ShowInstallPage(this.pageIndex - 1);

        private bool ValidateInstallFolder()
        {
            try
            {
                var fullPath = Path.GetFullPath(this.InstallFolder);
                if (String.IsNullOrWhiteSpace(this.InstallFolder) || Path.GetPathRoot(fullPath) == fullPath)
                    throw new InvalidOperationException("请选择具体的安装文件夹，不能使用磁盘根目录。");
                var root = Path.GetPathRoot(fullPath);
                var drive = new DriveInfo(root);
                if (drive.IsReady && drive.AvailableFreeSpace < 256L * 1024L * 1024L)
                    throw new InvalidOperationException("目标磁盘可用空间不足 256 MiB。请选择其他位置。");
                this.InstallDirectoryValidator(fullPath);
                this.ShowLocationError(null);
                return true;
            }
            catch (Exception ex)
            {
                this.ShowLocationError(ex.Message);
                return false;
            }
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new WinForms.FolderBrowserDialog { Description = "选择蓝联安装文件夹", SelectedPath = this.InstallFolder, ShowNewFolderButton = true })
            {
                if (dialog.ShowDialog() == WinForms.DialogResult.OK)
                {
                    this.InstallFolderBox.Text = NormalizeInstallFolder(dialog.SelectedPath);
                    this.InstallFolderBox.CaretIndex = this.InstallFolderBox.Text.Length;
                }
            }
        }

        private bool NormalizeInstallFolderBox()
        {
            try
            {
                this.InstallFolderBox.Text = NormalizeInstallFolder(this.InstallFolderBox.Text);
                this.InstallFolderBox.CaretIndex = this.InstallFolderBox.Text.Length;
                return true;
            }
            catch (Exception ex)
            {
                this.ShowLocationError(ex.Message);
                return false;
            }
        }

        internal static string NormalizeInstallFolder(string selectedPath)
        {
            if (String.IsNullOrWhiteSpace(selectedPath))
                throw new InvalidOperationException("请选择蓝联的安装文件夹。");

            var fullPath = Path.GetFullPath(selectedPath.Trim());
            var directory = new DirectoryInfo(fullPath);
            return directory.Name.Equals("BlueLink", StringComparison.OrdinalIgnoreCase)
                ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : Path.Combine(fullPath, "BlueLink");
        }

        internal static void ValidateInstallDirectoryContents(string folder)
        {
            InstallDirectoryOwnership.ValidateInstallable(folder);
        }

        internal static bool IsBlueLinkExecutable(string path)
        {
            return InstallDirectoryOwnership.IsBlueLinkExecutable(path);
        }

        internal static int CleanupProductRollbackFiles(string folder)
        {
            if (!Directory.Exists(folder)) return 0;
            var rollbackFiles = new DirectoryInfo(folder)
                .EnumerateFiles("*.rbf", SearchOption.TopDirectoryOnly)
                .Where(file => InstallDirectoryOwnership.IsProductRollbackFile(file))
                .ToArray();
            foreach (var rollbackFile in rollbackFiles) rollbackFile.Delete();
            return rollbackFiles.Length;
        }

        private void UpdateFolderLabels()
        {
            try
            {
                var fullPath = Path.GetFullPath(this.InstallFolderBox.Text);
                var drive = new DriveInfo(Path.GetPathRoot(fullPath));
                this.AvailableSpaceText.Text = drive.IsReady
                    ? "可用空间 " + (drive.AvailableFreeSpace / 1024d / 1024d / 1024d).ToString("0.#") + " GiB"
                    : "可用空间未知";
            }
            catch { this.AvailableSpaceText.Text = "可用空间未知"; }
        }

        private void RuntimeInstall_Click(object sender, RoutedEventArgs e) { if (runtimeStage == "completed") RuntimeContinueRequested?.Invoke(); else if (!applying) RuntimeInstallRequested?.Invoke(); }
        private void RuntimeRedetect_Click(object sender, RoutedEventArgs e) { if (!applying) this.RuntimeRedetectRequested?.Invoke(); }
        private void RuntimeCancel_Click(object sender, RoutedEventArgs e) { if (applying) ApplyCancelRequested?.Invoke(); else { this.CancelRequested?.Invoke(); this.Close(); } }

        private void RemoveApplication_Click(object sender, RoutedEventArgs e)
        {
            if (this.Confirm("确认卸载蓝联", DeleteUserData ? "将卸载蓝联，并删除聊天记录、已信任设备和蓝联设置。Download 中已接收的文件仍会保留。" : "将卸载蓝联程序。聊天记录、已信任设备、蓝联设置以及 Download 中已接收的文件都会保留。"))
                this.RemoveApplicationRequested?.Invoke();
        }

        private void Finish_Click(object sender, RoutedEventArgs e)
        {
            if (visualAcceptance) { Close(); return; }
            if (this.DesktopShortcutCheckBox.IsChecked != true)
            {
                try
                {
                    var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "蓝联 BlueLink.lnk");
                    if (File.Exists(shortcut)) File.Delete(shortcut);
                }
                catch { }
            }
            if (this.LaunchAfterCheckBox.IsChecked == true) this.OpenPath(Path.Combine(this.installedFolder ?? this.InstallFolder, "BlueLink.exe"));
            this.Close();
        }

        private void OpenDownload_Click(object sender, RoutedEventArgs e) =>
            this.OpenPath(Path.Combine(this.installedFolder ?? this.InstallFolder, "Download"));

        private void RetryInstall_Click(object sender, RoutedEventArgs e) => ShowOverwriteContext();
        private void OpenFailureLog_Click(object sender, RoutedEventArgs e)
        { if (!String.IsNullOrWhiteSpace(logPath)) OpenPath(Path.GetDirectoryName(logPath)); }

        private void CopyInstallPath_Click(object sender, RoutedEventArgs e) => this.CopyPath(this.installedFolder ?? this.InstallFolder);

        private void CopyDownloadPath_Click(object sender, RoutedEventArgs e) => this.CopyPath(Path.Combine(this.installedFolder ?? this.InstallFolder, "Download"));

        private void CopyPath(string path)
        {
            try
            {
                Clipboard.SetText(path);
                this.FooterInfoText.Text = "路径已复制";
                this.FooterInfoText.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private void OpenPath(string path)
        {
            if (visualAcceptance) return;
            try
            {
                if (File.Exists(path) || Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch { }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => this.WindowState = WindowState.Minimized;

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (this.applying)
            {
                if (this.Confirm("取消安装", "确认取消当前安装操作？"))
                {
                    this.ProgressStatusText.Text = "正在取消安装…";
                    this.CancelButton.IsEnabled = false;
                    this.ApplyCancelRequested?.Invoke();
                }
                return;
            }
            this.CancelRequested?.Invoke();
            this.Close();
        }
    }
}
