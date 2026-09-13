using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BlueLink;
using Button = Wpf.Ui.Controls.Button;

internal static partial class BackgroundSettingsVerification
{
    private static void VerifySettingsReview(MainWindow window, string output, Action<bool, string> check)
    {
        var hoverKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        foreach (var theme in new[] { "light", "dark" })
        {
            Wait(window.ViewModel.SaveSettingsAsync(window.ViewModel.Settings with { Theme = theme, Language = "zh-CN" }));
            window.Width = 1000; window.Height = 600;
            window.OpenSettings(); Drain();
            var page = window.ActiveSettingsPage!;
            var root = (FrameworkElement)window.Content;
            Invoke((FrameworkElement)page.FindName("PrivacyNavigationItem"));
            check(((FrameworkElement)page.FindName("IdentityDetailsPanel")).IsVisible &&
                  !Descendants<Button>(page).Any(b => b.Content is string text && text == "设备身份与安全"),
                "identity details remain directly accessible without the removed reveal button: " + theme);
            ((FrameworkElement)page.FindName("IdentityDetailsPanel")).BringIntoView(); Drain();
            Capture(root, Path.Combine(output, theme + "-identity.png"));

            Invoke((FrameworkElement)page.FindName("AboutNavigationItem"));
            Invoke(Descendants<Button>(page).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "开源许可"));
            var entries = ((StackPanel)page.FindName("LicenseEntries")).Children.OfType<Button>().ToArray();
            var blue = ((SolidColorBrush)page.FindResource("SettingsBlueBrush")).Color;
            var soft = ((SolidColorBrush)page.FindResource("SettingsSoftBlueBrush")).Color;
            Invoke(entries[1]); Drain();
            check(Descendants<Border>(entries[1]).Count(b => b.Name == "LicenseSelectionMarker") == 1 &&
                  !Descendants<Border>(entries[0]).Any(b => b.Name == "LicenseSelectionMarker"),
                "license selection moves its single blue marker: " + theme);
            check(((SolidColorBrush)Descendants<TextBlock>(entries[1]).Single(t => t.Name == "LicenseEntryTitle").Foreground).Color == blue,
                "selected license title uses the same blue as help topics: " + theme);
            var divider = ((StackPanel)page.FindName("LicenseEntries")).Children.OfType<Border>().First();
            check(divider.Margin.Top >= 8 && divider.Margin.Bottom >= 8, "license highlight has space away from dividers: " + theme);
            foreach (var entry in entries.Take(2))
            {
                entry.SetValue(hoverKey, true); Drain();
                check(Descendants<Border>(entry).Any(b => b.Background is SolidColorBrush brush && brush.Color == soft && b.CornerRadius.TopLeft >= 8),
                    "selected and unselected licenses retain rounded hover highlight: " + theme);
                Capture(root, Path.Combine(output, theme + (entry == entries[1] ? "-license-selected-hover.png" : "-license-hover.png")));
                entry.SetValue(hoverKey, false);
            }

            Invoke((FrameworkElement)page.FindName("AboutNavigationItem"));
            Invoke(Descendants<Button>(page).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "帮助与反馈"));
            foreach (var topic in new[] { "connection", "messages", "faq" })
            {
                Invoke(((StackPanel)page.FindName("HelpTopics")).Children.OfType<Button>().Single(b => (string)b.Tag == topic)); Settle();
                var icon = Descendants<Image>(page).Single(i => i.Name == "HelpNoteIcon");
                var parent = (Grid)VisualTreeHelper.GetParent(icon);
                var origin = icon.TranslatePoint(new Point(), parent);
                var clip = LayoutInformation.GetLayoutClip(icon);
                check(icon.ActualWidth == 24 && parent.ColumnDefinitions[0].ActualWidth >= icon.ActualWidth &&
                      origin.X >= 0 && (clip == null || clip.Bounds.Width >= icon.ActualWidth),
                    "help note icon fits its column and has no cropped half: " + theme + "/" + topic);
                Capture(root, Path.Combine(output, theme + "-help-" + topic + ".png"));
            }

            Invoke((FrameworkElement)page.FindName("FilesNavigationItem"));
            var scroll = (ScrollViewer)page.FindName("SettingsScroll");
            var before = scroll.TranslatePoint(new Point(), root);
            var height = scroll.ActualHeight;
            var offset = scroll.VerticalOffset;
            var toasts = (ToastHost)window.FindName("Toasts");
            toasts.Expire(DateTimeOffset.MaxValue);
            var setStatus = typeof(SettingsPage).GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var pair in new[] {
                (Wpf.Ui.Controls.InfoBarSeverity.Informational, ToastLevel.Info),
                (Wpf.Ui.Controls.InfoBarSeverity.Success, ToastLevel.Success),
                (Wpf.Ui.Controls.InfoBarSeverity.Warning, ToastLevel.Warning),
                (Wpf.Ui.Controls.InfoBarSeverity.Error, ToastLevel.Error) })
            {
                var now = DateTimeOffset.UtcNow;
                setStatus.Invoke(page, new object[] { pair.Item1, "设置操作反馈", @"D:\BlueLink\Download" }); Drain();
                var notice = toasts.Items.Last();
                check(notice.Level == pair.Item2 && notice.ExpiresAt > now &&
                      notice.ExpiresAt <= DateTimeOffset.UtcNow + ToastHost.Duration(pair.Item2),
                    "settings feedback uses shared toast severity and automatic expiry: " + theme + "/" + pair.Item2);
            }
            check(toasts.IsVisible && toasts.Items.Count == 4 && page.FindName("SaveInfoBar") == null &&
                  scroll.TranslatePoint(new Point(), root) == before && scroll.ActualHeight == height && scroll.VerticalOffset == offset,
                "settings notifications overlay without shifting content or scroll position: " + theme);
            Capture(root, Path.Combine(output, theme + "-settings-toasts.png"));
            toasts.Expire(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(6)); Drain();
            check(toasts.Items.Count == 0 && scroll.ActualHeight == height, "expired settings messages leave no empty banner: " + theme);
            if (theme == "light")
            {
                setStatus.Invoke(page, new object[] { Wpf.Ui.Controls.InfoBarSeverity.Success, "已打开文件夹", @"D:\BlueLink\Download" });
                var timer = Stopwatch.StartNew();
                while (toasts.Items.Count > 0 && timer.Elapsed < TimeSpan.FromSeconds(4)) { Drain(); Thread.Sleep(20); }
                check(toasts.Items.Count == 0, "production dispatcher timer automatically removes settings success feedback");
            }
            Invoke((FrameworkElement)page.FindName("CancelButton")); Drain();
        }
    }
}
