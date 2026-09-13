using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BlueLink;
using BlueLink.Domain;
using BlueLink.Protocol;
using BlueLink.Security;
using BlueLink.Storage;
using Button = Wpf.Ui.Controls.Button;

internal static partial class BackgroundSettingsVerification
{
    private static void Settle()
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 400) { Drain(); Thread.Sleep(10); }
        Drain();
    }

    private static void VerifyControls(MainWindow window, string output, Action<bool, string> check)
    {
        var metrics = new List<object>();
        try
        {
            foreach (var theme in new[] { "light", "dark" })
            foreach (var language in new[] { "zh-CN", "zh-TW", "en-US" })
            {
                Wait(window.ViewModel.SaveSettingsAsync(window.ViewModel.Settings with { Theme = theme, Language = language }));
                foreach (var size in new[] { new Size(1000, 600), new Size(1260, 720) })
                {
                    window.Width = size.Width; window.Height = size.Height; Drain();
                    window.OpenSettings(); Settle();
                    var page = window.ActiveSettingsPage!;
                    var root = (FrameworkElement)window.Content;
                    foreach (var tab in new[] { "General", "Connection", "Files", "Privacy", "About" })
                    {
                        Invoke((FrameworkElement)page.FindName(tab + "NavigationItem")); Settle();
                        var label = $"{theme}-{language}-{size.Width}-{tab}";
                        Capture(root, Path.Combine(output, label + ".png"));
                        VerifyButtonLabels(page, label);
                        var scroll = (ScrollViewer)page.FindName("SettingsScroll");
                        check(scroll.Padding == new Thickness(12, 12, scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible ? 14 : 12, 12),
                            "settings inset is 12 DIP with a scrollbar lane only on overflow: " + label);
                        check(((FrameworkElement)page.FindName("FooterActions")).Margin == new Thickness(12),
                            "footer uses the same inset: " + label);
                        if (tab == "Connection")
                            check(!Descendants<TextBlock>(page).Any(t => t.IsVisible && t.Text == BlueLink.Localization.Strings.Get("附近设备扫描")),
                                "connection preferences have no duplicate scan card: " + label);
                        scroll.ScrollToBottom(); Drain();
                        VerifyButtonLabels(page, label + "-bottom");
                        if (tab is "Privacy" or "Connection") Capture(root, Path.Combine(output, label + "-bottom.png"));
                    }
                    check(window.TryCloseSettings(), "return from settings: " + size.Width);
                    Drain(); VerifyButtonLabels(root, theme + language + "-home");
                    check(window.FindName("ScanButton") is null && ((Border)window.FindName("DevicesSidebar")).ContextMenu.Items.Count == 1, "home refresh menu remains available without a scan button");
                    window.ViewModel.ShowFiles = true; Settle();
                    Invoke((FrameworkElement)window.FindName("ChangeReceiveDirectoryButton")); Settle();
                    var filesSettings = window.ActiveSettingsPage!;
                    check(((FrameworkElement)filesSettings.FindName("FilesPage")).IsVisible &&
                          !((FrameworkElement)filesSettings.FindName("GeneralPage")).IsVisible,
                        "receive directory link opens Files & storage directly");
                    check(((Wpf.Ui.Controls.NavigationViewItem)filesSettings.FindName("FilesNavigationItem")).IsActive,
                        "receive directory link highlights Files & storage navigation");
                    check(((ScrollViewer)filesSettings.FindName("SettingsScroll")).VerticalOffset == 0,
                        "receive directory is at the top when opening its settings");
                    Capture(root, Path.Combine(output, $"{theme}-{language}-{size.Width}-ReceiveDirectoryLink.png"));
                    check(window.TryCloseSettings(), "receive directory settings can return to file workspace");
                    window.ViewModel.ShowFiles = false; Settle();
                }
            }
            VerifyTrustProjection();
            VerifyAlignedPreferences();
            foreach (var theme in new[] { "light", "dark" })
            foreach (var language in new[] { "zh-CN", "zh-TW", "en-US" })
            {
                Wait(window.ViewModel.SaveSettingsAsync(window.ViewModel.Settings with { Theme = theme, Language = language }));
                foreach (var conflict in new[] { false, true })
                {
                    var offer = new BlueLink.Transfer.FileOffer(Guid.NewGuid(), "settings-review.png", 1024, 1024, new byte[32]);
                    var request = new BlueLink.Transfer.IncomingFileDecision("QA Android", offer, true, conflict, "ask");
                    var dialog = IncomingFilePrompt.CreateWindow(request, out var choice);
                    dialog.WindowStartupLocation = WindowStartupLocation.Manual;
                    dialog.Left = window.Left; dialog.Top = window.Top;
                    dialog.ShowActivated = false; dialog.ShowInTaskbar = false; dialog.Opacity = 0;
                    dialog.Show(); Settle();
                    try
                    {
                        var primary = (Button)dialog.FindName("ConfirmationPrimaryButton");
                        check(primary.Appearance == Wpf.Ui.Controls.ControlAppearance.Primary &&
                              !dialog.Confirmed, "receive is a non-destructive, unconfirmed choice");
                        check(choice.IsVisible == conflict, "duplicate choices appear only for a name conflict");
                        VerifyButtonLabels(dialog, $"{theme}-{language}-receive-{conflict}");
                        Capture((FrameworkElement)dialog.Content, Path.Combine(output, $"{theme}-{language}-receive-{conflict}.png"));
                        if (conflict) { choice.SelectedIndex = 1; Invoke(primary); check(dialog.Confirmed && choice.SelectedIndex == 1, "receive selection remains explicit"); }
                        else { Invoke((FrameworkElement)dialog.FindName("ConfirmationCancelButton")); check(!dialog.Confirmed, "reject closes without accepting"); }
                    }
                    finally { dialog.Close(); }
                }
            }
        }
        finally
        {
            File.WriteAllText(Path.Combine(output, "button-text-metrics.json"), JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
        }

        void VerifyButtonLabels(FrameworkElement scope, string scenario)
        {
            foreach (var button in Descendants<ButtonBase>(scope).Where(b => b.IsVisible && b.ActualHeight > 0 && b.ActualHeight <= 44))
            foreach (var label in Descendants<TextBlock>(button).Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text) && t.TextWrapping == TextWrapping.NoWrap))
            {
                var text = new FormattedText(label.Text, CultureInfo.CurrentUICulture, label.FlowDirection,
                    new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch), label.FontSize,
                    Brushes.Black, VisualTreeHelper.GetDpi(label).PixelsPerDip);
                metrics.Add(new { scenario, control = button.Name, label.Text, buttonHeight = button.ActualHeight,
                    padding = button.Padding.ToString(), actualTextHeight = label.ActualHeight, requiredTextHeight = text.Height });
                check(button.ActualHeight - button.Padding.Top - button.Padding.Bottom - button.BorderThickness.Top - button.BorderThickness.Bottom + 0.8 >= text.Height,
                    "button content area fits full text line: " + scenario + "/" + label.Text);
                check(label.ActualHeight + 0.8 >= text.Height,
                    $"complete button text line: {scenario}/{button.Name}/{label.Text} ({label.ActualHeight:F2} >= {text.Height:F2})");
                if (button.Name == "ReturnToHomeButton" || button.Content is string)
                {
                    var left = label.TranslatePoint(new Point(), button).X;
                    check(left >= button.BorderThickness.Left - 0.8 && left + text.Width <= button.ActualWidth - button.Padding.Right - button.BorderThickness.Right + 0.8,
                        "action label fits horizontally: " + scenario + "/" + label.Text);
                }
                var textTop = label.TranslatePoint(new Point(), button).Y;
                check(textTop >= -0.8 && textTop + text.Height <= button.ActualHeight + 0.8,
                    "text stays inside button: " + scenario + "/" + label.Text);
                for (var parent = VisualTreeHelper.GetParent(label); parent is FrameworkElement element && parent != button; parent = VisualTreeHelper.GetParent(parent))
                {
                    var clip = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(element);
                    if (clip is null) continue;
                    var top = label.TranslatePoint(new Point(), element).Y;
                    check(top >= clip.Bounds.Top - 0.8 && top + text.Height <= clip.Bounds.Bottom + 0.8,
                        "template does not clip button text: " + scenario + "/" + label.Text);
                }
            }
        }
        void VerifyTrustProjection()
        {
            var model = window.ViewModel;
            var identity = (IdentityStore)typeof(MainViewModel).GetField("_identity", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
            var db = new BlueLinkDatabase(model.DataDirectory);
            var trusted = DeviceIdentity.Generate();
            var pinnedId = Convert.ToHexString(trusted.PeerId);
            var historyId = Convert.ToHexString(DeviceIdentity.Generate().PeerId);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var id in new[] { pinnedId, historyId })
            {
                Wait(db.UpsertPeerAsync(new(id, id == pinnedId ? "已信任的离线设备" : "仅有历史的离线设备", "Android",
                    StoredTrustState.Unknown, null, now, now, now)));
                Wait(db.UpsertConversationAsync(new("peer:" + id, id, now, 0)));
                Wait(db.UpsertMessageAsync(new(Guid.NewGuid().ToString("N"), "peer:" + id, id,
                    StoredMessageDirection.Incoming, StoredMessageType.Text, "验收历史", "Received", now, now)));
            }
            identity.Trust(trusted.PeerId, trusted.PublicKey);
            Wait(model.InitializeLocalStateAsync());
            Wait(model.SaveSettingsAsync(model.Settings with { Theme = "light", Language = "zh-CN" }));
            check(model.OfflineConversations.Count == 2 && model.TrustedDevices.Count == 1 && model.TrustedDevices[0].PeerId == pinnedId,
                "trusted offline devices are listed while history-only devices are excluded");
            window.OpenSettings(); var page = window.ActiveSettingsPage!; page.ShowConnections(); Settle();
            var list = Descendants<ItemsControl>(page).Single(c => AutomationProperties.GetName(c) == "已信任设备列表");
            check(list.Items.Count == 1 && ((ConversationSummary)list.Items[0]).PeerId == pinnedId,
                "settings trust list binds the offline pinned device");
            ((ScrollViewer)page.FindName("SettingsScroll")).ScrollToBottom(); Drain();
            Capture((FrameworkElement)window.Content, Path.Combine(output, "trusted-offline.png"));
            VerifyButtonLabels(page, "trusted-offline");
            Wait(model.ForgetPeerAsync(pinnedId)); Drain();
            check(model.TrustedDevices.Count == 0 && list.Items.Count == 0 && model.OfflineConversations.Count == 2,
                "revoking trust updates open settings while retaining both offline histories");
            check(Descendants<TextBlock>(page).Any(t => t.IsVisible && t.Text.Contains("离线列表保留历史会话")),
                "empty state distinguishes historical conversations from current security trust");
            Capture((FrameworkElement)window.Content, Path.Combine(output, "history-without-trust.png"));
            Wait(model.InitializeLocalStateAsync());
            check(new IdentityStore(model.DataDirectory).TrustedIdentities.Count == 0 && model.OfflineConversations.Count == 2,
                "revocation survives reload without trusting history records");
            var messages = db.LoadMessagesAsync("peer:" + pinnedId); Wait(messages);
            check(messages.Result.Single().Content == "验收历史", "trust removal preserves conversation messages");
            check(window.TryCloseSettings(), "trust acceptance returns to main window");
        }
        void VerifyAlignedPreferences()
        {
            var model = window.ViewModel;
            Wait(model.SaveSettingsAsync(model.Settings with { AutoDownloadFiles = true, DuplicateFilePolicy = "rename", Language = "zh-CN" }));
            window.OpenSettings(); Settle();
            var page = window.ActiveSettingsPage!;
            Invoke((FrameworkElement)page.FindName("FilesNavigationItem")); Settle();
            var ask = (ToggleButton)page.FindName("AutoDownloadToggle");
            check(ask.IsChecked == false, "automatic receiving is displayed as ask-before-receive off");
            ((System.Windows.Automation.Provider.IToggleProvider)System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(ask)!.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle();
            check(ask.IsChecked == true && model.Settings.AutoDownloadFiles, "native toggle edits draft without applying before save");
            ((ComboBox)page.FindName("DuplicatePolicyBox")).SelectedValue = "ask";
            Invoke((FrameworkElement)page.FindName("SaveButton")); Settle();
            check(!model.Settings.AutoDownloadFiles && model.Settings.DuplicateFilePolicy == "ask", "save commits inverted receive preference and duplicate policy");
            window.OpenSettings(); Settle(); page = window.ActiveSettingsPage!;
            Invoke((FrameworkElement)page.FindName("ConnectionNavigationItem")); Settle();
            check(page.FindName("MaxConnectionsBox") is null, "connection count setting has been removed");
            check(((FrameworkElement)page.FindName("ScanStartupToggle")).IsVisible && ((FrameworkElement)page.FindName("DiscoveryToggle")).IsVisible && ((FrameworkElement)page.FindName("ReconnectToggle")).IsVisible,
                "connection preferences expose independent scan, discovery and reconnect controls");
            Invoke((FrameworkElement)page.FindName("PrivacyNavigationItem")); Settle();
            check(((FrameworkElement)page.FindName("IdentityDetailsPanel")).Visibility == Visibility.Visible, "identity details are visible without a redundant reveal button");
            var retention = (ComboBox)page.FindName("RetentionBox"); retention.SelectedValue = "7d";
            check(retention.SelectedIndex >= 0, "seven-day retention can be selected");
            Invoke((FrameworkElement)page.FindName("CancelButton")); Settle();
            check(model.Settings.RetentionPeriod == "forever", "cancel discards retention draft");
        }
    }
}
