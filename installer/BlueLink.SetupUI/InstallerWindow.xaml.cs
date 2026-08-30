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

        public InstallerWindow()
        {
            EnsureWpfUiResources();
            this.InitializeComponent();
            this.InstallFolderBox.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "BlueLink");
            this.InstallFolderBox.TextChanged += (s, e) => this.UpdateFolderLabels();
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

        public string InstallFolder => this.InstallFolderBox.Text.Trim();
        public bool CreateDesktopShortcut => this.DesktopShortcutCheckBox.IsChecked == true;
        public bool AutoStart => false;

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

        public void SetDisplayVersion(string version)
        {
            var displayVersion = InstallerExecutionPolicy.GetDisplayVersion(version);
            this.Dispatcher.Invoke(() =>
                this.FooterInfoText.Text = "版本 " + displayVersion + "  ·  Windows 10/11 x64");
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
            this.Dispatcher.Invoke(() => this.ShowOnly(this.UninstallPage));
        }

        public void ShowRuntimeRequired(string version, string size, string message = null)
        {
            this.Dispatcher.Invoke(() =>
            {
                this.RuntimeStatusBadge.Text = String.IsNullOrWhiteSpace(version) ? "尚未安装" : "尚未安装 · " + version;
                this.RuntimePackageSizeText.Text = "预计下载：" + (String.IsNullOrWhiteSpace(size) ? "读取实际包大小" : size);
                this.RuntimeInfoBar.Severity = String.IsNullOrWhiteSpace(message) ? InfoBarSeverity.Informational : InfoBarSeverity.Error;
                this.RuntimeInfoBar.Message = String.IsNullOrWhiteSpace(message)
                    ? "仅安装运行环境，不会更改你的聊天记录、接收文件或蓝联设置。"
                    : message + " 可重试，或前往 Microsoft 官方页面手动安装。";
                this.RuntimeOfficialDownloadButton.Visibility = String.IsNullOrWhiteSpace(message)
                    ? Visibility.Collapsed : Visibility.Visible;
                this.ShowOnly(this.RuntimePage);
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
                    this.CompleteTitleText.Text = "蓝联已成功卸载";
                    this.LaunchAfterCheckBox.IsChecked = false;
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
                this.ShowOnly(this.FailurePage);
            });
        }

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
            foreach (var candidate in new[] { this.WelcomePage, this.LocationPage, this.RuntimePage, this.ProgressPage, this.CompletePage, this.UninstallPage, this.FailurePage })
                candidate.Visibility = candidate == page ? Visibility.Visible : Visibility.Collapsed;

            var split = page == this.WelcomePage || page == this.CompletePage;
            this.InstallerTitleBar.Visibility = Visibility.Visible;
            this.InstallerTitleBar.Opacity = 1;
            Panel.SetZIndex(this.InstallerTitleBar, 12);
            this.InstallerTitleBar.InvalidateMeasure();
            this.InstallerTitleBar.InvalidateVisual();
            this.BrandPanel.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
            this.PanelBrandHeader.Visibility = page == this.WelcomePage ? Visibility.Collapsed : Visibility.Visible;
            this.BrandColumn.Width = new GridLength(page == this.CompletePage ? 352 : 396);
            this.BrandHeroLogo.Width = page == this.CompletePage ? 390 : 330;
            this.BrandHeroLogo.Height = page == this.CompletePage ? 390 : 330;
            this.BrandHeroLogo.Margin = page == this.CompletePage
                ? new Thickness(-36, 37, 0, 0)
                : new Thickness(-23, 44, 0, -44);
            if (page == this.WelcomePage)
            {
                this.BrandHeroLogo.Width = 390;
                this.BrandHeroLogo.Height = 390;
            }
            this.BrandContentStack.Margin = page == this.CompletePage
                ? new Thickness(22, 42, 14, 30)
                : new Thickness(40, 42, 30, 38);
            Grid.SetColumn(this.ContentShell, split ? 1 : 0);
            Grid.SetColumnSpan(this.ContentShell, split ? 1 : 2);

            var navigation = page == this.WelcomePage || page == this.LocationPage || page == this.ProgressPage;
            this.NavigationButtons.Visibility = navigation ? Visibility.Visible : Visibility.Collapsed;
            this.CompletionButtons.Visibility = page == this.CompletePage ? Visibility.Visible : Visibility.Collapsed;
            this.RuntimeButtons.Visibility = page == this.RuntimePage ? Visibility.Visible : Visibility.Collapsed;
            this.UninstallButtons.Visibility = page == this.UninstallPage ? Visibility.Visible : Visibility.Collapsed;
            this.CloseButton.Visibility = page == this.FailurePage ? Visibility.Visible : Visibility.Collapsed;
            this.FooterInfoText.Visibility = page == this.WelcomePage ? Visibility.Visible : Visibility.Collapsed;
            this.BackButton.Visibility = page == this.LocationPage || page == this.ProgressPage ? Visibility.Visible : Visibility.Collapsed;
            this.BackButton.IsEnabled = page != this.ProgressPage;
            this.CancelButton.IsEnabled = true;
            this.NextButton.Visibility = page == this.ProgressPage ? Visibility.Collapsed : Visibility.Visible;
            this.NextButton.Content = page == this.LocationPage ? "安装" : "下一步";
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
                ValidateInstallDirectoryContents(fullPath);
                this.LocationErrorText.Text = String.Empty;
                return true;
            }
            catch (Exception ex)
            {
                this.LocationErrorText.Text = ex.Message;
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
                this.LocationErrorText.Text = ex.Message;
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

        private void RuntimeInstall_Click(object sender, RoutedEventArgs e) => this.RuntimeInstallRequested?.Invoke();
        private void RuntimeRedetect_Click(object sender, RoutedEventArgs e) => this.RuntimeRedetectRequested?.Invoke();
        private void RuntimeCancel_Click(object sender, RoutedEventArgs e) { this.CancelRequested?.Invoke(); this.Close(); }

        private void RemoveApplication_Click(object sender, RoutedEventArgs e)
        {
            if (this.Confirm("确认卸载蓝联", "将删除蓝联程序、快捷方式和自启动项，但会保留聊天记录、设置和接收文件。"))
                this.RemoveApplicationRequested?.Invoke();
        }

        private void Finish_Click(object sender, RoutedEventArgs e)
        {
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
            try
            {
                if (File.Exists(path) || Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch { }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else this.DragMove();
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
