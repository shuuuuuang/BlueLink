namespace BlueLink.SetupUI
{
    using System;
    using System.IO;
    using System.Windows;
    using System.Windows.Automation;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Windows.Media.Imaging;
    using System.Windows.Threading;
    using System.Threading.Tasks;
    using Wpf.Ui.Controls;

    internal static class InstallerPromptWindow
    {
        public static bool Confirm(Window owner, string title, string message, string autoCancelSnapshotPath = null)
        {
            Exception automationFailure = null;
            var overwrite = title.Equals("蓝联正在运行", StringComparison.Ordinal);
            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Owner = owner,
                Title = overwrite ? String.Empty : title,
                Content = BuildContent(message, overwrite),
                ShowTitle = !overwrite,
                PrimaryButtonText = overwrite ? "取消" : "确认",
                SecondaryButtonText = overwrite ? "关闭并继续安装" : String.Empty,
                CloseButtonText = overwrite ? String.Empty : "取消",
                PrimaryButtonAppearance = overwrite ? ControlAppearance.Secondary : ControlAppearance.Caution,
                SecondaryButtonAppearance = overwrite ? ControlAppearance.Primary : ControlAppearance.Caution,
                IsSecondaryButtonEnabled = overwrite,
                IsCloseButtonEnabled = !overwrite,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = overwrite ? 600 : 560,
                Height = overwrite ? 333 : 260,
                MinWidth = 480,
                MinHeight = 220
            };
            var officialStyle = Application.Current?.TryFindResource(typeof(Wpf.Ui.Controls.MessageBox)) as Style;
            if (officialStyle == null)
                throw new InvalidOperationException("WPF UI 官方 MessageBox 样式未加载。");
            dialog.Style = officialStyle;
            if (overwrite)
            {
                dialog.Loaded += (sender, args) => PrepareOverwriteVisuals(dialog);
            }
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
                        var dialogHandle = new System.Windows.Interop.WindowInteropHelper(dialog).Handle;
                        // InvokePattern synchronously marshals to the target UI
                        // thread. Calling it from this DispatcherTimer tick would
                        // ask the dialog thread to invoke itself and deadlock.
                        // Run UI Automation from a worker while the real dialog
                        // dispatcher remains free to process the visible click.
                        Task.Run(() =>
                        {
                            try { InvokeVisibleButton(dialogHandle, "取消"); }
                            catch (Exception failure)
                            {
                                automationFailure = failure;
                                try
                                {
                                    File.WriteAllText(autoCancelSnapshotPath + ".uia-error.txt",
                                        failure.ToString());
                                }
                                catch { }
                                dialog.Dispatcher.BeginInvoke(new Action(() =>
                                    ((Window)dialog).Close()));
                            }
                        });
                    };
                    timer.Start();
                };
            }
            var result = dialog.ShowDialogAsync().GetAwaiter().GetResult();
            if (automationFailure != null)
                throw new InvalidOperationException("覆盖安装弹窗 UI Automation 点击失败。", automationFailure);
            return overwrite
                ? result == Wpf.Ui.Controls.MessageBoxResult.Secondary
                : result == Wpf.Ui.Controls.MessageBoxResult.Primary;
        }

        private static FrameworkElement BuildContent(string message, bool overwrite)
        {
            if (!overwrite)
            {
                return new System.Windows.Controls.TextBlock
                {
                    Text = message,
                    FontFamily = new FontFamily("Microsoft YaHei UI"),
                    FontSize = 15,
                    LineHeight = 24,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                };
            }

            var content = new Grid { Margin = new Thickness(18, -35, 18, 0) };
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var heading = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0)
            };
            heading.Children.Add(new Border
            {
                Width = 52,
                Height = 52,
                CornerRadius = new CornerRadius(26),
                Background = new SolidColorBrush(Color.FromRgb(0xE7, 0xEF, 0xFF)),
                Child = new SymbolIcon
                {
                    Symbol = SymbolRegular.Info24,
                    FontSize = 26,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x09, 0x69, 0xF5))
                }
            });
            heading.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "蓝联正在运行",
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x1B, 0x39)),
                Margin = new Thickness(18, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetRow(heading, 0);
            content.Children.Add(heading);

            var body = new System.Windows.Controls.TextBlock
            {
                Text = "覆盖安装需要先关闭正在运行的蓝联。确认后，安装向导将关闭应用并继续安装。",
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 15,
                LineHeight = 24,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x20, 0x28, 0x36)),
                Margin = new Thickness(0, 19, 0, 0)
            };
            Grid.SetRow(body, 1);
            content.Children.Add(body);

            var notice = new System.Windows.Controls.TextBlock
            {
                Text = "正在进行的聊天或文件传输将会中断。",
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0x75, 0x81, 0x94)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            };
            Grid.SetRow(notice, 2);
            content.Children.Add(notice);
            return content;
        }

        private static void PrepareOverwriteVisuals(Wpf.Ui.Controls.MessageBox dialog)
        {
            dialog.ApplyTemplate();
            // WPF UI 4.3 exposes ShowTitle, but its official net472 template does
            // not consume that property.  Keep the official template and hide
            // only the duplicated title presenter; the business heading remains
            // in Content, matching the reviewed prototype.
            var titlePresenter = dialog.Template?.FindName("Title", dialog) as UIElement;
            if (titlePresenter != null) titlePresenter.Visibility = Visibility.Collapsed;

            Grid buttonGrid = null;
            foreach (var button in FindVisualChildren<Wpf.Ui.Controls.Button>(dialog))
            {
                var text = button.Content as string;
                if (String.Equals(text, "取消", StringComparison.Ordinal))
                {
                    buttonGrid = button.Parent as Grid;
                }
                else if (String.Equals(text, "关闭并继续安装", StringComparison.Ordinal))
                {
                    buttonGrid = button.Parent as Grid ?? buttonGrid;
                }
            }
            if (buttonGrid != null && buttonGrid.ColumnDefinitions.Count >= 5)
            {
                buttonGrid.Width = 232;
                buttonGrid.HorizontalAlignment = HorizontalAlignment.Right;
                buttonGrid.ColumnDefinitions[0].Width = new GridLength(94);
                buttonGrid.ColumnDefinitions[1].Width = new GridLength(16);
                buttonGrid.ColumnDefinitions[2].Width = new GridLength(122);
                buttonGrid.ColumnDefinitions[3].Width = new GridLength(0);
                buttonGrid.ColumnDefinitions[4].Width = new GridLength(0);
            }

            dialog.MinWidth = 500;
            dialog.MaxWidth = 500;
            dialog.Width = 500;
            dialog.Height = 263;
            dialog.Padding = new Thickness(33, 20, 33, 20);
            dialog.UpdateLayout();
            var ownerWidth = dialog.Owner.ActualWidth > 0 ? dialog.Owner.ActualWidth : dialog.Owner.Width;
            var ownerHeight = dialog.Owner.ActualHeight > 0 ? dialog.Owner.ActualHeight : dialog.Owner.Height;
            dialog.Left = dialog.Owner.Left + ((ownerWidth - dialog.Width) / 2);
            dialog.Top = dialog.Owner.Top + ((ownerHeight - dialog.Height) / 2) - 6;
        }

        private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
            where T : DependencyObject
        {
            if (parent == null) yield break;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                var match = child as T;
                if (match != null) yield return match;
                foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
            }
        }

        private static void InvokeVisibleButton(IntPtr handle, string buttonName)
        {
            var root = AutomationElement.FromHandle(handle);
            var button = root?.FindFirst(TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, buttonName)));
            if (button == null || !button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                throw new InvalidOperationException("UI Automation 未找到可见的‘" + buttonName + "’按钮。");
            ((InvokePattern)pattern).Invoke();
        }

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
