using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlueLink;
using BlueLink.Domain;
using BlueLink.Appearance;
using BlueLink.Storage;

internal static class TransientNoticeVerification
{
    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        Exception? failure = null;
        var checks = 0;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            Application? app = null;
            try
            {
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                BlueLink.Session.SessionLog.DirectoryPath = Path.Combine(output, "logs");
                app = App.CreateResourceOnlyHost();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                window = new MainWindow(false, Path.Combine(output, "isolated"))
                {
                    Width = 1000, Height = 640, Left = -5000, Top = -5000,
                    WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false,
                };
                var host = (ToastHost)window.FindName("Toasts");
                var model = window.ViewModel;
                var device = new NearbyDevice("qa", "QA 手机", "00:00:00:00:00:01", PeerPlatform.Android);
                var peer = device with { Id = "qa-other", Name = "QA 另一台手机", Address = "00:00:00:00:00:02" };
                window.Show(); Drain();
                void Check(bool valid, string name) { if (!valid) throw new InvalidOperationException(name); checks++; }
                foreach (var theme in new[] { "light", "dark" })
                foreach (var language in new[] { "zh-CN", "en-US", "zh-TW" })
                {
                    AppearanceService.Apply(BlueLinkSettings.Defaults(output) with { Theme = theme, Language = language });
                    host.Items.Clear();
                    var windowsBefore = app.Windows.Count;
                    model.ReportConnectionFailure(device, new TimeoutException("RAW timeout must stay in diagnostics"), false);
                    Check(host.Items.Count == 1 && host.Items[0].Level == ToastLevel.Error && !host.Items[0].Message.Contains("RAW"), "friendly connection timeout");
                    Check(host.Items[0].Message.Contains(device.Name) && app.Windows.Count == windowsBefore, "model failure routes to existing nonmodal host");
                    Check(host.Items[0].ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(3) && host.Items[0].ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(5), "error lifetime is five seconds");
                    Drain(); window.UpdateLayout();
                    var root = (FrameworkElement)window.Content;
                    var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(root);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp));
                    using (var file = File.Create(Path.Combine(output, $"connection-{theme}-{language}.png"))) png.Save(file);
                    host.Expire(DateTimeOffset.UtcNow.AddSeconds(6));
                    Check(host.Items.Count == 0, "expired error leaves no reserved message");
                    model.ReportScanFailure(new IOException("RAW low-level failure"));
                    Check(host.Items.Count == 1 && !host.Items[0].Message.Contains("RAW"), "friendly scan failure");
                    host.Items.Clear();
                }
                var now = DateTimeOffset.UtcNow;
                model.ReportConnectionFailure(device, new TimeoutException(), true, now);
                model.ReportConnectionFailure(device, new TimeoutException(), true, now.AddSeconds(30));
                Check(host.Items.Count == 1, "automatic retry errors deduplicate per device");
                model.ReportConnectionFailure(peer, new TimeoutException(), true, now);
                Check(host.Items.Count == 2, "other devices are not suppressed");
                model.ReportConnectionFailure(device, new TimeoutException(), false, now.AddSeconds(40));
                Check(host.Items.Count == 3, "manual retry reports its result immediately");
                model.ReportConnectionFailure(device, new TimeoutException(), true, now.AddSeconds(61));
                Check(host.Items.Count == 4, "automatic error can recur after cooldown");
                host.Items.Clear();
                model.CancelConnection();
                model.ReportConnectionFailure(device, new OperationCanceledException(), false);
                Check(host.Items.Count == 0, "user cancellation is not an error toast");
                var disposal = window.DisposeAsync().AsTask();
                while (!disposal.IsCompleted) Drain();
                disposal.GetAwaiter().GetResult();
                model.ReportScanFailure(new IOException("disposed"));
                Check(host.Items.Count == 0, "disposed windows do not receive notices");
                Console.WriteLine($"Transient notice verification passed: {checks} checks, 6 themed/localized screenshots.");
            }
            catch (Exception error) { failure = error; }
            finally { window?.Close(); app?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }
    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
