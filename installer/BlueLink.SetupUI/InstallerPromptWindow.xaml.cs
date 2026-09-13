namespace BlueLink.SetupUI
{
    using System;
    using System.IO;
    using System.Windows;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;
    using System.Windows.Threading;
    using BlueLink.Installation;

    internal static class InstallerPromptWindow
    {
        public static bool Confirm(Window owner, string title, string message, string autoCancelSnapshotPath = null)
        {
            var dialog = Create(owner, title, message);
            if (!String.IsNullOrWhiteSpace(autoCancelSnapshotPath))
            {
                dialog.ContentRendered += (sender, args) =>
                {
                    var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dialog.Dispatcher)
                    {
                        Interval = TimeSpan.FromMilliseconds(750)
                    };
                    timer.Tick += (tickSender, tickArgs) =>
                    {
                        timer.Stop();
                        SaveSnapshot(owner, dialog, autoCancelSnapshotPath);
                    };
                    timer.Start();
                };
            }
            dialog.ShowDialog();
            return dialog.Confirmed;
        }

        internal static InstallerDialogWindow Create(Window owner, string title, string message) =>
            title.Equals("蓝联正在运行", StringComparison.Ordinal)
                ? new InstallerDialogWindow(title, message, owner, "关闭并继续安装", "取消")
                : InstallerConfirmationDialog.Create(owner, title, message);

        private static void SaveSnapshot(Window owner, Window dialog, string path)
        {
            owner.UpdateLayout();
            dialog.UpdateLayout();
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!String.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var ownerRoot = VisualTreeHelper.GetChildrenCount(owner) > 0
                ? VisualTreeHelper.GetChild(owner, 0) as FrameworkElement
                : null;
            var dialogRoot = VisualTreeHelper.GetChildrenCount(dialog) > 0
                ? VisualTreeHelper.GetChild(dialog, 0) as FrameworkElement
                : null;
            if (ownerRoot == null || dialogRoot == null)
                throw new InvalidOperationException("覆盖安装确认界面无法渲染。");
            ownerRoot.UpdateLayout();
            dialogRoot.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(ownerRoot);
            var width = Math.Max(1, (int)Math.Ceiling(ownerRoot.ActualWidth * dpi.DpiScaleX));
            var height = Math.Max(1, (int)Math.Ceiling(ownerRoot.ActualHeight * dpi.DpiScaleY));
            var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX,
                dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual();
            var dialogLeft = dialog.Left - owner.Left;
            var dialogTop = dialog.Top - owner.Top;
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(owner.Background ?? Brushes.White, null,
                    new Rect(0, 0, ownerRoot.ActualWidth, ownerRoot.ActualHeight));
                context.DrawRectangle(new VisualBrush(ownerRoot), null,
                    new Rect(0, 0, ownerRoot.ActualWidth, ownerRoot.ActualHeight));
                context.DrawRectangle(new VisualBrush(dialogRoot), null,
                    new Rect(dialogLeft, dialogTop, dialogRoot.ActualWidth, dialogRoot.ActualHeight));
            }
            bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }

    }
}
