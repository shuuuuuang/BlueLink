using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlueLink;
using BlueLink.Domain;

internal static partial class BackgroundConversationVerification
{
    private static void VerifyTransferSettings(MainWindow window, FrameworkElement root, string output, Action<bool, string> check)
    {
        var model = window.ViewModel;
        var png = Path.Combine(output, "qa-thumbnail.png");
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen()) dc.DrawRectangle(Brushes.CornflowerBlue, null, new Rect(0, 0, 120, 80));
        var bitmap = new RenderTargetBitmap(120, 80, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(png)) encoder.Save(stream);
        foreach (var theme in new[] { "light", "dark" })
        foreach (var show in new[] { true, false, true })
        {
            Wait(model.SaveSettingsAsync(model.Settings with { Theme = theme, ShowImageThumbnails = show }));
            model.Messages.Clear();
            model.Messages.Add(new(Guid.NewGuid(), "", false, DateTimeOffset.Now, MessageStatus.Received, ChatItemKind.Image,
                [new(Guid.NewGuid(), Guid.NewGuid(), "qa-image.png", "image/png", 1000, State: "Transferring", PreviewPath: png, CompletedBytes: 400)]));
            Drain(); root.UpdateLayout(); Drain();
            var thumbnail = Descendants<Image>(root).Single(x => x.Name == "Thumbnail");
            var card = Descendants<Grid>(root).Single(x => x.Name == "FileCard");
            check(thumbnail.IsVisible == show && card.IsVisible != show, "saved thumbnail preference controls in-progress previews: " + theme + "/" + show);
            Capture(root, Path.Combine(output, $"thumbnail-{theme}-{show}.png"));
        }
        Wait(model.SaveSettingsAsync(model.Settings with { ShowImageThumbnails = false }));
        var waitingMessage = model.Messages.Single();
        model.Messages[0] = waitingMessage with { Attachments = [waitingMessage.Attachments![0] with { State = "RemotePaused" }] };
        Drain(); root.UpdateLayout(); Drain();
        check(Descendants<TextBlock>(root).Any(x => x.IsVisible && x.Text.Contains("等待对端继续发送")) &&
            Descendants<Image>(root).Single(x => x.Name == "PausedReceiveIndicator").IsVisible,
            "receiver file card displays waiting and pause indicator after remote pause");
        Capture(root, Path.Combine(output, "receiver-waiting.png"));
        Wait(model.SaveSettingsAsync(model.Settings with { ShowImageThumbnails = true }));
        void Toggle(string name)
        {
            var control = (ToggleButton)window.ActiveSettingsPage!.FindName(name);
            var provider = (IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(control)!.GetPattern(PatternInterface.Toggle);
            provider.Toggle(); Drain();
        }
        void Choice(string name)
        {
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var dialog = Application.Current.Windows.OfType<ConfirmationWindow>().Single();
                dialog.ApplyTemplate(); dialog.UpdateLayout();
                Capture((FrameworkElement)VisualTreeHelper.GetChild(dialog, 0), Path.Combine(output, "unsaved-" + name + ".png"));
                var button = Descendants<Button>(dialog).Single(b => b.IsVisible && (b.Content?.ToString() == name));
                ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(button)!.GetPattern(PatternInterface.Invoke)).Invoke();
            }));
        }
        window.OpenSettings();
        var page = window.ActiveSettingsPage!;
        check(!page.HasUnsavedChanges, "opening settings is clean");
        Toggle("ThumbnailsToggle"); Toggle("ThumbnailsToggle");
        check(!page.HasUnsavedChanges, "reverting a toggle removes the dirty state");
        Toggle("ThumbnailsToggle");
        Choice("继续编辑");
        check(!window.TryCloseSettings() && window.ActiveSettingsPage == page && page.HasUnsavedChanges,
            "keep editing preserves unsaved draft and the settings page");
        Choice("放弃修改");
        check(window.TryCloseSettings() && model.Settings.ShowImageThumbnails, "discard returns without persisting the draft");
        window.OpenSettings(); Toggle("ThumbnailsToggle"); Toggle("ConnectionUsbToggle");
        Choice("保存并返回");
        check(window.TryCloseSettings() && !model.Settings.ShowImageThumbnails && model.Settings.UsbEnabled,
            "save-and-return persists both USB and thumbnail settings before leaving");
        window.OpenSettings();
        check(!window.ActiveSettingsPage!.HasUnsavedChanges && window.TryCloseSettings(), "saved values reopen cleanly without another prompt");
        window.OpenSettings();
        var limit = (TextBox)window.ActiveSettingsPage!.FindName("ReceiveLimitText");
        // Validate the save route without letting validation focus this invisible test window.
        limit.Focusable = false;
        var previousLimit = limit.Text;
        ((IValueProvider)UIElementAutomationPeer.CreatePeerForElement(limit)!.GetPattern(PatternInterface.Value)).SetValue("invalid");
        Choice("保存并返回");
        check(!window.TryCloseSettings() && window.ActiveSettingsPage!.HasUnsavedChanges,
            "invalid input keeps the draft and settings page when save-and-return validation fails");
        limit.Text = previousLimit;
        check(window.TryCloseSettings(), "correcting input back to saved value leaves cleanly");
        Wait(model.SaveSettingsAsync(model.Settings with { UsbEnabled = false }));
    }
}
