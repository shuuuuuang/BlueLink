namespace BlueLink.Launcher
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Net.Http;
    using System.Security.Cryptography;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;
    using Wpf.Ui.Controls;

    public partial class RuntimeWindow : FluentWindow
    {
        private readonly RuntimePackageInfo package;
        private readonly string[] originalArguments;
        private bool busy;
        public string SnapshotPath { get; set; }
        public event Action RuntimeReady;
        public event Action Canceled;

        public RuntimeWindow(RuntimePackageInfo package, string[] originalArguments)
        {
            this.package = package;
            this.originalArguments = originalArguments;
            InitializeComponent();
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
            busy = true;
            InstallButton.IsEnabled = false;
            try
            {
                if (String.IsNullOrWhiteSpace(package.Sha512) || String.IsNullOrWhiteSpace(package.Url))
                    throw new InvalidOperationException("安装器没有有效的运行时包元数据，请使用 Microsoft 官方下载入口。");
                SetStatus(InfoBarSeverity.Informational, "正在从 Microsoft 官方下载 .NET Desktop Runtime " + package.Version + "…");
                var target = Path.Combine(Path.GetTempPath(), package.FileName);
                using (var client = new HttpClient())
                using (var response = await client.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    using (var input = await response.Content.ReadAsStreamAsync())
                    using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                        await input.CopyToAsync(output);
                }
                SetStatus(InfoBarSeverity.Informational, "正在校验 SHA-512 与 Microsoft Authenticode 签名…");
                using (var sha = SHA512.Create())
                using (var input = File.OpenRead(target))
                {
                    var actual = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
                    if (!actual.Equals(package.Sha512, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("下载的运行时安装包 SHA-512 校验失败。");
                }
                AuthenticodeVerifier.VerifyMicrosoft(target);
                SetStatus(InfoBarSeverity.Informational, "正在请求系统权限并安装运行时…");
                var process = Process.Start(new ProcessStartInfo(target, "/install /quiet /norestart")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                });
                if (process == null) throw new InvalidOperationException("无法启动运行时安装程序。");
                await Task.Run(() => process.WaitForExit());
                if (process.ExitCode != 0 && process.ExitCode != 3010)
                    throw new InvalidOperationException("运行时安装失败，错误代码：" + process.ExitCode + "。");
                if (!RuntimeDetector.IsDesktopRuntime8X64Installed())
                    throw new InvalidOperationException(process.ExitCode == 3010
                        ? "运行时安装需要重启 Windows。重启后请重新打开蓝联。"
                        : "安装结束后仍未检测到 x64 Microsoft.WindowsDesktop.App 8.x。");
                SetStatus(InfoBarSeverity.Success, "运行时安装完成，正在启动蓝联…");
                RuntimeReady?.Invoke();
            }
            catch (Exception failure)
            {
                SetStatus(InfoBarSeverity.Error, FailureMessage(failure) + " 可重试，或前往 Microsoft 官方页面手动安装。");
                OfficialDownloadButton.Visibility = Visibility.Visible;
                InstallButton.IsEnabled = true;
            }
            finally { busy = false; }
        }

        private void Redetect_Click(object sender, RoutedEventArgs e)
        {
            if (RuntimeDetector.IsDesktopRuntime8X64Installed()) RuntimeReady?.Invoke();
            else SetStatus(InfoBarSeverity.Warning, "仍未检测到 x64 Microsoft .NET Desktop Runtime 8。请完成安装后重试。");
        }

        private void OfficialDownload_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo("https://dotnet.microsoft.com/download/dotnet/8.0/runtime")
            {
                UseShellExecute = true,
            });
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) { if (!busy) Canceled?.Invoke(); }
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
