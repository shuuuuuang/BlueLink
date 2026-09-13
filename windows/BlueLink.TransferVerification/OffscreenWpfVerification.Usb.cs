using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using BlueLink;
using BlueLink.Domain;
using BlueLink.Session;
using BlueLink.Transport;

internal sealed partial class OffscreenWpfVerification
{
    private void VerifyUsbSessionDesign(string dataRoot, string output)
    {
        foreach (var theme in new[] { "light", "dark" })
        {
            var window = new MainWindow(initializeRuntime: false, dataRoot: Path.Combine(dataRoot, theme));
            var model = window.ViewModel;
            try
            {
                WaitForUiTask(model.InitializeLocalStateAsync());
                WaitForUiTask(model.SaveSettingsAsync(model.Settings with { Theme = theme, Language = "zh-CN" }));
                using (var settings = new SettingsPage(model))
                    VerifyUsbPages(model, settings, DetachForRendering(settings), output);
                WaitForUiTask(model.SaveSettingsAsync(model.Settings with { UsbEnabled = true }));
                var peer = new ConversationSummary("usb-a", "QA 手机 A", PeerPlatform.Android, DeviceAvailability.Connected,
                    Guid.NewGuid(), "usb:a", 7, DateTimeOffset.Now) { UsbReady = true };
                model.Sessions.Add(new(peer.SessionId!.Value, peer.PeerId, peer.PeerName, "usb:a", ConnectionPhase.Connected,
                    "", DateTimeOffset.Now, TransportKind.Usb, PeerPlatform.Android));
                model.UseVisualFixture(peer);
                var root = DetachForRendering(window);
                Capture(root, output, "usb-header-" + theme, 1000, 600);
                var header = (Image)window.FindName("HeaderUsbReady");
                Check(header.Visibility == Visibility.Visible && header.ActualWidth == 20, "verified active peer shows the header lightning: " + theme);
                model.ShowFiles = true;
                model.FilesAllDevices = false;
                Capture(root, output, "usb-files-header-" + theme, 1000, 600);
                Check(header.Visibility == Visibility.Visible, "peer file view retains its own USB indicator");
                model.FilesAllDevices = true;
                Layout(root, 1000, 600);
                Check(header.Visibility == Visibility.Collapsed, "global files have no peer USB indicator");
                model.ShowFiles = false;
                model.FilesAllDevices = false;
                foreach (var ready in new[] { true, false })
                foreach (var name in new[] { "QA 手机 A", "QA 非常长的设备名称用于检查闪电和未读徽标不会被挤出卡片边界" })
                {
                    var card = new ContentControl { Content = peer with { PeerName = name, UsbReady = ready },
                        ContentTemplate = (DataTemplate)window.Resources["ConversationCardTemplate"] };
                    card.Resources.MergedDictionaries.Add(window.Resources);
                    card.Resources.MergedDictionaries.Add(Application.Current.Resources);
                    Capture(card, output, $"usb-card-{theme}-{ready}-{name.Length}", 284, 90);
                    var icon = Descendants<Image>(card).Single(value => value.Name == "ConversationUsbReady");
                    var badge = Descendants<Border>(card).Single(value => value.Name == "UnreadBadge");
                    var label = Descendants<TextBlock>(card).First(value => value.Text == name);
                    Check(icon.Visibility == (ready ? Visibility.Visible : Visibility.Collapsed), "only ready cards show lightning");
                    if (ready)
                    {
                        var point = icon.TranslatePoint(new Point(), card);
                        var textEnd = label.TranslatePoint(new Point(label.ActualWidth, label.ActualHeight / 2), card);
                        var badgePoint = badge.TranslatePoint(new Point(), card);
                        Check(Math.Abs(point.X - textEnd.X - 6) < 1 && Math.Abs(point.Y + icon.ActualHeight / 2 - textEnd.Y) < 1,
                            "lightning stays six DIP after the name and vertically centered");
                        Check(point.X + icon.ActualWidth <= badgePoint.X, "long name cannot displace unread badge or lightning");
                    }
                }
                model.Sessions.Clear();
                model.Sessions.Add(new(peer.SessionId!.Value, "other-peer", "Other", "usb:b", ConnectionPhase.Connected,
                    "", DateTimeOffset.Now, TransportKind.Usb));
                model.UseVisualFixture(peer);
                Check(!model.ActiveUsbReady, "another device USB cannot light the active header");
                Check(new WindowInteropHelper(window).Handle == IntPtr.Zero, "USB visual checks create no native desktop window");
            }
            finally { WaitForUiTask(model.DisposeAsync().AsTask()); window.Close(); }
        }
    }
}
