using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlueLink.Launcher;
using BlueLink.SetupUI;

internal static class RuntimeDesktopVerification
{
    internal static int Run(string outputDirectory)
    {
        var root = Path.GetFullPath(Environment.CurrentDirectory);
        var output = Path.GetFullPath(outputDirectory);
        if (!File.Exists(Path.Combine(root, "VERSION")) ||
            !output.StartsWith(Path.Combine(root, ".acceptance") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use an isolated workspace .acceptance output directory.");
        Directory.CreateDirectory(output);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 0;
        application.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var checks = 0;
            try
            {
                foreach (var arch in new[] { "x86", "x64", "arm64" })
                {
                    var setup = new InstallerWindow();
                    setup.PrepareVisualAcceptance();
                    setup.SetTargetArchitecture(arch);
                    setup.ShowRuntimeRequired("8.0.30", "55.8 MiB");
                    var launcher = new RuntimeWindow(new RuntimePackageInfo { Rid = "win-" + arch, Version = "8.0.30", Size = 58510672 }, new string[0]);
                    foreach (var item in new[] { Tuple.Create<Window, string>(setup, "setup"), Tuple.Create<Window, string>(launcher, "launcher") })
                    {
                        var window = item.Item1;
                        try
                        {
                            window.Show();
                            await Task.Delay(200);
                            var handle = new WindowInteropHelper(window).Handle;
                            if (handle == IntPtr.Zero) throw new Exception("Expected a real desktop HWND.");
                            await Task.Run(() =>
                            {
                                var element = AutomationElement.FromHandle(handle);
                                var label = element.FindFirst(TreeScope.Descendants,
                                    new PropertyCondition(AutomationElement.NameProperty, "架构：" + arch));
                                var download = element.FindFirst(TreeScope.Descendants,
                                    new AndCondition(
                                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                                        new PropertyCondition(AutomationElement.NameProperty, "下载并安装")));
                                if (label == null || label.Current.IsOffscreen || label.Current.BoundingRectangle.Width <= 0 ||
                                    download == null || !download.Current.IsEnabled || download.Current.IsOffscreen)
                                    throw new Exception("Real UI Automation could not read target architecture/download action.");
                            });
                            checks += 3;
                            window.UpdateLayout();
                            var content = (FrameworkElement)window.Content;
                            var dpi = VisualTreeHelper.GetDpi(content);
                            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX),
                                (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                            bitmap.Render(content);
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using (var stream = File.Create(Path.Combine(output, item.Item2 + "-" + arch + ".png"))) encoder.Save(stream);
                        }
                        finally { window.Close(); }
                    }
                }
                Console.WriteLine("Runtime desktop UI Automation passed: " + checks + " checks; 6 screenshots; real HWNDs.");
            }
            catch (Exception error) { Console.Error.WriteLine(error); exitCode = 1; }
            finally { application.Shutdown(); }
        }));
        application.Run();
        return exitCode;
    }
}
