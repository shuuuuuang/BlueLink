namespace BlueLink.Uninstall
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Windows;

    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var smokeTest = e.Args.Any(value =>
                value.StartsWith("--ui-smoke-test=", StringComparison.OrdinalIgnoreCase));
            var detached = e.Args.Any(value =>
                value.Equals("--detached", StringComparison.OrdinalIgnoreCase));
            var installRoot = ReadArgument(e.Args, "--install-root=") ?? AppDomain.CurrentDomain.BaseDirectory;

            if (!smokeTest && !detached &&
                File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".bluelink-install.json")))
            {
                try
                {
                    StartDetachedCopy(AppDomain.CurrentDomain.BaseDirectory);
                    Shutdown(0);
                }
                catch (Exception failure)
                {
                    var dialog = new Wpf.Ui.Controls.MessageBox
                    {
                        Title = "无法启动卸载程序",
                        Content = failure.Message,
                        CloseButtonText = "关闭",
                        WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    };
                    dialog.Style = TryFindResource(typeof(Wpf.Ui.Controls.MessageBox)) as Style
                        ?? throw new InvalidOperationException("WPF UI 官方 MessageBox 样式未加载。");
                    await dialog.ShowDialogAsync();
                    Shutdown(3);
                }
                return;
            }

            var window = new UninstallWindow(Path.GetFullPath(installRoot));
            MainWindow = window;
            window.Show();
        }

        private static void StartDetachedCopy(string installRoot)
        {
            var sourceRoot = Path.GetFullPath(installRoot);
            var detachedRoot = Path.Combine(Path.GetTempPath(),
                "BlueLink-Uninstall-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(detachedRoot);

            CopyFile(Path.Combine(sourceRoot, "Uninstall.exe"),
                Path.Combine(detachedRoot, "Uninstall.exe"));
            CopyFile(Path.Combine(sourceRoot, "Uninstall.exe.config"),
                Path.Combine(detachedRoot, "Uninstall.exe.config"));
            CopyDirectory(Path.Combine(sourceRoot, "bootstrap"),
                Path.Combine(detachedRoot, "bootstrap"));

            var process = Process.Start(new ProcessStartInfo(
                Path.Combine(detachedRoot, "Uninstall.exe"),
                "--detached --install-root=" + Quote(sourceRoot))
            {
                WorkingDirectory = detachedRoot,
                UseShellExecute = false,
            });
            if (process == null) throw new InvalidOperationException("无法启动临时卸载进程。");
        }

        private static void CopyDirectory(string source, string destination)
        {
            if (!Directory.Exists(source))
                throw new DirectoryNotFoundException("卸载依赖目录不存在：" + source);
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
                CopyFile(file, Path.Combine(destination, Path.GetFileName(file)));
            foreach (var directory in Directory.GetDirectories(source))
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        private static void CopyFile(string source, string destination)
        {
            if (!File.Exists(source)) throw new FileNotFoundException("卸载文件不存在。", source);
            File.Copy(source, destination, true);
        }

        private static string ReadArgument(string[] values, string prefix)
        {
            var argument = values.FirstOrDefault(value =>
                value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return argument == null ? null : argument.Substring(prefix.Length).Trim('"');
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
