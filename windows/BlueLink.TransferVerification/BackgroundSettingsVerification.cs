using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlueLink;
using Button = Wpf.Ui.Controls.Button;

internal static partial class BackgroundSettingsVerification
{
    public static void Run(string outputDirectory, bool controlsOnly = false, bool inputsOnly = false, bool aboutOnly = false)
    {
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Application? app = null;
            MainWindow? window = null;
            try
            {
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                app = App.CreateResourceOnlyHost();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                app.DispatcherUnhandledException += (_, args) => { failure ??= args.Exception; args.Handled = true; };
                var foreground = GetForegroundWindow();
                window = new MainWindow(initializeRuntime: false, dataRoot: Path.Combine(output, "isolated-" + Guid.NewGuid().ToString("N")))
                {
                    Width = 1180, Height = 720, WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = SystemParameters.VirtualScreenLeft - 4000, Top = SystemParameters.VirtualScreenTop - 4000,
                    ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
                };
                Wait(window.ViewModel.InitializeLocalStateAsync());
                window.Show(); Drain();
                if (aboutOnly)
                {
                    window.Width = 1260; window.Height = 840; Drain();
                    foreach (var theme in new[] { "light", "dark" })
                    {
                        Wait(window.ViewModel.SaveSettingsAsync(window.ViewModel.Settings with { Theme = theme, Language = "zh-CN" }));
                        window.OpenSettings(); Drain();
                        var page = window.ActiveSettingsPage!;
                        Invoke((FrameworkElement)page.FindName("AboutNavigationItem"));
                        Check(((FrameworkElement)page.FindName("AboutPage")).IsVisible, "about page opened in native window: " + theme);
                        // Let the official navigation transition finish before taking the screenshot.
                        var transition = Stopwatch.StartNew();
                        while (transition.ElapsedMilliseconds < 450) { Drain(); Thread.Sleep(10); }
                        Capture((FrameworkElement)window.Content, Path.Combine(output, "about-" + theme + ".png"));
                        Check(window.TryCloseSettings(), "about page returns to existing window: " + theme);
                    }
                }
                else if (inputsOnly) VerifyInputs(window, output, Check);
                else if (controlsOnly) VerifyControls(window, output, Check);
                else
                foreach (var theme in new[] { "light", "dark" })
                foreach (var language in new[] { "zh-CN", "en-US" })
                {
                    Wait(window.ViewModel.SaveSettingsAsync(window.ViewModel.Settings with { Theme = theme, Language = language }));
                    foreach (var size in new[] { new Size(1000, 600), new Size(1260, 720) })
                    {
                        window.Width = size.Width; window.Height = size.Height; Drain();
                        window.OpenSettings(); Drain();
                        var page = window.ActiveSettingsPage!;
                        var root = (FrameworkElement)window.Content;
                        foreach (var title in new[] { "开源许可", "隐私政策", "用户协议", "帮助与反馈" })
                        {
                            Invoke((FrameworkElement)page.FindName("AboutNavigationItem"));
                            Invoke(Descendants<Button>(page).Single(b => AutomationProperties.GetName(b) == BlueLink.Localization.Strings.Get(title)));
                            Drain(); page.UpdateLayout();
                            var detail = (ScrollViewer)page.FindName("HelpDetailScroll");
                            var left = (ScrollViewer)page.FindName(title == "开源许可" ? "LicenseNavigationScroll" : "HelpNavigationScroll");
                            var header = (FrameworkElement)page.FindName("HelpPageTitle");
                            var label = $"{theme}/{language}/{size.Width}/{title}";
                            Check(((FrameworkElement)page.FindName("SettingsScroll")).Visibility == Visibility.Collapsed &&
                                  ((FrameworkElement)page.FindName("SettingsFooter")).Visibility == Visibility.Collapsed,
                                "reading workspace has no outer scrollbar or empty footer: " + label);
                            Check(detail.ActualHeight > 200 && detail.ActualHeight < page.ActualHeight &&
                                  left.ActualHeight > 200 && left.ActualHeight < page.ActualHeight,
                                "both panels have bounded independent viewports: " + label);
                            var headerTop = header.TranslatePoint(new Point(), root).Y;
                            var leftBefore = left.VerticalOffset;
                            if (detail.ScrollableHeight > 0) ScrollEnd(detail);
                            Check(Math.Abs(detail.VerticalOffset - detail.ScrollableHeight) < 1 && left.VerticalOffset == leftBefore &&
                                  Math.Abs(header.TranslatePoint(new Point(), root).Y - headerTop) < 1,
                                "right panel reaches end without moving left panel or heading: " + label);
                            if (title == "开源许可")
                            {
                                left.MaxHeight = 180; page.UpdateLayout(); Drain();
                                Check(left.ScrollableHeight > 0, "long component list overflows only its own panel: " + label);
                            }
                            var detailBefore = detail.VerticalOffset;
                            if (left.ScrollableHeight > 0) ScrollEnd(left);
                            Check(detail.VerticalOffset == detailBefore, "left scrolling leaves reading position unchanged: " + label);
                            Check(left.Padding.Right == (left.ScrollableHeight > 0 ? 14 : 0),
                                "scrollbar lane exists only when navigation overflows: " + label);
                            left.MaxHeight = double.PositiveInfinity;
                            left.ScrollToTop(); detail.ScrollToTop(); Drain();
                            if (title == "开源许可")
                            {
                                var entries = (StackPanel)page.FindName("LicenseEntries");
                                var buttons = entries.Children.OfType<Button>().ToArray();
                                Invoke(buttons[1]);
                                var selected = buttons[1];
                                var hoverKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                                selected.SetValue(hoverKey, true); Drain();
                                var color = ((SolidColorBrush)page.FindResource("SettingsSoftBlueBrush")).Color;
                                Check(((SolidColorBrush)selected.MouseOverBackground).Color == color &&
                                      Descendants<Border>(selected).Any(b => b.Background is SolidColorBrush brush && brush.Color == color),
                                    "selected license remains highlighted in actual hover template: " + label);
                                selected.SetValue(hoverKey, false);
                                buttons[0].SetValue(hoverKey, true); Drain();
                                Check(Descendants<Border>(buttons[0]).Any(b => b.Background is SolidColorBrush brush && brush.Color == color),
                                    "unselected license gains hover highlight: " + label);
                                buttons[0].SetValue(hoverKey, false);
                                var query = (Wpf.Ui.Controls.TextBox)page.FindName("LicenseQuery");
                                ((IValueProvider)UIElementAutomationPeer.CreatePeerForElement(query)!.GetPattern(PatternInterface.Value)).SetValue("BouncyCastle"); Drain();
                                Check(entries.Children.OfType<Button>().Count() == 1 && left.ScrollableHeight == 0 && left.Padding.Right == 0,
                                    "search removes unnecessary navigation scrollbar: " + label);
                                query.Text = ""; Drain();
                            }
                            if (language == "zh-CN") Capture(root, Path.Combine(output, $"{theme}-{size.Width}-{title}.png"));
                        }
                        Check(window.TryCloseSettings(), "settings navigation returns to the existing main window");
                    }
                }
                Check(GetForegroundWindow() == foreground && !window.IsActive, "native acceptance never changes foreground focus");
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (window is not null) { window.Close(); Wait(window.DisposeAsync().AsTask()); }
                app?.Shutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(inputsOnly ? 5 : 3))) throw new TimeoutException("Background settings acceptance timed out.");
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { checks, failure = failure?.ToString(), physicalInputSent = false }, new JsonSerializerOptions { WriteIndented = true }));
        if (failure is not null) throw new InvalidOperationException("Background settings acceptance failed.", failure);
        Console.WriteLine($"Background settings acceptance passed: {checks.Count} checks, native UI Automation, no physical input.");
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks.Add(label); }
    }
    private static void Invoke(FrameworkElement control)
    {
        control.UpdateLayout();
        if (control is Wpf.Ui.Controls.NavigationViewItem)
        {
            control.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Drain(); return;
        }
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(control)!.GetPattern(PatternInterface.Invoke)).Invoke(); Drain();
    }
    private static void ScrollEnd(ScrollViewer scroll)
    {
        ((IScrollProvider)new ScrollViewerAutomationPeer(scroll).GetPattern(PatternInterface.Scroll)).SetScrollPercent(ScrollPatternIdentifiers.NoScroll, 100); Drain();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Capture(FrameworkElement root, string path)
    {
        root.UpdateLayout(); Drain();
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void Wait(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted) { if (timer.Elapsed.TotalSeconds > 15) throw new TimeoutException(); Drain(); Thread.Sleep(1); }
        task.GetAwaiter().GetResult(); Drain();
    }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
