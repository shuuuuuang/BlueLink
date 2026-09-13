using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlueLink.SetupUI;
using BlueLink.Launcher;

internal static class Program
{
    private static int checks, images;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--runtime-architectures") return RuntimeDesktopVerification.Run(args[1]);
            if (args.Length == 2 && (args[0] == "--notices-only" || args[0] == "--desktop-notice"))
                return NoticeWindowVerification.Run(args[1], args[0] == "--desktop-notice");
            if (args.Length == 2 && args[0] == "--background-compact") return CompactInstallerScenes.Run(args[1]);
            if (args.Length == 4 && args[0] == "--recovery-read-only")
            {
                var assembly = typeof(InstallerWindow).Assembly;
                var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
                var folders = (IEnumerable<string>)assembly.GetType("BlueLink.SetupUI.MsiRelatedProductLocator")
                    .GetMethod("FindInstallFolders", flags).Invoke(null, new object[] { args[2] });
                var validate = assembly.GetType("BlueLink.Shared.InstallDirectoryOwnership")
                    .GetMethod("CanRecoverRegisteredInstall", flags);
                var allowed = folders.Any(folder => (bool)validate.Invoke(null,
                    new object[] { Path.GetFullPath(args[1]), folder, Path.GetFullPath(args[3]) }));
                Console.WriteLine("Exact MSI directory recovery eligible: " + allowed + ". Read-only; no HWND/process/registry mutations.");
                return allowed ? 0 : 1;
            }
            if (args.Length == 3 && args[0] == "--registration-read-only")
            {
                // Only the production registration reader is invoked: no window, process stop,
                // installer engine, registry write or deletion is allowed on this route.
                var config = new System.Xml.XmlDocument(); config.Load(Path.GetFullPath(args[2]));
                foreach (System.Xml.XmlElement setting in config.SelectNodes("/configuration/appSettings/add"))
                    System.Configuration.ConfigurationManager.AppSettings[setting.GetAttribute("key")] = setting.GetAttribute("value");
                var reader = typeof(BlueLink.Uninstall.UninstallWindow).GetMethod("ReadRegistration", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                reader.Invoke(null, new object[] { Path.GetFullPath(args[1]) });
                Console.WriteLine("Production uninstall registration matched the exact MSI product and installation path. Read-only; no HWND/process/registry mutations.");
                return 0;
            }
            if (args.Contains("--failure-reporting-self-test")) throw new InvalidOperationException("Expected installer verification failure.");
            if (args.Length == 2 && args[0].StartsWith("--desktop-ui-scene=", StringComparison.Ordinal))
                return DesktopScenes.Run(args[0].Substring("--desktop-ui-scene=".Length), args[1]);
            if (args.Length != 1) throw new ArgumentException("Provide a dedicated output directory.");
            var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var setup = new InstallerWindow();
            // Constructors do not run the Burn engine, enumerate processes, or open devices.
            setup.SetInstallFolder(Path.Combine(output, "isolated-location", "BlueLink"));
            setup.SetDisplayVersion("0.2.17");
            var root = Detach(setup);
            Capture(setup, root, output, "setup-welcome");
            var footer = (FrameworkElement)setup.FindName("FooterInfoText");
            var navigation = (FrameworkElement)setup.FindName("NavigationButtons");
            Check(footer.TranslatePoint(new Point(footer.ActualWidth, 0), root).X <= navigation.TranslatePoint(new Point(0, 0), root).X ||
                footer.TranslatePoint(new Point(0, footer.ActualHeight), root).Y <= navigation.TranslatePoint(new Point(0, 0), root).Y,
                "compact welcome version information does not overlap navigation buttons");
            foreach (var architecture in new[] { "x86", "x64", "arm64" })
            {
                setup.SetTargetArchitecture(architecture);
                setup.SetDisplayVersion("0.2.17");
                Capture(setup, root, output, "setup-architecture-" + architecture);
                var text = (TextBlock)setup.FindName("FooterInfoText");
                var peer = new System.Windows.Automation.Peers.TextBlockAutomationPeer(text);
                Check(peer.GetName().EndsWith(architecture, StringComparison.Ordinal), "installer automation exposes target architecture: " + architecture);
                Check(text.TranslatePoint(new Point(text.ActualWidth, 0), root).X <= navigation.TranslatePoint(new Point(0, 0), root).X ||
                    text.TranslatePoint(new Point(0, text.ActualHeight), root).Y <= navigation.TranslatePoint(new Point(0, 0), root).Y,
                    "installer architecture footer does not overlap navigation: " + architecture);
            }
            setup.SetTargetArchitecture("x64"); setup.SetDisplayVersion("0.2.17");
            setup.ShowOverwriteContext(); Capture(setup, root, output, "setup-location");
            setup.ShowLocationError("QA 目标位置不可用"); Capture(setup, root, output, "setup-location-error");
            Check(!((Button)setup.FindName("NextButton")).IsEnabled, "invalid location prevents installation");
            setup.SetInstallFolder(Path.Combine(output, "corrected-location", "BlueLink"));
            Check(((Button)setup.FindName("NextButton")).IsEnabled && ((TextBlock)setup.FindName("LocationErrorText")).Text.Length == 0,
                "editing the path clears stale validation and allows another attempt");
            setup.SetInstallFolder(Path.Combine(output, "isolated-location", "BlueLink"));
            setup.ShowInstalling("QA 正在安装新版程序文件…"); setup.SetProgress(56, "QA 安装引擎进度"); Capture(setup, root, output, "setup-progress");
            setup.ShowFailure("QA 安装失败：测试错误 0x80070643"); Capture(setup, root, output, "setup-failure");
            setup.ShowCompleted(false, Path.Combine(output, "isolated-location", "BlueLink")); Capture(setup, root, output, "setup-complete");
            foreach (var name in new[] { "CompleteInstallPathText", "CompleteDownloadPathText" })
            {
                var pathText = (TextBlock)setup.FindName(name);
                Check(pathText.ActualHeight <= 25 && pathText.ToolTip.ToString() == pathText.Text,
                    "compact completion keeps long paths on one line and exposes full path: " + name);
            }
            setup.ShowRuntimeRequired("8.0.30", "55.8 MiB"); Capture(setup, root, output, "setup-runtime-required");
            foreach (var state in new[] { "downloading", "verifying", "elevation", "installing", "completed" })
            { setup.ShowRuntimeProgress(state, 41, 100); Capture(setup, root, output, "setup-runtime-" + state); }
            setup.ShowUninstall();
            var surface = (FrameworkElement)setup.FindName("UninstallSurface");
            Capture(setup, root, output, "uninstall-options-keep");
            var checkbox = (CheckBox)surface.FindName("DeleteDataCheck");
            Check(checkbox.IsChecked == false, "uninstall defaults to preserving user data");
            checkbox.IsChecked = true;
            Check(((Button)surface.FindName("Primary")).Content.ToString() == "卸载并删除", "deletion choice changes the destructive action label");
            Capture(setup, root, output, "uninstall-options-delete");
            setup.ShowInstalling("QA 正在移除程序文件…"); setup.SetProgress(56, "QA 卸载引擎进度"); Capture(setup, root, output, "uninstall-progress");
            Check(!((Button)surface.FindName("Secondary")).IsEnabled, "uninstall cannot close while removal is in progress");
            setup.ShowCompleted(true, output); Capture(setup, root, output, "uninstall-complete-delete");
            setup.ShowUninstall(); checkbox.IsChecked = false; setup.ShowCompleted(true, output); Capture(setup, root, output, "uninstall-complete-keep");
            setup.ShowFailure("QA 无法移除被占用的文件。"); Capture(setup, root, output, "uninstall-failed");
            setup.ShowUninstallRestartRequired(); Capture(setup, root, output, "uninstall-restart-required");
            setup.ShowUninstall();
            var prompt = typeof(InstallerWindow).Assembly.GetType("BlueLink.SetupUI.InstallerPromptWindow");
            foreach (var title in new[] { "取消安装", "蓝联正在运行" })
            {
                var dialog = (Window)prompt.GetMethod("Create", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(null, new object[] { setup, title, title == "取消安装" ? "确认取消当前安装操作？" : "关闭正在运行的蓝联后继续。" });
                dialog.ApplyTemplate();
                var running = title == "蓝联正在运行";
                Capture(dialog, (FrameworkElement)dialog.Content, output, running ? "setup-confirm-running" : "setup-confirm-cancel", 460, 250);
                Check(DialogText(dialog, "PrimaryText") == (running ? "关闭并继续安装" : "确认"), "installer prompt action semantics: " + title);
                VerifyActions(dialog, running ? "关闭并继续安装" : "确认");
                ((Window)dialog).Close();
            }

            var uninstall = new BlueLink.Uninstall.UninstallWindow(Path.Combine(output, "isolated-location", "BlueLink"));
            var uninstallRoot = Detach(uninstall);
            Capture(uninstall, uninstallRoot, output, "standalone-uninstall");
            foreach (var delete in new[] { false, true })
            {
                ((CheckBox)((FrameworkElement)uninstall.FindName("UninstallSurface")).FindName("DeleteDataCheck")).IsChecked = delete;
                var dialog = (Window)typeof(BlueLink.Uninstall.UninstallWindow)
                    .GetMethod("CreateRemovalConfirmation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(uninstall, null);
                VerifyConfirmation(dialog, output, "standalone-confirm-" + (delete ? "delete" : "keep"), delete);
                var factory = typeof(InstallerWindow).Assembly.GetType("BlueLink.SetupUI.InstallerPromptWindow");
                var setupDialog = (Window)factory.GetMethod("Create", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(null, new object[] { setup, "确认卸载蓝联", DialogText(dialog, "Message") });
                VerifyConfirmation(setupDialog, output, "setup-confirm-" + (delete ? "delete" : "keep"), delete);
            }
            uninstall.Close();
            foreach (var arch in new[] { "x86", "x64", "arm64" })
            {
                setup.SetTargetArchitecture(arch);
                setup.ShowRuntimeRequired("8.0.30", "55.8 MiB");
                Capture(setup, root, output, "setup-runtime-" + arch);
                var text = Descendants<TextBlock>(root).Single(b => b.Name == "RuntimeArchitectureText");
                Check(new System.Windows.Automation.Peers.TextBlockAutomationPeer(text).GetName() == "架构：" + arch, "runtime target architecture automation name");
                var view = new RuntimeWindow(new RuntimePackageInfo { Version = "8.0.30", Size = 58510672, Rid = "win-" + arch }, new string[0]);
                var content = Detach(view);
                Capture(view, content, output, "launcher-runtime-" + arch);
                var label = Descendants<TextBlock>(content).Single(b => b.Name == "RuntimeArchitectureText");
                Check(new System.Windows.Automation.Peers.TextBlockAutomationPeer(label).GetName() == "架构：" + arch, "launcher target architecture automation name");
                view.Close();
            }
            var package = new RuntimePackageInfo { Version = "8.0.30", Size = 58510672 };
            var runtime = new RuntimeWindow(package, new string[0]);
            var runtimeRoot = Detach(runtime);
            foreach (RuntimeStage state in Enum.GetValues(typeof(RuntimeStage)))
            {
                runtime.ShowRuntimeProgress(new RuntimeProgress { Stage = state, Received = 24000000, Total = package.Size, Message = state == RuntimeStage.Failed ? "QA 文件校验失败，可以重试。" : null });
                Capture(runtime, runtimeRoot, output, "launcher-" + state.ToString().ToLowerInvariant());
            }
            runtime.Close();
            setup.Close();
            application.Shutdown();
            Console.WriteLine("Installer offscreen verification passed: " + checks + " checks; " + images + " images; all HWND = 0.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("Verification failed: " + error); return 1; }
    }
    private static string DialogText(Window dialog, string property) => (string)dialog.GetType()
        .GetProperty(property, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(dialog);

    private static void VerifyActions(Window dialog, string primaryText)
    {
        var root = (FrameworkElement)dialog.Content;
        var actions = Descendants<Wpf.Ui.Controls.Button>(root).Where(b => b.Visibility == Visibility.Visible && b.Content is string).ToArray();
        Check(actions.Length == 2 && actions.All(b => !String.IsNullOrWhiteSpace((string)b.Content)), "no empty confirmation actions");
        var cancel = actions.Single(b => b.Content as string == "取消");
        var primary = actions.Single(b => b.Content as string == primaryText);
        Check(cancel.ActualWidth >= 76 && primary.ActualWidth >= 84 && cancel.ActualHeight == 36 && primary.ActualHeight == 36, "shared action geometry");
        Check(Math.Abs(primary.TranslatePoint(new Point(), root).X - cancel.TranslatePoint(new Point(cancel.ActualWidth, 0), root).X - 8) < .5, "shared action gap");
        foreach (var pair in new[] { new[] { "NoticeTitle", "NoticeClose" }, new[] { "NoticeText", "NoticeIconTile" } })
        {
            var elements = pair.Select(name => Descendants<FrameworkElement>(root).Single(e => e.Name == name)).ToArray();
            Check(Math.Abs(elements[0].TranslatePoint(new Point(0, elements[0].ActualHeight / 2), root).Y - elements[1].TranslatePoint(new Point(0, elements[1].ActualHeight / 2), root).Y) < 1, "shared text/icon alignment");
        }
    }

    private static void VerifyConfirmation(Window dialog, string output, string name, bool delete)
    {
        var root = (FrameworkElement)dialog.Content;
        Capture(dialog, root, output, name, 460, 280);
        var title = Descendants<TextBlock>(root).Single(b => b.Name == "NoticeTitle");
        var primaryText = delete ? "卸载并删除" : "卸载";
        VerifyActions(dialog, primaryText);
        Check(title.FontSize == 16 && title.FontFamily.Source.Contains("Noto Sans SC"), "shared confirmation typography: " + name);
        Check(DialogText(dialog, "Message").Contains("Download") && DialogText(dialog, "PrimaryText") == primaryText, "confirmation preserves data-choice semantics: " + name);
        var primary = Descendants<Wpf.Ui.Controls.Button>(root).Single(b => b.Content as string == primaryText);
        Check(((SolidColorBrush)primary.Background).Color.ToString() == "#FFDB2F3D", "destructive confirmation action color: " + name);
        // Exercise only the isolated confirmation result; no uninstall handler is attached.
        primary.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check((bool)dialog.GetType().GetProperty("Confirmed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(dialog), "primary action confirms: " + name);
        dialog.Close();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
    private static FrameworkElement Detach(Window window)
    {
        var content = (FrameworkElement)window.Content; window.Content = null;
        var scope = NameScope.GetNameScope(window); if (scope != null) NameScope.SetNameScope(content, scope);
        var frame = new Border { Background = window.Background, Child = content, Resources = window.Resources, DataContext = window.DataContext };
        return frame;
    }
    private static void Capture(Window window, FrameworkElement root, string output, string name, int width = 0, int height = 0)
    {
        if (width == 0) width = window is InstallerWindow ? 800 : 1040;
        if (height == 0) height = window is InstallerWindow ? 560 : 680;
        Drain(); root.InvalidateMeasure(); root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); Drain();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root); Drain(); bitmap.Clear(); bitmap.Render(root);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(output, name + ".png"))) png.Save(file);
        Check(new WindowInteropHelper(window).Handle == IntPtr.Zero && !window.IsVisible && !window.IsActive, "no native window: " + name);
        Check(root.ActualWidth == width && root.ActualHeight == height, "layout bounds: " + name);
        images++;
    }
    private static void Drain() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; }
}

