using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlueLink;
using BlueLink.Security;
using BlueLink.Storage;
using BlueLink.Protocol;
using BlueLink.Transfer;
using BlueLink.Domain;
using BlueLink.Files;

try
{
    if (args.Contains("--portable-probe")) { await PortableVerification.ProbeAsync(); return; }
    if (args.Contains("--portable-only")) { await PortableVerification.RunAsync(); return; }
    if (args.Contains("--mtp-responsiveness")) { await MtpResponsivenessVerification.RunAsync(); return; }
    if (args.Length == 2 && args[0] == "--forget-device") { ForgetPeerVerification.Run(args[1]); return; }
    if (args.Length == 2 && args[0] == "--notices-only") { TransientNoticeVerification.Run(args[1]); return; }
    if (args.Length == 2 && args[0] == "--dialogs-only") { DialogVerification.Run(args[1]); return; }
    if (args.Length == 4 && args[0] == "--desktop-dialog") { DialogVerification.Run(args[1], args[2], args[3]); return; }
    if (args.Length == 2 && args[0] == "--peer-reconnect") { await new UsbVerification().VerifyTransferReconnectAsync(args[1]); return; }
    if (args.Contains("--transfer-reconnect-only")) { await SessionTransferLedgerVerification.RunAsync(); return; }
    if (args.Contains("--mtp-only")) { await MtpVerification.RunAsync(); return; }
    if (args.Contains("--protocol-only", StringComparer.OrdinalIgnoreCase))
    {
        new BtxProtocolVerification().Run();
        return;
    }
    var fileIconsArgument = args.FirstOrDefault(value => value.StartsWith("--file-icons=", StringComparison.OrdinalIgnoreCase));
    if (fileIconsArgument is not null) { FileIconVerification.Run(fileIconsArgument["--file-icons=".Length..]); return; }
    if (args.Contains("--message-polish-protocol-only", StringComparer.OrdinalIgnoreCase))
    {
        DeviceNameVerification.Run();
        await new SecurityHandshakeVerification().RunAsync();
        return;
    }
    if (args.Contains("--bluetooth-callback-only", StringComparer.OrdinalIgnoreCase))
    {
        await BluetoothCallbackVerification.RunAsync();
        return;
    }
    var inputLayoutArgument = args.FirstOrDefault(value => value.StartsWith("--background-inputs=", StringComparison.OrdinalIgnoreCase));
    if (inputLayoutArgument is not null)
    {
        BackgroundSettingsVerification.Run(inputLayoutArgument["--background-inputs=".Length..], inputsOnly: true);
        return;
    }
    if (args.Contains("--settings-alignment-only", StringComparer.OrdinalIgnoreCase))
    {
        await new TransferReceiverVerification().RunAsync();
        await new SettingsVerification().RunAsync();
        await ReceiveSettingsVerification.RunAsync();
        await new SecurityHandshakeVerification().RunAsync();
        NotificationVerification.Run();
        return;
    }
    var settingsControlsArgument = args.FirstOrDefault(value => value.StartsWith("--background-settings-controls=", StringComparison.OrdinalIgnoreCase));
    if (settingsControlsArgument is not null)
    {
        BackgroundSettingsVerification.Run(settingsControlsArgument["--background-settings-controls=".Length..], controlsOnly: true);
        return;
    }
    var settingsLayoutArgument = args.FirstOrDefault(value => value.StartsWith("--offscreen-settings=", StringComparison.OrdinalIgnoreCase));
    if (settingsLayoutArgument is not null)
    {
        new OffscreenWpfVerification().Run(settingsLayoutArgument["--offscreen-settings=".Length..], settingsOnly: true);
        return;
    }
    var workspaceLayoutArgument = args.FirstOrDefault(value => value.StartsWith("--offscreen-workspace=", StringComparison.OrdinalIgnoreCase));
    if (workspaceLayoutArgument is not null)
    {
        new OffscreenWpfVerification().Run(workspaceLayoutArgument["--offscreen-workspace=".Length..], workspaceOnly: true);
        return;
    }
    var aboutUiArgument = args.FirstOrDefault(value => value.StartsWith("--background-about=", StringComparison.OrdinalIgnoreCase));
    if (aboutUiArgument is not null)
    {
        BackgroundSettingsVerification.Run(aboutUiArgument["--background-about=".Length..], aboutOnly: true);
        return;
    }
    var settingsUiArgument = args.FirstOrDefault(value => value.StartsWith("--background-settings=", StringComparison.OrdinalIgnoreCase));
    if (settingsUiArgument is not null)
    {
        BackgroundSettingsVerification.Run(settingsUiArgument["--background-settings=".Length..]);
        return;
    }
    if (args.Contains("--failure-reporting-self-test", StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException("Expected verification failure for the log-and-exit regression.");

    // Explicit, read-only hardware inventory; never opens a USB pipe or changes drivers.
    if (args.Contains("--usb-inventory", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
            new BlueLink.Usb.WindowsUsbInventory().Enumerate(CancellationToken.None),
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return;
    }

    var accessoryArgument = args.FirstOrDefault(value => value.StartsWith("--usb-start-accessory=", StringComparison.OrdinalIgnoreCase));
    if (accessoryArgument is not null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var targetId = accessoryArgument["--usb-start-accessory=".Length..];
        var target = new BlueLink.Usb.WindowsUsbInventory().Enumerate(deadline.Token)
            .Single(device => device.InstanceId.Equals(targetId, StringComparison.OrdinalIgnoreCase));
        if (target.AccessoryMode) throw new InvalidOperationException("Selected device is already in accessory mode.");
        await using var connection = new BlueLink.Usb.WinUsbConnection(target);
        await BlueLink.Usb.AoaProtocol.StartAccessoryAsync(connection, "BlueLink-USB-benchmark", deadline.Token);
        Console.WriteLine("AOA start request completed for selected device: " + target.InstanceId);
        return;
    }

    var hardwareArgument = args.FirstOrDefault(value => value.StartsWith("--usb-hardware=", StringComparison.OrdinalIgnoreCase));
    if (hardwareArgument is not null)
    {
        var output = args.Single(value => value.StartsWith("--hardware-output=", StringComparison.OrdinalIgnoreCase))["--hardware-output=".Length..];
        await new UsbHardwareVerification().RunAsync(hardwareArgument["--usb-hardware=".Length..], output,
            args.Contains("--hardware-session-only", StringComparer.OrdinalIgnoreCase),
            args.Contains("--hardware-commands", StringComparer.OrdinalIgnoreCase),
            args.FirstOrDefault(value => value.StartsWith("--hardware-identity=", StringComparison.OrdinalIgnoreCase))?["--hardware-identity=".Length..]);
        return;
    }

    var previewSourceArgument = args.FirstOrDefault(value =>
        value.StartsWith("--preview-source=", StringComparison.OrdinalIgnoreCase));
    var previewSource = previewSourceArgument?["--preview-source=".Length..];

    var usbArgument = args.FirstOrDefault(value => value.StartsWith("--offscreen-usb=", StringComparison.OrdinalIgnoreCase));
    if (usbArgument is not null)
    {
        new OffscreenWpfVerification().Run(usbArgument["--offscreen-usb=".Length..], usbOnly: true);
        return;
    }

    var interactionsArgument = args.FirstOrDefault(value => value.StartsWith("--offscreen-interactions=", StringComparison.OrdinalIgnoreCase));
    if (interactionsArgument is not null)
    {
        new OffscreenWpfVerification().Run(interactionsArgument["--offscreen-interactions=".Length..], interactionsOnly: true);
        return;
    }

    var nativeThumbnailArgument = args.FirstOrDefault(value => value.StartsWith("--background-thumbnails=", StringComparison.OrdinalIgnoreCase));
    if (nativeThumbnailArgument is not null)
    {
        BackgroundConversationVerification.Run(nativeThumbnailArgument["--background-thumbnails=".Length..], thumbnailsOnly: true);
        return;
    }

    var nativeConversationArgument = args.FirstOrDefault(value => value.StartsWith("--background-conversations=", StringComparison.OrdinalIgnoreCase));
    if (nativeConversationArgument is not null)
    {
        BackgroundConversationVerification.Run(nativeConversationArgument["--background-conversations=".Length..]);
        return;
    }

    var nativePreviewArgument = args.FirstOrDefault(value => value.StartsWith("--background-native-preview=", StringComparison.OrdinalIgnoreCase));
    if (nativePreviewArgument is not null)
    {
        BackgroundPreviewVerification.Run(nativePreviewArgument["--background-native-preview=".Length..]);
        return;
    }

    var imagePreviewArgument = args.FirstOrDefault(value => value.StartsWith("--offscreen-image-preview=", StringComparison.OrdinalIgnoreCase));
    if (imagePreviewArgument is not null)
    {
        new OffscreenWpfVerification().Run(imagePreviewArgument["--offscreen-image-preview=".Length..], imagePreviewOnly: true);
        return;
    }

    var fileAvailabilityArgument = args.FirstOrDefault(value => value.StartsWith("--offscreen-file-availability=", StringComparison.OrdinalIgnoreCase));
    if (fileAvailabilityArgument is not null)
    {
        new OffscreenWpfVerification().Run(fileAvailabilityArgument["--offscreen-file-availability=".Length..], fileAvailabilityOnly: true);
        return;
    }

    var identityArgument = args.FirstOrDefault(value => value.StartsWith("--offscreen-identity=", StringComparison.OrdinalIgnoreCase));
    if (identityArgument is not null)
    {
        new OffscreenWpfVerification().Run(identityArgument["--offscreen-identity=".Length..], identityOnly: true);
        return;
    }

    var nearbyLayoutArgument = args.FirstOrDefault(value => value.StartsWith("--offscreen-nearby=", StringComparison.OrdinalIgnoreCase));
    if (nearbyLayoutArgument is not null)
    {
        new OffscreenWpfVerification().Run(nearbyLayoutArgument["--offscreen-nearby=".Length..], nearbyOnly: true);
        return;
    }

    var offscreenArgument = args.FirstOrDefault(value => value.StartsWith("--offscreen-layout=", StringComparison.OrdinalIgnoreCase));
    if (offscreenArgument is not null)
    {
        new OffscreenWpfVerification().Run(offscreenArgument["--offscreen-layout=".Length..]);
        return;
    }

    var safeBackgroundOnly = args.Contains("--safe-background-only", StringComparer.OrdinalIgnoreCase);
    if (safeBackgroundOnly || args.Contains("--background-only", StringComparer.OrdinalIgnoreCase))
    {
        await new TransferReceiverVerification().RunAsync();
        await new LocalStorageVerification().RunAsync();
        await new FeedbackVerification().RunAsync();
        await new SettingsVerification().RunAsync();
        await ReceiveSettingsVerification.RunAsync();
        await new WindowSizeVerification().RunAsync();
        await new SecurityHandshakeVerification().RunAsync();
        await new UpdateVerification().RunAsync();
        await new UsbVerification().RunAsync();
        // The safe suite never starts installation fixtures or touches startup registration.
        if (!safeBackgroundOnly) await new InstallationVerification().RunAsync();
        NotificationVerification.Run();
        new IdentityRecoveryVerification().Run();
        new BtxProtocolVerification().Run();
        new FileDragDropVerification().Run();
        new ChatTimeVerification().Run();
        new HistoryQueryVerification().Run();
        await new OutgoingSnapshotVerification().RunAsync();
        new ImagePreviewViewportVerification().Run();
        return;
    }

    if (args.Contains("--installation-only", StringComparer.OrdinalIgnoreCase))
    {
        await new InstallationVerification().RunAsync();
        return;
    }

    if (args.Contains("--usb-only", StringComparer.OrdinalIgnoreCase))
    {
        await new UsbVerification().RunAsync();
        return;
    }

    if (args.Contains("--updates-only", StringComparer.OrdinalIgnoreCase))
    {
        await new UpdateVerification().RunAsync();
        return;
    }

    if (args.Contains("--security-only", StringComparer.OrdinalIgnoreCase))
    {
        await new SecurityHandshakeVerification().RunAsync();
        return;
    }

    if (args.Contains("--feedback-only", StringComparer.OrdinalIgnoreCase))
    {
        await new FeedbackVerification().RunAsync();
        return;
    }

    if (args.Contains("--settings-only", StringComparer.OrdinalIgnoreCase))
    {
        await new SettingsVerification().RunAsync();
        return;
    }

    if (args.Contains("--history-query-only", StringComparer.OrdinalIgnoreCase))
    {
        new HistoryQueryVerification().Run();
        return;
    }

    if (args.Contains("--image-preview-only", StringComparer.OrdinalIgnoreCase))
    {
        new ImagePreviewViewportVerification().Run(previewSource);
        return;
    }

    if (args.Contains("--chat-scrollbar-only", StringComparer.OrdinalIgnoreCase))
    {
        new ChatScrollBarVerification().Run();
        return;
    }

    var verification = new TransferReceiverVerification();
    await verification.RunAsync();
    await new LocalStorageVerification().RunAsync();
    await new FeedbackVerification().RunAsync();
    await new SettingsVerification().RunAsync();
    await new SecurityHandshakeVerification().RunAsync();
    await new UpdateVerification().RunAsync();
    await new UsbVerification().RunAsync();
    new IdentityRecoveryVerification().Run();
    new BtxProtocolVerification().Run();
    new FileDragDropVerification().Run();
    new ChatTimeVerification().Run();
    new HistoryQueryVerification().Run();
    await new OutgoingSnapshotVerification().RunAsync();
    new ChatScrollBarVerification().Run();
    new ImagePreviewViewportVerification().Run(previewSource);

}
catch (Exception failure)
{
    Console.Error.WriteLine("Verification failed: " + failure);
    Environment.ExitCode = 1;
}

internal sealed class ImagePreviewViewportVerification
{
    private int _checks;

    public void Run(string? sourcePath = null)
    {
        var rotated = ImagePreviewViewportMath.RotatedExtent(new(400, 300), 90, 2);
        Check(Close(rotated.Width, 600) && Close(rotated.Height, 800), "90-degree rotated extent");

        var fit = ImagePreviewViewportMath.FitScale(new(1000, 800), 0,
            new(500, 500), 50, 0.05, 8);
        Check(Close(fit, 0.4), "fit scale respects viewport padding");

        var fitBounds = ImagePreviewViewportMath.TransformedBounds(
            new(1000, 800),
            ImagePreviewViewportMath.CreateImageMatrix(
                new(1000, 800), 0, fit, new(500, 500), new()));
        Check(Close(fitBounds.Left, 50) && Close(fitBounds.Top, 90) &&
              Close(fitBounds.Right, 450) && Close(fitBounds.Bottom, 410),
            "fit matrix keeps every image edge inside the viewport");

        var rotatedFit = ImagePreviewViewportMath.FitScale(new(1000, 800), 90,
            new(500, 500), 50, 0.05, 8);
        var rotatedBounds = ImagePreviewViewportMath.TransformedBounds(
            new(1000, 800),
            ImagePreviewViewportMath.CreateImageMatrix(
                new(1000, 800), 90, rotatedFit, new(500, 500), new()));
        Check(Close(rotatedBounds.Left, 90) && Close(rotatedBounds.Top, 50) &&
              Close(rotatedBounds.Right, 410) && Close(rotatedBounds.Bottom, 450),
            "quarter-turn fit matrix keeps every image edge inside the viewport");

        var clamped = ImagePreviewViewportMath.ClampOffset(new(500, -500),
            new(800, 600), new(500, 400));
        Check(Close(clamped.X, 150) && Close(clamped.Y, -100), "pan offset clamps to image edges");

        var zoomed = ImagePreviewViewportMath.ZoomAround(new(0, 0), new(750, 400),
            new(1000, 800), 1, 2);
        Check(Close(zoomed.X, -250) && Close(zoomed.Y, 0), "pointer-centered zoom preserves anchor");

        var crop = ImagePreviewViewportMath.VisibleMapRect(new(1000, 800),
            new(500, 400), new(0, 0), new(0, 0, 100, 80));
        Check(Close(crop.X, 25) && Close(crop.Y, 20) &&
              Close(crop.Width, 50) && Close(crop.Height, 40), "navigator crop maps visible viewport");

        Check(ImagePreviewViewportMath.NormalizeAngle(-90) == 270,
            "negative rotation normalizes to a stable quarter turn");

        VerifyDpiInvariantPixelGeometry();
        if (!string.IsNullOrWhiteSpace(sourcePath)) VerifyExactSource(sourcePath);
        Console.WriteLine($"BlueLink image preview viewport verification passed: {_checks} checks");
    }

    private void VerifyDpiInvariantPixelGeometry()
    {
        var viewport = new Size(1100, 680);
        foreach (var dpiScale in new[] { 1d, 1.25d, 1.5d })
        {
            var dpi = new DpiScale(dpiScale, dpiScale);
            var imageSize = ImagePreviewViewportMath.PixelSizeInDips(1254, 1254, dpi);
            Check(Close(imageSize.Width * dpiScale, 1254) &&
                  Close(imageSize.Height * dpiScale, 1254),
                $"{dpiScale:P0} actual-size maps one source pixel to one physical pixel");

            foreach (var angle in new[] { 0d, 90d, 180d, 270d })
            {
                var scale = ImagePreviewViewportMath.FitScale(
                    imageSize, angle, viewport, 28, 0.05, 8);
                var bounds = ImagePreviewViewportMath.TransformedBounds(
                    imageSize,
                    ImagePreviewViewportMath.CreateImageMatrix(
                        imageSize, angle, scale, viewport, new Point()));
                Check(bounds.Left >= 27.999 && bounds.Top >= 27.999 &&
                      bounds.Right <= viewport.Width - 27.999 &&
                      bounds.Bottom <= viewport.Height - 27.999,
                    $"{dpiScale:P0} {angle:0}-degree fit keeps every source edge inside the viewport");
            }
        }

        var rectangular = new Size(1254, 837);
        foreach (var angle in new[] { 0d, 90d, 180d, 270d })
        {
            var scale = ImagePreviewViewportMath.FitScale(
                rectangular, angle, viewport, 28, 0.05, 8);
            var bounds = ImagePreviewViewportMath.TransformedBounds(
                rectangular,
                ImagePreviewViewportMath.CreateImageMatrix(
                    rectangular, angle, scale, viewport, new Point()));
            Check(bounds.Left >= 27.999 && bounds.Top >= 27.999 &&
                  bounds.Right <= viewport.Width - 27.999 &&
                  bounds.Bottom <= viewport.Height - 27.999,
                $"rectangular {angle:0}-degree fit keeps every source edge inside the viewport");
        }
    }

    private void VerifyExactSource(string sourcePath)
    {
        var resolved = Path.GetFullPath(sourcePath);
        Check(File.Exists(resolved), "exact preview source exists");
        using var stream = File.OpenRead(resolved);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        Check(hash.Equals(
                "CA9BF460758A117259D352B104E42EEEB73569639D2EC206BA699DB4B10B1EF2",
                StringComparison.OrdinalIgnoreCase),
            "exact preview source SHA-256");
        stream.Position = 0;
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        Check(frame.PixelWidth == 1254 && frame.PixelHeight == 1254,
            "exact preview source is 1254 by 1254 pixels");
        Check(frame.Format.BitsPerPixel == 32,
            "exact preview source retains a 32-bit alpha-capable pixel format");
        Console.WriteLine(
            $"Exact preview source: {resolved}; {frame.PixelWidth}x{frame.PixelHeight}; " +
            $"{frame.Format}; SHA256={hash}");
    }

    private static bool Close(double actual, double expected) => Math.Abs(actual - expected) < 0.001;

    private void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }
}

internal sealed class ChatScrollBarVerification
{
    public void Run()
    {
        Exception? failure = null;
        var measuredGap = 0d;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var testRoot = Path.Combine(Path.GetTempPath(), $"BlueLinkChatLayout-{Guid.NewGuid():N}");
                var window = new MainWindow(initializeRuntime: false, dataRoot: testRoot)
                {
                    Width = 1180,
                    Height = 720,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000
                };
                try
                {
                    var fixture = typeof(MainWindow).GetMethod(
                        "LoadVisualFixture", BindingFlags.Instance | BindingFlags.NonPublic);
                    fixture?.Invoke(window, [true]);
                    var messageList = window.FindName("MessageList") as ListBox ??
                        throw new InvalidOperationException("MessageList was not found.");
                    messageList.Height = 118;
                    window.Show();
                    window.UpdateLayout();
                    messageList.UpdateLayout();

                    var scrollViewer = FindVisualChild<ScrollViewer>(messageList) ??
                        throw new InvalidOperationException("Message ScrollViewer was not created.");
                    var scrollBar = FindVisualChild<ScrollBar>(
                        messageList, value => value.Orientation == Orientation.Vertical &&
                                              value.Visibility == Visibility.Visible) ??
                        throw new InvalidOperationException("Vertical message scrollbar did not become visible.");
                    var firstItem = messageList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                    if (firstItem is null)
                    {
                        messageList.ScrollIntoView(messageList.Items[0]);
                        window.UpdateLayout();
                        firstItem = messageList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                    }
                    if (firstItem is null)
                        throw new InvalidOperationException("Visible message item was not generated.");

                    var itemOrigin = firstItem.TransformToAncestor(messageList).Transform(new Point());
                    var barOrigin = scrollBar.TransformToAncestor(messageList).Transform(new Point());
                    measuredGap = barOrigin.X - (itemOrigin.X + firstItem.ActualWidth);
                    if (measuredGap < 8)
                        throw new InvalidOperationException(
                            $"Message/scrollbar safety gap is only {measuredGap:0.##} px.");

                    scrollViewer.ScrollToTop();
                    window.UpdateLayout();
                    var before = scrollViewer.VerticalOffset;
                    scrollViewer.ScrollToEnd();
                    window.UpdateLayout();
                    if (scrollViewer.ScrollableHeight <= 0 ||
                        scrollViewer.VerticalOffset <= before ||
                        Math.Abs(scrollViewer.VerticalOffset - scrollViewer.ScrollableHeight) > 0.5)
                        throw new InvalidOperationException("Message scrollbar could not scroll to the end.");
                    VerifyHistoryWindow();
                }
                finally
                {
                    window.Close();
                    app.Shutdown();
                    Directory.Delete(testRoot, recursive: true);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20)))
            throw new TimeoutException("Chat scrollbar WPF verification timed out.");
        if (failure is not null) throw new InvalidOperationException(
            "Chat scrollbar WPF verification failed.", failure);
        Console.WriteLine(
            $"BlueLink chat scrollbar verification passed: SafetyGap={measuredGap:0.##} px; ScrollToEnd=True; HistoryWindow=True");
    }

    private static void VerifyHistoryWindow()
    {
        var records = new System.Collections.ObjectModel.ObservableCollection<ChatItem>
        {
            new(Guid.NewGuid(), "QA Review", true, DateTimeOffset.Now, MessageStatus.Read),
            new(Guid.NewGuid(), "", false, DateTimeOffset.Now.AddDays(-1), MessageStatus.Received,
                ChatItemKind.Image, [new(Guid.NewGuid(), Guid.NewGuid(), "QA.png", "image/png", 32)]),
        };
        var history = new MessageSearchWindow("QA 回归", records)
        { Left = -32000, Top = -32000, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            history.Show();
            history.UpdateLayout();
            var input = (Wpf.Ui.Controls.TextBox)history.FindName("QueryInput");
            var results = (ListBox)history.FindName("Results");
            if (!history.IsVisible || results.Items.Count != 2 || results.SelectedItem is not null)
                throw new InvalidOperationException("History grouping must not select a result or close during window creation.");
            input.Text = " review ";
            history.UpdateLayout();
            if (results.Items.Count != 1) throw new InvalidOperationException("History input must filter rendered results.");
            input.Text = "missing QA";
            if (((TextBlock)history.FindName("EmptyMessage")).Visibility != Visibility.Visible)
                throw new InvalidOperationException("Empty history result state is missing.");
            input.Text = "";
            var imageTab = FindVisualChild<RadioButton>(history, button => (string?)button.Tag == "Images")!;
            imageTab.IsChecked = true;
            if (results.Items.Count != 1) throw new InvalidOperationException("Image tab must filter attachment history.");
            var dateTab = FindVisualChild<RadioButton>(history, button => (string?)button.Tag == "Date")!;
            dateTab.IsChecked = true;
            ((DatePicker)history.FindName("DateInput")).SelectedDate = DateTime.Today.AddDays(-1);
            if (results.Items.Count != 1) throw new InvalidOperationException("Date filter must use the selected local day.");
        }
        finally { history.Close(); }
    }

    private static T? FindVisualChild<T>(DependencyObject root, Predicate<T>? predicate = null)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && (predicate is null || predicate(match))) return match;
            var nested = FindVisualChild(child, predicate);
            if (nested is not null) return nested;
        }
        return null;
    }
}

