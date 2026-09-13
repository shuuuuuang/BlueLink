namespace BlueLink.Launcher
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Net.Http;
    using System.Security.Cryptography;
    using System.Threading.Tasks;
    using System.Threading;
    using System.ComponentModel;
    using System.Windows;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;
    using Wpf.Ui.Controls;

    public partial class RuntimeWindow : FluentWindow
    {
        private readonly RuntimePackageInfo package;
        private readonly string[] originalArguments;
        private bool busy;
        private bool installing;
        private CancellationTokenSource stop;
        public string SnapshotPath { get; set; }
        public event Action RuntimeReady;
        public event Action Canceled;

        public RuntimeWindow(RuntimePackageInfo package, string[] originalArguments)
        {
            this.package = package;
            this.originalArguments = originalArguments;
            InitializeComponent();
            RuntimeArchitectureText.Text = "架构：" + package.Architecture;
            Closing += (sender, args) => { if (busy) { args.Cancel = true; if (!installing) stop?.Cancel(); } };
            PackageSizeText.Text = "预计下载：" + package.DisplaySize;
            RuntimeStatusText.Text = package.Version == "8.0.x" ? "尚未安装" : "尚未安装 · " + package.Version;
            ContentRendered += (sender, args) =>
            {
                if (String.IsNullOrWhiteSpace(SnapshotPath)) return;
                try { SaveSnapshot(SnapshotPath); }
                finally { Canceled?.Invoke(); }
            };
        }

        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            if (completed) { RuntimeReady?.Invoke(); return; }
            busy = true; stop = new CancellationTokenSource();
            InstallButton.IsEnabled = false;
            try
            {
                ShowRuntimeProgress(new RuntimeProgress { Stage = RuntimeStage.Downloading, Total = package.Size });
                using (var lease = await new RuntimeDownloadService().DownloadAsync(package, Path.Combine(Path.GetTempPath(), "BlueLink", "Runtime"), new Progress<RuntimeProgress>(ShowRuntimeProgress), stop.Token))
                {
                    stop.Token.ThrowIfCancellationRequested();
                    ShowRuntimeProgress(new RuntimeProgress { Stage = RuntimeStage.Elevation });
                    installing = true;
                    using (var process = Process.Start(new ProcessStartInfo(lease.Path, "/install /quiet /norestart") { UseShellExecute = true, Verb = "runas" }))
                    {
                        if (process == null) throw new InvalidOperationException("无法启动运行时安装程序。");
                        ShowRuntimeProgress(new RuntimeProgress { Stage = RuntimeStage.Installing });
                        await Task.Run(() => process.WaitForExit());
                        if (process.ExitCode != 0 && process.ExitCode != 3010) throw new InvalidOperationException("运行时安装失败，错误代码：" + process.ExitCode + "。");
                        if (!RuntimeDetector.IsDesktopRuntime8Installed(package.Architecture)) throw new InvalidOperationException(process.ExitCode == 3010 ? "需要重启 Windows。重启后请重新打开蓝联。" : "安装结束后仍未检测到 " + package.Architecture + " Microsoft.WindowsDesktop.App 8.x。");
                    }
                }
                completed = true;
                ShowRuntimeProgress(new RuntimeProgress { Stage = RuntimeStage.Completed });
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            { ShowRuntimeProgress(new RuntimeProgress { Stage = RuntimeStage.Failed, Message = "下载已取消，可以重试。" }); }
            catch (Exception failure)
            { ShowRuntimeProgress(new RuntimeProgress { Stage = RuntimeStage.Failed, Message = FailureMessage(failure) + " 可重试，或前往 Microsoft 官方页面手动安装。" }); }
            finally { busy = false; installing = false; stop.Dispose(); stop = null; }
        }

        private bool completed;
        public void ShowRuntimeProgress(RuntimeProgress progress)
        {
            var stage = progress.Stage;
            RuntimeHeading.Text = stage == RuntimeStage.Downloading ? "正在下载 .NET 8 桌面运行时" : stage == RuntimeStage.Verifying ? "正在验证 .NET 8 桌面运行时" : stage == RuntimeStage.Elevation ? "需要管理员权限" : stage == RuntimeStage.Installing ? "正在安装 .NET 8 桌面运行时" : stage == RuntimeStage.Completed ? "运行时已安装完成" : stage == RuntimeStage.Failed ? "运行时安装未完成" : "需要安装 .NET 8 桌面运行时";
            RuntimeDescription.Text = stage == RuntimeStage.Downloading ? "正在从 Microsoft 官方源下载安装包，请保持网络连接。" : stage == RuntimeStage.Verifying ? "正在验证 SHA-512 校验值与 Microsoft 数字签名。" : stage == RuntimeStage.Elevation ? "Windows 即将显示系统确认，用于安装 Microsoft .NET Desktop Runtime 8。" : stage == RuntimeStage.Completed ? "已检测到所需运行环境，可以继续启动蓝联。" : "根据下方状态完成运行环境安装。";
            RuntimeStatusText.Text = stage == RuntimeStage.Downloading ? "正在下载 · " + progress.Percent + "%" : stage == RuntimeStage.Completed ? "已安装" : stage == RuntimeStage.Verifying ? "正在验证" : stage == RuntimeStage.Failed ? "未完成" : stage == RuntimeStage.Required ? "尚未安装" : "正在处理";
            DownloadProgress.Visibility = stage == RuntimeStage.Required || stage == RuntimeStage.Failed ? Visibility.Collapsed : Visibility.Visible;
            DownloadProgress.IsIndeterminate = stage != RuntimeStage.Downloading && stage != RuntimeStage.Completed;
            DownloadProgress.Value = stage == RuntimeStage.Completed ? 100 : progress.Percent;
            SetStatus(stage == RuntimeStage.Failed ? InfoBarSeverity.Error : stage == RuntimeStage.Completed ? InfoBarSeverity.Success : InfoBarSeverity.Informational,
                progress.Message ?? (stage == RuntimeStage.Downloading ? "已下载 " + (progress.Received / 1048576d).ToString("0.0") + " MiB / " + (progress.Total / 1048576d).ToString("0.0") + " MiB" : RuntimeDescription.Text));
            InstallButton.Content = stage == RuntimeStage.Completed ? "继续启动蓝联" : stage == RuntimeStage.Failed ? "重试下载" : "下载并安装";
            InstallButton.IsEnabled = stage == RuntimeStage.Required || stage == RuntimeStage.Failed || stage == RuntimeStage.Completed;
            CancelButton.IsEnabled = stage != RuntimeStage.Elevation && stage != RuntimeStage.Installing;
            OfficialDownloadButton.Visibility = stage == RuntimeStage.Failed ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Redetect_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            if (RuntimeDetector.IsDesktopRuntime8Installed(package.Architecture)) RuntimeReady?.Invoke();
            else SetStatus(InfoBarSeverity.Warning, "仍未检测到 " + package.Architecture + " Microsoft .NET Desktop Runtime 8。请完成安装后重试。");
        }

        private void OfficialDownload_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo("https://dotnet.microsoft.com/download/dotnet/8.0/runtime")
            {
                UseShellExecute = true,
            });
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) { if (busy) { if (!installing) stop?.Cancel(); } else Canceled?.Invoke(); }
        protected override void OnClosed(EventArgs e) { if (!busy) Canceled?.Invoke(); base.OnClosed(e); }
        private void SetStatus(InfoBarSeverity severity, string message) { StatusInfoBar.Severity = severity; StatusInfoBar.Message = message; StatusInfoBar.IsOpen = true; }
        private static string FailureMessage(Exception failure) => failure is System.ComponentModel.Win32Exception && ((System.ComponentModel.Win32Exception)failure).NativeErrorCode == 1223
            ? "已取消系统权限请求。" : failure.Message;

        private void SaveSnapshot(string path)
        {
            UpdateLayout();
            var content = Content as FrameworkElement ?? throw new InvalidOperationException("运行时页面不可渲染。");
            var dpi = VisualTreeHelper.GetDpi(content);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!String.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }
    }
}
