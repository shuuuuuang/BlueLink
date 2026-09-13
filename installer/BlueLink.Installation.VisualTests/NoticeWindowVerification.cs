using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlueLink.SetupUI;

internal static class NoticeWindowVerification
{
    internal static int Run(string directory, bool desktop)
    {
        var output = Path.GetFullPath(directory); Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var resources = new InstallerWindow();
        var assemblies = new[] { typeof(BlueLink.Launcher.RuntimeWindow).Assembly, typeof(BlueLink.Uninstall.UninstallWindow).Assembly };
        var index = 0;
        foreach (var assembly in assemblies)
        {
            var noticeType = assembly.GetType("BlueLink.Installation.InstallerNoticeWindow");
            var title = index == 0 ? "无法启动蓝联" : "无法启动卸载程序";
            var dialog = (Window)Activator.CreateInstance(noticeType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { title, "QA 应用文件不完整，请从安装向导执行修复。", null }, null);
            if (desktop) { dialog.ShowDialog(); break; }
            dialog.WindowStartupLocation = WindowStartupLocation.Manual; dialog.Left = -5000; dialog.Top = -5000; dialog.ShowActivated = false;
            dialog.Show(); dialog.UpdateLayout(); app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var primary = Descendants<Button>(dialog).Single(b => b.Content as string == "确定");
            var face = new Typeface(primary.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (face.TryGetGlyphTypeface(out var glyph)) Console.WriteLine("Notice font: " + glyph.FontUri);
            else throw new InvalidOperationException("Bundled notice font could not be resolved: " + primary.FontFamily.Source);
            if (primary.ActualHeight != 36 || primary.ActualWidth < 84 || primary.ActualWidth > 150 || dialog.ActualWidth != 460)
                throw new InvalidOperationException("Startup notice action layout differs from the confirmation shell.");
            Func<string, double> center = name =>
            {
                var item = Descendants<FrameworkElement>(dialog).Single(x => x.Name == name);
                return item.TranslatePoint(new Point(0, item.ActualHeight / 2), dialog).Y;
            };
            if (Math.Abs(center("NoticeTitle") - center("NoticeClose")) > .8 || Math.Abs(center("NoticeIconTile") - center("NoticeText")) > .8)
                throw new InvalidOperationException("Notice text and icon centers differ.");
            var content = (FrameworkElement)dialog.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * 1.5), (int)Math.Ceiling(content.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32); bitmap.Render(content);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(output, "notice-" + index + ".png"))) png.Save(file);
            dialog.Close(); index++;
        }
        resources.Close(); app.Shutdown();
        Console.WriteLine("Startup notice verification passed: " + index + " layouts."); return 0;
    }
    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