internal sealed class ChatTimeVerification
{
    public void Run()
    {
        var now = DateTimeOffset.Now;
        Check(Create(now.Date.AddHours(9).AddMinutes(7)).GroupTimeText == "今天 09:07", "today includes its relative date and HH:mm");
        Check(Create(now.Date.AddDays(-1).AddHours(8).AddMinutes(6)).GroupTimeText == "昨天 08:06", "yesterday label");
        Check(Create(new DateTimeOffset(new DateTime(now.Year - 1, 12, 31, 7, 5, 0), now.Offset)).GroupTimeText ==
              $"{now.Year - 1}年12月31日 07:05", "previous year label");
        var olderThisYear = new DateTimeOffset(new DateTime(now.Year, 1, 2, 6, 4, 0), now.Offset);
        if (olderThisYear.Date < now.Date.AddDays(-7))
            Check(Create(olderThisYear).GroupTimeText == "1月2日 06:04", "same year label without zero padding");
        Console.WriteLine("BlueLink Windows chat timestamp verification passed");
    }

    private static ChatItem Create(DateTimeOffset timestamp) =>
        new(Guid.NewGuid(), "test", false, timestamp, MessageStatus.Received);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class OutgoingSnapshotVerification
{
    public async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "BlueLinkSnapshotVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "mutable.bin");
            var original = Enumerable.Range(0, 130_000).Select(value => (byte)(value % 251)).ToArray();
            await File.WriteAllBytesAsync(source, original);
            var snapshot = await OutgoingSnapshot.CreateAsync(source, Path.Combine(root, "cache"), Guid.NewGuid(),
                CancellationToken.None);
            await File.WriteAllTextAsync(source, "mutated after snapshot");
            Check(File.ReadAllBytes(snapshot).SequenceEqual(original),
                "snapshot bytes must not follow later source mutations");
            Check(new FileInfo(snapshot).Length == original.LongLength,
                "snapshot must preserve the exact source length");
            Console.WriteLine("BlueLink Windows outgoing snapshot verification passed: 2 checks");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class FileDragDropVerification
{
    public void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "BlueLinkFileDropVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = Path.Combine(root, "first.txt");
            var second = Path.Combine(root, "second.bin");
            File.WriteAllText(first, "BlueLink");
            File.WriteAllBytes(second, [1, 2, 3, 4]);
            var files = FileDragDropService.NormalizeFilePaths([first, first.ToUpperInvariant(), root, second, ""]);
            Check(files.Count == 2, "file drops must reject directories and de-duplicate paths");
            Check(files[0] == Path.GetFullPath(first) && files[1] == Path.GetFullPath(second),
                "file drops must preserve the user's order");
            Check(FileDragDropService.TotalBytes(files) == 12, "file drop byte summary");

            var complete = new ChatAttachment(Guid.NewGuid(), Guid.NewGuid(), "first.txt", "text/plain", 8,
                first, "Completed");
            var incomplete = complete with { State = "Transferring" };
            Check(FileDragDropService.CanCopyOut(complete), "completed local attachments can be dragged out");
            Check(!FileDragDropService.CanCopyOut(incomplete), "incomplete attachments cannot be dragged out");
            Console.WriteLine("BlueLink Windows file drag/drop verification passed: 5 checks");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class IdentityRecoveryVerification
{
    public void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "BlueLinkIdentityVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var original = new IdentityStore(root);
            var originalPeerId = original.Identity.PeerId;
            original.Trust(RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(44));

            var path = Path.Combine(root, "identity.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            json["PrivateKeyProtected"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
            File.WriteAllText(path, json.ToJsonString());

            var recovered = new IdentityStore(root);
            Check(recovered.RecoveredCorruptIdentity, "corrupt DPAPI identity must be recovered");
            Check(recovered.RecoveryBackupPath is not null && File.Exists(recovered.RecoveryBackupPath),
                "unreadable identity must be backed up");
            Check(!CryptographicOperations.FixedTimeEquals(originalPeerId, recovered.Identity.PeerId),
                "recovery must generate a new identity");
            Check(recovered.TrustedIdentities.Count == 0,
                "identity recovery must require peers to be trusted again");
            Console.WriteLine("BlueLink Windows identity recovery verification passed: 4 checks");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class BtxProtocolVerification
{
    public void Run()
    {
        Check(Convert.ToHexString(ProtocolGreeting.Current.Encode()) == "010100000000003F",
            "BTX/1.1 greeting vector");
        var legacy = ProtocolGreeting.Decode([1, 0, 0, 0]);
        var downgrade = ProtocolGreeting.Current.Negotiate(legacy);
        Check(downgrade.Minor == 0 && downgrade.Capabilities == BtxCapability.None,
            "BTX/1.0 capability downgrade");
        var previous = ProtocolGreeting.Decode(Convert.FromHexString("010100000000001F"));
        var previousNegotiation = ProtocolGreeting.Current.Negotiate(previous);
        Check(previousNegotiation.Capabilities == (BtxCapability)0x1f &&
            (previousNegotiation.Capabilities & BtxCapability.MtpFiles) == BtxCapability.None,
            "previous BTX/1.1 peer preserves Bluetooth capabilities without MTP");
        Check((byte)WireMessageType.MtpControl == 33, "MTP control message code");

        var hash = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var message = new ChatEnvelope(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), ChatPayloadKind.File,
            1_700_000_000_123L, "BTX 1.1",
            [new(Guid.Parse("11111111-2222-3333-4444-555555555555"),
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), AttachmentRole.File,
                "report.pdf", "application/pdf", 123_456L, hash)]);
        var encoded = MessageWire.Encode(message);
        Check(Convert.ToHexString(encoded) ==
            "424D010300112233445566778899AABBCCDDEEFF0000018BCFE5687B0000000742545820312E310111111111222233334444555555555555AAAAAAAABBBBCCCCDDDDEEEEEEEEEEEE00000000000001E240000A7265706F72742E706466000F6170706C69636174696F6E2F70646620000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F",
            "cross-platform BTX/1.1 attachment message vector");
        var decoded = MessageWire.Decode(encoded);
        Check(decoded.MessageId == message.MessageId && decoded.Attachments.Count == 1 &&
            decoded.Attachments[0].Sha256!.SequenceEqual(hash), "structured attachment message round trip");

        var receipt = new ChatReceipt(message.MessageId, ReceiptState.Delivered, 1_700_000_000_321L);
        Check(MessageWire.DecodeReceipt(MessageWire.EncodeReceipt(receipt)) == receipt,
            "message receipt round trip");

        var control = new TransferControl(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            TransferControlAction.Cancel, 1_700_000_000_456L, "user canceled");
        var encodedControl = TransferControlWire.Encode(control);
        Check(Convert.ToHexString(encodedControl) ==
            "425401AAAAAAAABBBBCCCCDDDDEEEEEEEEEEEE030000018BCFE569C8000D757365722063616E63656C6564",
            "cross-platform BTX/1.1 transfer control vector");
        Check(TransferControlWire.Decode(encodedControl) == control, "transfer control round trip");

        var offer = new FileOffer(control.TransferId, "report.pdf", 123_456, 65_536, hash,
            message.MessageId, message.Attachments[0].AttachmentId, "application/pdf", AttachmentRole.File);
        var decodedOffer = TransferWire.DecodeOffer(TransferWire.EncodeOffer(offer));
        Check(decodedOffer.Id == offer.Id && decodedOffer.MessageId == offer.MessageId &&
            decodedOffer.AttachmentId == offer.AttachmentId && decodedOffer.Name == offer.Name &&
            decodedOffer.MimeType == offer.MimeType && decodedOffer.Role == offer.Role &&
            decodedOffer.Hash.SequenceEqual(hash), "enhanced file offer round trip");

        var accept = new FileTransferAccept(offer.Id, 7);
        Check(TransferWire.DecodeAccept(TransferWire.EncodeAccept(accept)) == accept,
            "resume-aware transfer accept round trip");
        Check(TransferWire.DecodeAccept(TransferWire.EncodeId(offer.Id)).NextExtent == 0,
            "legacy transfer accept maps to a zero resume extent");

        var truncatedRejected = false;
        try { MessageWire.Decode(encoded[..^1]); }
        catch (IOException) { truncatedRejected = true; }
        Check(truncatedRejected, "truncated structured message rejection");
        Check(WireMessagePriority.Of(WireMessageType.TransferControl) == 0,
            "transfer control bypasses bulk frames");
        Console.WriteLine("BlueLink BTX/1.1 Windows verification passed: 14 checks");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class LocalStorageVerification
{
    public async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "BlueLinkStorageVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = new BlueLinkDatabase(root, root);
            await database.InitializeAsync(new IdentityStore(root));

            var settings = BlueLinkSettings.Defaults(Path.Combine(root, "inbox")) with
            {
                ReceiveSizeLimitBytes = 123_456_789,
                DuplicateFilePolicy = "ask",
                AllowDiscovery = false,
                ReconnectAfterDisconnect = false,
                MessageNotifications = false,
                ConnectionNotifications = false,
                TransferNotifications = false,
            };
            await database.SaveSettingsAsync(settings);
            var loadedSettings = await database.LoadSettingsAsync();
            Check(loadedSettings == settings, "settings must round-trip through SQLite");

            using (var connection = new NativeSqliteConnection(database.DatabasePath))
            using (var query = connection.Prepare("SELECT key FROM app_setting WHERE key='max_connections'"))
                Check(!query.Read(), "new settings do not persist a connection limit");
            foreach (var legacyLimit in new[] { "1", "4", "8", "999", "invalid" })
            {
                using (var connection = new NativeSqliteConnection(database.DatabasePath))
                using (var insert = connection.Prepare("INSERT OR REPLACE INTO app_setting(key,value) VALUES('max_connections',?)"))
                    insert.Bind(1, legacyLimit).ExecuteNonQuery();
                Check(await database.LoadSettingsAsync() == settings, "legacy connection limit is ignored: " + legacyLimit);
                await database.SaveSettingsAsync(settings);
                Check(await database.LoadSettingsAsync() == settings, "saving unrelated preferences cannot restore the limit");
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            const string peerId = "0123456789ABCDEF0123456789ABCDEF";
            await database.UpsertPeerAsync(new(peerId, "Android test", "Android", StoredTrustState.Trusted, [1, 2, 3], now, now, now));
            await database.UpsertConversationAsync(new("conversation-test", peerId, now, 2, "draft"));
            await database.UpsertMessageAsync(new("message-test", "conversation-test", peerId,
                StoredMessageDirection.Incoming, StoredMessageType.File, "payload", "Delivered", now, 1));
            await database.UpsertAttachmentAsync(new("attachment-test", "message-test", "transfer-test",
                "photo.png", "image/png", 3, [4, 5, 6], "photo.png", "preview.png", "Completed"));
            await database.UpsertTransferAsync(new("transfer-test", peerId, "message-test", "Incoming", "Completed",
                "photo.png", "image/png", 3, 3, "photo.png", null, [4, 5, 6], null, null, now, now));
            await database.UpsertExtentAsync(new("transfer-test", 0, 0, 3, [4, 5, 6], "Verified"));

            Check((await database.LoadPeersAsync()).Any(peer => peer.PeerId == peerId && peer.DisplayName == "Android test"),
                "peer must persist");
            Check((await database.LoadConversationsAsync()).Any(conversation => conversation.ConversationId == "conversation-test"),
                "conversation must persist");
            Check((await database.LoadMessagesAsync("conversation-test")).Single().Type == StoredMessageType.File,
                "message must persist in order");
            Check((await database.LoadTransfersAsync(peerId)).Single().Status == "Completed",
                "transfer must persist");
            Console.WriteLine("BlueLink Windows SQLite verification passed: 5 checks");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class TransferReceiverVerification
{
    private int _checks;

    public async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "BlueLinkTransferVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await CompletesAfterHashStreamIsClosed(root);
            await IsolatesConcurrentSameNameTransfers(root);
            await PreservesAndRetriesACommitBlockedByAnotherHandle(root);
            await ResumesAfterReceiverRestart(root);
            Console.WriteLine($"BlueLink Windows transfer verification passed: {_checks} checks");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private async Task CompletesAfterHashStreamIsClosed(string root)
    {
        var data = CreateBytes(183_501, 20260826);
        var offer = Offer("android-screenshot.jpg", data);
        await using var receiver = new TransferReceiver(root, offer);
        await WriteExtents(receiver, offer, data);
        var committing = false;
        var target = await receiver.FinishAsync(CancellationToken.None, () => committing = true);

        Check(committing, "commit callback must run after hash verification");
        Check(File.Exists(target), "verified file must be committed");
        Check(File.ReadAllBytes(target).SequenceEqual(data), "committed bytes must match the sender");
        Check(!Directory.EnumerateFiles(root, "android-screenshot.jpg.*.part").Any(),
            "successful commit must remove the partial file");
        Check(await receiver.FinishAsync(CancellationToken.None) == target,
            "repeated finish must be idempotent");
    }

    private async Task IsolatesConcurrentSameNameTransfers(string root)
    {
        var firstData = CreateBytes(70_001, 1);
        var secondData = CreateBytes(70_002, 2);
        var firstOffer = Offer("same-name.bin", firstData);
        var secondOffer = Offer("same-name.bin", secondData);
        await using var first = new TransferReceiver(root, firstOffer);
        await using var second = new TransferReceiver(root, secondOffer);
        await WriteExtents(first, firstOffer, firstData);
        await WriteExtents(second, secondOffer, secondData);

        Check(Directory.EnumerateFiles(root, "same-name.bin.*.part").Count() == 2,
            "same-name transfers must use different partial files");
        var firstTarget = await first.FinishAsync(CancellationToken.None);
        var target = await second.FinishAsync(CancellationToken.None);
        Check(File.ReadAllBytes(target).SequenceEqual(secondData),
            "renamed file must contain the second transfer");
        Check(target != firstTarget && File.ReadAllBytes(firstTarget).SequenceEqual(firstData),
            "default same-name handling must preserve both files");
    }

    private async Task PreservesAndRetriesACommitBlockedByAnotherHandle(string root)
    {
        var oldData = CreateBytes(128, 3);
        var newData = CreateBytes(65_537, 4);
        var target = Path.Combine(root, "temporarily-locked.bin");
        await File.WriteAllBytesAsync(target, oldData);
        var offer = Offer(Path.GetFileName(target), newData);
        await using var receiver = new TransferReceiver(root, offer, "overwrite");
        await WriteExtents(receiver, offer, newData);

        IOException? commitFailure = null;
        using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { await receiver.FinishAsync(CancellationToken.None); }
            catch (IOException failure) { commitFailure = failure; }
        }

        Check(commitFailure?.Message.Contains("after 4 attempts", StringComparison.Ordinal) == true,
            "persistent sharing violation must report bounded commit retries");
        Check(File.ReadAllBytes(target).SequenceEqual(oldData),
            "failed commit must not corrupt the existing target");
        Check(Directory.EnumerateFiles(root, "temporarily-locked.bin.*.part").SingleOrDefault() is not null,
            "failed commit must retain the verified partial file");

        await receiver.FinishAsync(CancellationToken.None);
        Check(File.ReadAllBytes(target).SequenceEqual(newData),
            "finish must be retryable after the external lock is released");
        Check(!Directory.EnumerateFiles(root, "temporarily-locked.bin.*.part").Any(),
            "successful retry must consume the partial file");
    }

    private async Task ResumesAfterReceiverRestart(string root)
    {
        var data = CreateBytes(160_321, 5);
        var offer = Offer("restart-resume.bin", data);
        await using (var first = new TransferReceiver(root, offer))
        {
            var firstExtent = data.AsSpan(0, offer.ExtentSize).ToArray();
            await first.AcceptAsync(new FileExtent(offer.Id, 0, SHA256.HashData(firstExtent), firstExtent),
                CancellationToken.None);
            Check(first.ContiguousBytes == offer.ExtentSize,
                "receiver must expose the committed resume offset before restart");
        }

        await using (var resumed = new TransferReceiver(root, offer))
        {
            Check(resumed.ContiguousBytes == offer.ExtentSize,
                "receiver must restore committed extents after process restart");
            for (var offset = offer.ExtentSize; offset < data.Length; offset += offer.ExtentSize)
            {
                var index = offset / offer.ExtentSize;
                var length = Math.Min(offer.ExtentSize, data.Length - offset);
                var extent = data.AsSpan(offset, length).ToArray();
                await resumed.AcceptAsync(new FileExtent(offer.Id, index, SHA256.HashData(extent), extent),
                    CancellationToken.None);
            }
            var target = await resumed.FinishAsync(CancellationToken.None);
            Check(File.ReadAllBytes(target).SequenceEqual(data),
                "resumed transfer must commit the exact original bytes");
            Check(!File.Exists($"{target}.{offer.Id:N}.part.resume"),
                "successful resume must remove durable metadata");
        }
    }

    private static FileOffer Offer(string name, byte[] data, int extentSize = 65_536) =>
        new(Guid.NewGuid(), name, data.LongLength, extentSize, SHA256.HashData(data));

    private static async Task WriteExtents(TransferReceiver receiver, FileOffer offer, byte[] data)
    {
        var index = 0;
        for (var offset = 0; offset < data.Length; offset += offer.ExtentSize)
        {
            var length = Math.Min(offer.ExtentSize, data.Length - offset);
            var extent = data.AsSpan(offset, length).ToArray();
            await receiver.AcceptAsync(new FileExtent(offer.Id, index++, SHA256.HashData(extent), extent),
                CancellationToken.None);
        }
    }

    private static byte[] CreateBytes(int length, int seed)
    {
        var value = new byte[length];
        new Random(seed).NextBytes(value);
        return value;
    }

    private void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }
}
