namespace BlueLink.Uninstall
{
    using System;
    using System.Configuration;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;
    using Microsoft.Win32;
    using BlueLink.Shared;
    using Wpf.Ui.Controls;

    public partial class UninstallWindow : FluentWindow
    {
        private const string DefaultBundleUpgradeCode = "{6B6EB4AB-16E2-4E54-BBBE-556061F7D4A7}";
        private const string DefaultProductRegistryKey = @"Software\BlueLink";
        private readonly string installRoot;
        private bool busy;
        private bool programRemoved;
        private string logPath;

        public UninstallWindow(string installRoot)
        {
            this.installRoot = Path.GetFullPath(installRoot);
            InitializeComponent();
            UninstallSurface.RemoveRequested += () => Remove_Click(this, null);
            UninstallSurface.CloseRequested += () => { if (!busy) Close(); };
            UninstallSurface.OpenFolderRequested += () => OpenFolder(UninstallSurface.DataDeleted ? Path.Combine(this.installRoot, "Download") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueLink"));
            UninstallSurface.OpenLogRequested += () => OpenFolder(Path.GetDirectoryName(logPath));
            Closing += (sender, args) => { if (busy) args.Cancel = true; };
            var prefix = "--ui-smoke-test=";
            var snapshot = Array.Find(Environment.GetCommandLineArgs(), value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (snapshot != null)
            {
                ContentRendered += (sender, args) =>
                {
                    SaveSnapshot(snapshot.Substring(prefix.Length));
                    Close();
                };
            }
        }

        private void SaveSnapshot(string path)
        {
            UpdateLayout();
            var root = Content as FrameworkElement ?? throw new InvalidOperationException("卸载页面不可渲染。");
            var dpi = VisualTreeHelper.GetDpi(root);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xFB, 0xFC, 0xFF)), null,
                    new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                context.DrawRectangle(new VisualBrush(root), null,
                    new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            }
            bitmap.Render(drawing);
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!String.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }

        internal BlueLink.Installation.InstallerDialogWindow CreateRemovalConfirmation() =>
            BlueLink.Installation.InstallerConfirmationDialog.Create(this, "确认卸载蓝联",
                UninstallSurface.DeleteUserData
                    ? "将卸载蓝联，并删除聊天记录、已信任设备和蓝联设置。Download 中已接收的文件仍会保留。"
                    : "将卸载蓝联程序。聊天记录、已信任设备、蓝联设置以及 Download 中已接收的文件都会保留。");

        private async void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            DialogOverlay.Visibility = Visibility.Visible;
            bool confirmed;
            try
            {
                var dialog = CreateRemovalConfirmation();
                dialog.ShowDialog();
                confirmed = dialog.Confirmed;
            }
            finally { DialogOverlay.Visibility = Visibility.Collapsed; }
            if (!confirmed) return;

