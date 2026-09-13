using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlueLink.SetupUI;

internal static class CompactInstallerScenes
{
    // UI-only host: no Burn/MSI engine, process control, downloads, registry or shortcut writes.
    internal static int Run(string directory)
    {
        var output = Path.GetFullPath(directory);
        var boundary = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../../../.acceptance")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!output.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Evidence must remain below workspace .acceptance.");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        var screenshots = 0;
        InstallerWindow window = null;
        Application app = null;
        try
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            window = new InstallerWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = SystemParameters.VirtualScreenLeft - 4000, Top = SystemParameters.VirtualScreenTop - 4000,
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
            };
            window.PrepareVisualAcceptance();
            window.SourceInitialized += (_, __) =>
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000000); // WS_EX_NOACTIVATE for background UI-only QA.
            };
            var wasActivated = false;
            window.Activated += (_, __) => wasActivated = true;
            var engineRequests = 0;
            window.InstallRequested += (_, __, ___) => engineRequests++;
            window.RuntimeInstallRequested += () => engineRequests++;
            window.RemoveApplicationRequested += () => engineRequests++;
            window.SetInstallFolder(Path.Combine(output, "isolated-location", "BlueLink"));
            window.SetLogPath(Path.Combine(output, "qa-install.log"));
            window.SetDisplayVersion("0.2.17");
            window.Show(); Drain();
            var handle = new WindowInteropHelper(window).Handle;
            Check(!wasActivated && !window.IsActive, "background window opens without activation");
            var style = GetWindowLong(handle, -16);
            Check((style & 0x00040000) == 0 && (style & 0x00010000) == 0 && (style & 0x00020000) != 0,
                "native frame has neither resize border nor maximize capability and retains minimize");
            Check(window.ResizeMode == ResizeMode.CanMinimize && !((Wpf.Ui.Controls.TitleBar)window.FindName("InstallerTitleBar")).ShowMaximize,
                "official title bar hides maximize and window allows only minimize");
            VerifyNativeAutomation(handle, Check);
            Check(!wasActivated && !window.IsActive, "native UIA window inspection stays in background");
            Check(Math.Abs(window.ActualWidth - 800) < 1 && Math.Abs(window.ActualHeight - 560) < 1,
                "wizard opens at 800 by 560 DIP");
            Check(window.MinWidth == 800 && window.MaxWidth == 800 && window.MinHeight == 560 && window.MaxHeight == 560,
                "minimum and maximum bounds enforce the fixed wizard size");
            Scene("welcome", true);
            Invoke("NextButton");
            Check(((FrameworkElement)window.FindName("LocationPage")).IsVisible, "UIA next opens installation location without running installer");
            Scene("location", true);
            var inputMetrics = new List<string>();
            var input = (Wpf.Ui.Controls.TextBox)window.FindName("InstallFolderBox");
            var originalPath = input.Text;
            try
            {
                foreach (var text in new[] { "", @"D:\BlueLink", @"D:\中文安装路径\BlueLink Agjp 0123456789" })
                {
                    ((IValueProvider)UIElementAutomationPeer.CreatePeerForElement(input).GetPattern(PatternInterface.Value)).SetValue(text);
                    Drain();
                    InputControlGeometry.Verify(input, Check, inputMetrics, "installer-location");
                }
            }
            finally
            {
                File.WriteAllLines(Path.Combine(output, "input-metrics.tsv"), inputMetrics);
                input.Text = originalPath;
            }
            window.ShowLocationError("验收：所选目录不可写，请选择其他安装文件夹。"); Scene("location-error", true);
            Check(!((Button)window.FindName("NextButton")).IsEnabled, "invalid location keeps installation disabled");
            window.ShowInstalling("正在复制应用文件…"); window.SetProgress(56, "正在复制应用文件…"); Scene("progress", true);
            window.ShowFailure("安装未能完成，错误代码 0x80070643。请查看日志并重试。"); Scene("failure", true);
            window.ShowFailure(String.Concat(Enumerable.Repeat("验收错误详情：文件占用，目录不可写，请检查后重试。", 24))); Scene("long-failure", false);
            window.ShowCompleted(false, window.InstallFolder); Scene("complete", true);
            foreach (var kind in new[] { "Install", "Download" })
            {
                var tile = (Border)window.FindName("Complete" + kind + "IconBackground");
                var icon = (TextBlock)window.FindName("Complete" + kind + "Icon");
                var iconCenter = icon.TranslatePoint(new Point(icon.ActualWidth / 2, icon.ActualHeight / 2), tile);
                Check(Math.Abs(iconCenter.X - tile.ActualWidth / 2) < 1 && Math.Abs(iconCenter.Y - tile.ActualHeight / 2) < 1,
                    "completion icon is centered within its unchanged background: " + kind);
                var path = (TextBlock)window.FindName("Complete" + kind + "PathText");
                var copy = (Button)window.FindName("Complete" + kind + "CopyButton");
                Check(Math.Abs(copy.TranslatePoint(new Point(0, copy.ActualHeight / 2), path).Y - path.ActualHeight / 2) < 1,
                    "copy action aligns with the path line: " + kind);
            }
            window.ShowRuntimeRequired("8.0.30", "55.8 MiB"); Scene("runtime-required", true);
            foreach (var stage in new[] { "downloading", "verifying", "elevation", "installing", "completed" })
            { window.ShowRuntimeProgress(stage, 24000000, 58510672); Scene("runtime-" + stage, true); }
            window.ShowRuntimeRequired("8.0.30", "55.8 MiB", "下载失败，请检查网络后重试。"); Scene("runtime-failure", true);
            window.ShowUninstall(); Scene("embedded-uninstall", false);
            Check(engineRequests == 0, "no install, runtime-install or uninstall request was raised");
            Check(!wasActivated && GetForegroundWindow() != handle && !window.IsActive,
                "background acceptance never activates the wizard or takes foreground focus");
            window.ShowCompleted(false, window.InstallFolder); Invoke("FinishButton"); Drain();
            Check(!window.IsVisible, "finish button closes visual-only wizard without launching the application");
            File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
            Console.WriteLine("Compact installer background acceptance passed: " + checks.Count + " checks; " + screenshots + " screenshots; no physical input or installation engine.");
            return 0;
        }
        finally
        {
            File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
            if (window != null && window.IsVisible) window.Close();
            if (app != null) app.Shutdown();
        }

        void Check(bool value, string message)
        {
            if (!value) { File.WriteAllLines(Path.Combine(output, "checks.txt"), checks); throw new InvalidOperationException(message); }
            checks.Add(message);
        }
        void Invoke(string name)
        {
            ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement((UIElement)window.FindName(name)).GetPattern(PatternInterface.Invoke)).Invoke(); Drain();
        }
        void Scene(string name, bool shouldFit)
        {
            Drain(); window.UpdateLayout();
            var root = (FrameworkElement)window.Content;
            var scroll = (ScrollViewer)window.FindName("InstallerBodyScroll");
            scroll.ScrollToTop(); Drain();
            var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(output, name + ".png"))) png.Save(file);
            screenshots++;
            Check(window.WindowState == WindowState.Normal && Math.Abs(window.ActualWidth - 800) < 1 && Math.Abs(window.ActualHeight - 560) < 1,
                "fixed window in scene: " + name);
            if (shouldFit) Check(scroll.ScrollableHeight < 1, "normal step fits without body scrolling: " + name + "/" + scroll.ScrollableHeight);
            if (scroll.ScrollableHeight > 0)
            {
                var scrollProvider = (IScrollProvider)new ScrollViewerAutomationPeer(scroll).GetPattern(PatternInterface.Scroll);
                scrollProvider.SetScrollPercent(ScrollPatternIdentifiers.NoScroll, 100); Drain();
                Check(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1, "long content can reach the end: " + name);
            }
            foreach (var button in Descendants<Button>(root).Where(b => b.IsVisible && b.ActualWidth > 0 && b.ActualHeight > 0))
            foreach (var text in Descendants<TextBlock>(button).Where(t => t.IsVisible && !String.IsNullOrWhiteSpace(t.Text)))
            {
                if (text.TextWrapping != TextWrapping.NoWrap) continue;
                var measured = new FormattedText(text.Text, CultureInfo.CurrentCulture, text.FlowDirection,
                    new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black,
                    VisualTreeHelper.GetDpi(text).PixelsPerDip);
                Check(button.ActualHeight - button.Padding.Top - button.Padding.Bottom - button.BorderThickness.Top - button.BorderThickness.Bottom + .8 >= measured.Height,
                    "button content has full line height: " + name + "/" + text.Text);
            }
            foreach (var groupName in new[] { "NavigationButtons", "CompletionButtons", "RuntimeButtons", "FailureButtons" })
            {
                var group = (FrameworkElement)window.FindName(groupName); if (!group.IsVisible) continue;
                var bounds = new Rect(group.TranslatePoint(new Point(), root), group.RenderSize);
                Check(bounds.Left >= 0 && bounds.Right <= root.ActualWidth + .8 && bounds.Top >= 0 && bounds.Bottom <= root.ActualHeight + .8,
                    "footer actions remain fully visible: " + name);
            }
        }
    }
    private static void VerifyNativeAutomation(IntPtr handle, Action<bool, string> check)
    {
        Exception failure = null;
        var worker = new System.Threading.Thread(() =>
        {
            try
            {
                var element = AutomationElement.FromHandle(handle);
                var window = (WindowPattern)element.GetCurrentPattern(WindowPattern.Pattern);
                var transform = (TransformPattern)element.GetCurrentPattern(TransformPattern.Pattern);
                check(!window.Current.CanMaximize && window.Current.CanMinimize && !transform.Current.CanResize,
                    "native UI Automation exposes minimize but no maximize or resize");
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        worker.Start();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (worker.IsAlive)
        {
            if (timer.Elapsed.TotalSeconds > 15) throw new TimeoutException("Native UI Automation timed out.");
            Drain(); System.Threading.Thread.Sleep(5);
        }
        if (failure != null) throw failure;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        { var child = VisualTreeHelper.GetChild(root, index); if (child is T typed) yield return typed; foreach (var value in Descendants<T>(child)) yield return value; }
    }
    private static void Drain()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr handle, int index, int value);
}
