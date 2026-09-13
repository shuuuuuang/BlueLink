namespace BlueLink.Launcher
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Windows;

    public partial class App : Application
    {
        public App()
        {
            DispatcherUnhandledException += (sender, args) =>
            {
                WriteFailure(args.Exception);
                args.Handled = true;
                Shutdown(3);
            };
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                base.OnStartup(e);
            var snapshot = e.Args.FirstOrDefault(value =>
                value.StartsWith("--runtime-ui-smoke-test=", StringComparison.OrdinalIgnoreCase));
            var forceRuntimePage = snapshot != null;
            if (!forceRuntimePage && RuntimeDetector.IsDesktopRuntime8X64Installed())
            {
                Shutdown(LaunchClient(e.Args));
                return;
            }

            var package = RuntimePackageInfo.Load();
            var window = new RuntimeWindow(package, e.Args);
            MainWindow = window;
            window.RuntimeReady += () => Shutdown(LaunchClient(e.Args));
            window.Canceled += () => Shutdown(1602);
            if (snapshot != null) window.SnapshotPath = snapshot.Substring("--runtime-ui-smoke-test=".Length);
                window.Show();
            }
            catch (Exception failure)
            {
                WriteFailure(failure);
                Shutdown(3);
            }
        }

        private static void WriteFailure(Exception failure)
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "bluelink-launcher-error.log"), failure.ToString()); }
            catch { }
        }

        private static int LaunchClient(string[] originalArguments)
        {
            var root = AppDomain.CurrentDomain.BaseDirectory;
            var client = Path.Combine(root, "app", "BlueLink.exe");
            if (!File.Exists(client))
            {
                new BlueLink.Installation.InstallerNoticeWindow("无法启动蓝联",
                    "蓝联应用文件不完整，请从安装向导执行修复。").ShowDialog();
                return 2;
            }
            var forwarded = originalArguments.Where(value =>
                !value.StartsWith("--runtime-ui-smoke-test=", StringComparison.OrdinalIgnoreCase)).ToArray();
            var process = Process.Start(new ProcessStartInfo(client, JoinArguments(forwarded))
            {
                WorkingDirectory = root,
                UseShellExecute = false,
            });
            if (process == null) return 2;
            if (!forwarded.Any(value => value.Equals("--startup-smoke-test", StringComparison.OrdinalIgnoreCase)))
                return 0;
            process.WaitForExit();
            return process.ExitCode;
        }

        private static string JoinArguments(string[] values) => string.Join(" ", values.Select(Quote));
        private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