            busy = true;
            UninstallSurface.ShowProgress(this.installRoot, "正在关闭蓝联并准备卸载…");
            try
            {
                if (!programRemoved)
                {
                    var registration = ReadRegistration(this.installRoot);
                    var stopped = await Task.Run(() => { string error; return InstalledApplicationController.Stop(this.installRoot, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), out error) ? null : error; });
                    if (stopped != null) throw new InvalidOperationException("无法关闭正在运行的蓝联：" + stopped);
                    var command = FindBurnUninstallCommand(registration.BundleProviderKey);
                    UninstallSurface.ShowProgress(this.installRoot, "正在删除程序文件…");
                    var exitCode = command == null ? await RunMsiFallbackAsync(registration.ProductCode) : await RunAsync(command.Item1, command.Item2);
                    if (exitCode != 0 && exitCode != 3010) throw new InvalidOperationException("卸载失败，错误代码：" + exitCode + "。");
                    if (exitCode == 3010) { UninstallSurface.ShowFailure("需要重启 Windows 才能完成卸载。用户数据清理尚未执行。", null, true); return; }
                    await VerifyUninstalledAsync(registration);
                    programRemoved = true;
                }
                if (UninstallSurface.DeleteUserData)
                {
                    UninstallSurface.ShowProgress(this.installRoot, "正在清理选定的用户数据，保留接收文件…");
                    await Task.Run(() => UserDataCleanup.DeleteSelectedData(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), this.installRoot));
                }
                UninstallSurface.ShowComplete(UninstallSurface.DeleteUserData);
            }
            catch (Exception failure)
            {
                logPath = WriteFailureLog(failure);
                UninstallSurface.ShowFailure((programRemoved ? "程序已移除，但所选用户数据清理未完成：" : "") + failure.Message, logPath);
            }
            finally { busy = false; }
        }

        private static string WriteFailureLog(Exception error)
        {
            try { var folder = Path.Combine(Path.GetTempPath(), "BlueLink", "Setup"); Directory.CreateDirectory(folder); var path = Path.Combine(folder, "Uninstall-" + Guid.NewGuid().ToString("N") + ".log"); File.WriteAllText(path, DateTimeOffset.Now.ToString("O") + Environment.NewLine + error); return path; }
            catch { return null; }
        }
        private static void OpenFolder(string path) { if (!String.IsNullOrEmpty(path) && Directory.Exists(path)) try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { } }

        private static Tuple<string, string> FindBurnUninstallCommand(string providerKey)
        {
            if (String.IsNullOrWhiteSpace(providerKey)) return null;
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (root == null) continue;
                        foreach (var name in root.GetSubKeyNames())
                        using (var key = root.OpenSubKey(name))
                        {
                            var registeredProvider = key?.GetValue("BundleProviderKey") as string;
                            if (!name.Equals(providerKey, StringComparison.OrdinalIgnoreCase) &&
                                !String.Equals(registeredProvider, providerKey, StringComparison.OrdinalIgnoreCase))
                                continue;
                            var raw = key?.GetValue("BundleUpgradeCode");
                            var match = raw is string text && text.Equals(CurrentBundleUpgradeCode, StringComparison.OrdinalIgnoreCase);
                            if (raw is string[] values) match = Array.Exists(values, value => value.Equals(CurrentBundleUpgradeCode, StringComparison.OrdinalIgnoreCase));
                            if (!match) continue;
                            var command = (key.GetValue("QuietUninstallString") ?? key.GetValue("UninstallString")) as string;
                            if (String.IsNullOrWhiteSpace(command)) continue;
                            var executable = ExtractExecutable(command);
                            if (File.Exists(executable)) return Tuple.Create(executable, "-uninstall -quiet -norestart");
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        private static async Task<int> RunMsiFallbackAsync(string productCode)
        {
            if (String.IsNullOrWhiteSpace(productCode))
                throw new InvalidOperationException("蓝联安装缓存不可用，且未找到准确的 MSI 产品注册。请重新运行同版本安装包执行修复。\n不会删除聊天记录、设置或接收文件。");
            return await RunAsync("msiexec.exe", "/x " + productCode + " /qn /norestart");
        }

        private static InstallRegistration ReadRegistration(string expectedInstallRoot)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(CurrentProductRegistryKey))
            {
                if (key == null) throw new InvalidOperationException(
                    "未找到蓝联的准确安装注册，已停止卸载以避免删除其他程序。");
                var folder = key.GetValue("InstallFolder") as string;
                var productCode = key.GetValue("MsiProductCode") as string;
                var providerKey = key.GetValue("BundleProviderKey") as string;
                Guid parsed;
                if (!Guid.TryParse(productCode, out parsed))
                    throw new InvalidOperationException("蓝联 MSI 产品标识无效，已停止卸载。");
                if (!PathsEqual(folder, expectedInstallRoot))
                    throw new InvalidOperationException("卸载入口与已注册安装目录不一致，已停止卸载。");
                return new InstallRegistration(expectedInstallRoot, parsed.ToString("B").ToUpperInvariant(), providerKey);
            }
        }

        private static async Task<int> RunAsync(string executable, string arguments)
        {
            var process = Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = true });
            if (process == null) throw new InvalidOperationException("无法启动卸载进程。");
            await Task.Run(() => process.WaitForExit());
            return process.ExitCode;
        }

        private static string ExtractExecutable(string command)
        {
            var value = command.Trim();
            if (value.StartsWith("\"")) { var end = value.IndexOf('"', 1); return end > 1 ? value.Substring(1, end - 1) : ""; }
            var space = value.IndexOf(' '); return space < 0 ? value : value.Substring(0, space);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) { if (!busy) Close(); }

        private static async Task VerifyUninstalledAsync(InstallRegistration registration)
        {
            await Task.Delay(400);
            if (InstalledApplicationController.IsRunning(registration.InstallRoot))
                throw new InvalidOperationException("卸载进程已返回成功，但蓝联仍在运行，因此未将本次操作标记为成功。");
            if (IsMsiRegistered(registration.ProductCode))
                throw new InvalidOperationException("卸载进程已返回成功，但当前 MSI 产品仍然注册，因此未将本次操作标记为成功。");
            if (!String.IsNullOrWhiteSpace(registration.BundleProviderKey) &&
                FindBurnUninstallCommand(registration.BundleProviderKey) != null)
                throw new InvalidOperationException("卸载进程已返回成功，但当前安装包仍然注册，因此未将本次操作标记为成功。");

            TryRemoveEmptyDirectory(Path.Combine(registration.InstallRoot, "app"));
            TryRemoveEmptyDirectory(Path.Combine(registration.InstallRoot, "bootstrap"));
            var remaining = new[]
            {
                Path.Combine(registration.InstallRoot, "BlueLink.exe"),
                Path.Combine(registration.InstallRoot, "BlueLink.exe.config"),
                Path.Combine(registration.InstallRoot, "Uninstall.exe"),
                Path.Combine(registration.InstallRoot, "Uninstall.exe.config"),
                Path.Combine(registration.InstallRoot, ".bluelink-install.json"),
            }.Where(File.Exists).Concat(new[]
            {
                Path.Combine(registration.InstallRoot, "app"),
                Path.Combine(registration.InstallRoot, "bootstrap"),
            }.Where(Directory.Exists)).ToArray();
            if (remaining.Length != 0)
                throw new InvalidOperationException("卸载进程已返回成功，但仍有程序文件未删除：" +
                    String.Join("；", remaining.Select(Path.GetFileName)));
        }

        private static bool IsMsiRegistered(string productCode)
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + productCode))
                    {
                        if (root != null) return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private static void TryRemoveEmptyDirectory(string directory)
        {
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory, false);
            }
            catch { }
        }

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
                    .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string CurrentProductRegistryKey =>
            ConfigurationManager.AppSettings["ProductRegistryKey"] ?? DefaultProductRegistryKey;

        private static string CurrentBundleUpgradeCode =>
            ConfigurationManager.AppSettings["BundleUpgradeCode"] ?? DefaultBundleUpgradeCode;

        private sealed class InstallRegistration
        {
            internal InstallRegistration(string installRoot, string productCode, string bundleProviderKey)
            {
                InstallRoot = installRoot;
                ProductCode = productCode;
                BundleProviderKey = bundleProviderKey;
            }

            internal string InstallRoot { get; }
            internal string ProductCode { get; }
            internal string BundleProviderKey { get; }
        }
    }
}
