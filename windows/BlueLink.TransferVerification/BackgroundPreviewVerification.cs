using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlueLink;
using BlueLink.Appearance;
using BlueLink.Storage;
using Button = Wpf.Ui.Controls.Button;

// Opt-in native regression: production windows stay outside the virtual desktop,
// transparent and non-activating. No global mouse/keyboard input is sent.
internal static class BackgroundPreviewVerification
{
    public static void Run(string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        Exception? failure = null;
        var results = new List<object>();
        var thread = new Thread(() =>
        {
            Application? app = null;
            try
            {
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                app = App.CreateResourceOnlyHost();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                app.DispatcherUnhandledException += (_, args) => { failure ??= args.Exception; args.Handled = true; };
                var data = Path.Combine(output, "isolated-data-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(data);
                var source = Path.Combine(data, "preview.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
                    new byte[] { 220, 140, 70, 255, 0, 0, 0, 0, 0, 0, 0, 0, 200, 180, 80, 255 }, 8)));
                using (var stream = File.Create(source)) encoder.Save(stream);

                foreach (var (theme, width, height) in new[] { ("light", 1000, 700), ("dark", 720, 480) })
                foreach (var closeMethod in new[] { "UIAutomation", "EscapeBinding" })
                {
                    AppearanceService.Apply(BlueLinkSettings.Defaults(data) with { Theme = theme, Language = "zh-CN" });
                    var foreground = GetForegroundWindow();
                    var window = new ImagePreviewWindow(source, "图片预览关闭按钮后台验收.png", data)
                    {
                        Width = width, Height = height, WindowState = WindowState.Normal,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = SystemParameters.VirtualScreenLeft - 3000,
                        Top = SystemParameters.VirtualScreenTop - 3000,
                        ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
                    };
                    var rendered = false;
                    var closed = false;
                    window.ContentRendered += (_, _) => rendered = true;
                    window.Closed += (_, _) => closed = true;
                    try
                    {
                        window.Show();
                        Drain();
                        Require(rendered, "production TitleBar has completed its native hook lifecycle");
                        var handle = new WindowInteropHelper(window).Handle;
                        Require(handle != IntPtr.Zero && !window.IsActive && GetForegroundWindow() == foreground,
                            "background preview never takes foreground focus");
                        Require(window.Left + window.ActualWidth < SystemParameters.VirtualScreenLeft &&
                            window.Top + window.ActualHeight < SystemParameters.VirtualScreenTop && window.Opacity == 0,
                            "native preview is transparent and outside the entire virtual desktop");
                        var close = (Button)window.FindName("PreviewCloseButton");
                        CommandManager.InvalidateRequerySuggested();
                        Drain();
                        var escape = window.InputBindings.OfType<KeyBinding>().Single();
                        Require(close.IsEnabled && ReferenceEquals(close.CommandTarget, window.Content) &&
                            escape.Key == Key.Escape && ReferenceEquals(escape.CommandTarget, close.CommandTarget) &&
                            ((RoutedCommand)escape.Command).CanExecute(null, escape.CommandTarget),
                            "button and Escape have a working explicit command target without keyboard focus");
                        var title = (FrameworkElement)window.FindName("PreviewTitleBar");
                        var points = new[] { new Point(2, 2), new Point(16, 16), new Point(29, 29) };
                        var hitResults = points.Select(point => HitTest(handle, close.PointToScreen(point))).ToArray();
                        var caption = HitTest(handle, title.PointToScreen(new Point(400, 32)));
                        results.Add(new { theme, width, height, closeMethod, closeHitTests = hitResults, captionHitTest = caption,
                            dpi = VisualTreeHelper.GetDpi(window).DpiScaleX, foregroundUnchanged = GetForegroundWindow() == foreground });
                        File.WriteAllText(Path.Combine(output, "native-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                        Require(hitResults.All(hit => hit == 1), "close button must return HTCLIENT, observed: " + string.Join(",", hitResults));
                        Require(caption == 2, "filename/header keeps the HTCAPTION drag region");
                        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(close);
                        Require(peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider, "close exposes UI Automation Invoke");
                        if (closeMethod == "UIAutomation")
                            ((IInvokeProvider)peer!.GetPattern(PatternInterface.Invoke)).Invoke();
                        else
                            ((RoutedCommand)escape.Command).Execute(escape.CommandParameter, escape.CommandTarget);
                        Drain();
                        Require(closed, closeMethod + " closes the actual native preview");
                        Require(GetForegroundWindow() == foreground, "closing the background preview preserves foreground focus");
                    }
                    finally { window.Close(); }
                }
            }
            catch (Exception exception) { failure = exception; }
            finally { app?.Shutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) throw new TimeoutException("Background native preview verification timed out.");
        if (failure is not null) throw new InvalidOperationException("Background native preview verification failed.", failure);
        Console.WriteLine("Background native preview passed: four windows, light/wide + dark/minimum; native hit tests, UI Automation close and Escape binding; no physical input or foreground activation.");
    }

    private static int HitTest(IntPtr handle, Point point)
    {
        var x = checked((short)Math.Round(point.X));
        var y = checked((short)Math.Round(point.Y));
        return (int)SendMessage(handle, 0x0084, IntPtr.Zero, new IntPtr((y << 16) | (ushort)x));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);
}
