using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Text;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlueLink;
using BlueLink.Domain;
using BlueLink.Security;
using BlueLink.Session;
using BlueLink.Storage;

internal static partial class BackgroundConversationVerification
{
    public static void Run(string outputDirectory, bool thumbnailsOnly = false)
    {
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Application? app = null;
            MainWindow? window = null;
            try
            {
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                app = App.CreateResourceOnlyHost();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                app.DispatcherUnhandledException += (_, args) => { failure ??= args.Exception; args.Handled = true; };
                var directory = Path.Combine(output, "isolated-" + Guid.NewGuid().ToString("N"));
                var db = new BlueLinkDatabase(directory, directory);
                Wait(db.InitializeAsync(new IdentityStore(directory)));
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var id in new[] { "qa-offline-a", "qa-offline-b" })
                {
                    Wait(db.UpsertPeerAsync(new(id, "同名 Android 设备", "Android", StoredTrustState.Unknown, null,
                        now, now, now, "AA:BB:CC:DD:EE:FF")));
                    Wait(db.UpsertConversationAsync(new("peer:" + id, id, now, 0)));
                    Wait(db.UpsertMessageAsync(new(Guid.NewGuid().ToString("N"), "peer:" + id, id,
                        StoredMessageDirection.Incoming, StoredMessageType.Text, id + " 的独立历史", "Received", now, 1)));
                }
                var foreground = GetForegroundWindow();
                window = new MainWindow(initializeRuntime: false, dataRoot: directory)
                {
                    Width = 1180, Height = 720, WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = SystemParameters.VirtualScreenLeft - 4000, Top = SystemParameters.VirtualScreenTop - 4000,
                    ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
                };
                Wait(window.ViewModel.InitializeLocalStateAsync());
                window.ViewModel.Devices.Add(new("nearby", "同名 Android 设备", "AA:BB:CC:DD:EE:FF", PeerPlatform.Android));
                Wait(window.ViewModel.SaveSettingsAsync(window.ViewModel.Settings with { Theme = "light" }));
                window.Show(); Drain();
                var model = window.ViewModel;
                var identityProbe = model.OfflineConversations[0];
                var hashBefore = identityProbe.GetHashCode();
                PropertyChangedEventHandler observer = (_, _) => { };
                identityProbe.PropertyChanged += observer;
                var hashSubscribed = identityProbe.GetHashCode();
                identityProbe.PropertyChanged -= observer;
                File.WriteAllText(Path.Combine(output, "selection-identity.json"), JsonSerializer.Serialize(new { hashBefore, hashSubscribed }));
                Check(hashBefore == hashSubscribed, "WPF binding subscriptions cannot change a conversation item's hash identity");
                var list = (ListBox)window.FindName("OfflineDeviceList");
                var root = (FrameworkElement)window.Content;
                Check(!window.IsActive && GetForegroundWindow() == foreground &&
                    window.Left + window.ActualWidth < SystemParameters.VirtualScreenLeft && window.Opacity == 0,
                    "native window remains transparent, outside desktop and non-activating");
                if (!thumbnailsOnly)
                {
                    var header = (Border)window.FindName("WorkspaceHeader");
                    var files = (RadioButton)window.FindName("FilesViewButton");
                    var messages = (RadioButton)window.FindName("MessagesViewButton");
                    var hit = root.InputHitTest(files.TranslatePoint(new Point(files.ActualWidth / 2, files.ActualHeight / 2), root));
                    Check(hit is DependencyObject target && (ReferenceEquals(target, files) || files.IsAncestorOf(target)),
                        "no-selection placeholder cannot cover or intercept the file entry");
                    Check(!model.HasActiveConversation && header.IsVisible && header.ActualHeight == 72 && files.IsVisible,
                        "no selection retains the workspace header and visible file entry");
                    Capture(root, Path.Combine(output, "workspace-no-selection.png"));
                    ((ISelectionItemProvider)new RadioButtonAutomationPeer(files).GetPattern(PatternInterface.SelectionItem)!).Select(); Drain();
                    Check(model.ShowFiles && model.FilesAllDevices && !model.HasActiveConversation && header.IsVisible,
                        "workspace file entry opens all devices without selecting a conversation");
                    Capture(root, Path.Combine(output, "workspace-no-selection-files.png"));
                    ((ISelectionItemProvider)new RadioButtonAutomationPeer(messages).GetPattern(PatternInterface.SelectionItem)!).Select(); Drain();
                    Check(model.ShowConversationPlaceholder && header.IsVisible && files.IsVisible,
                        "returning to messages preserves the no-selection file entry");
                }
                Select(list, "qa-offline-a");
                Until(() => model.Messages.Any(m => m.Text.StartsWith("qa-offline-a")));
                Wait(model.SelectConversationAsync("qa-offline-b")); Drain();
                Check(list.SelectedItem is ConversationSummary { PeerId: "qa-offline-b" },
                    "programmatic conversation change synchronizes the actual list selection");
                Select(list, "qa-offline-a");
                Until(() => model.Messages.Any(m => m.Text.StartsWith("qa-offline-a")));
                Check(model.ActivePeerId == "qa-offline-a" && model.Messages.Count == 1,
                    "UI Automation reselects the previous same-name device and loads only its history");
                if (thumbnailsOnly)
                {
                    VerifyChatThumbnailGeometry(window, root, output, Check);
                    Check(GetForegroundWindow() == foreground && !window.IsActive,
                        $"thumbnail checks preserve desktop focus: active={window.IsActive}, initial={foreground}, current={GetForegroundWindow()}");
                    return;
                }
                Wait(Task.WhenAll(model.SelectConversationAsync("qa-offline-a"), model.SelectConversationAsync("qa-offline-b"),
                    model.SelectConversationAsync("qa-offline-a")));
                Check(model.ActivePeerId == "qa-offline-a" && model.Messages.Count == 1 && model.Messages[0].Text.StartsWith("qa-offline-a"),
                    "overlapping A-B-A history loads cannot mix or overwrite the latest conversation");

                window.OpenFileWorkspace(allDevices: true); Drain();
                Wait(model.SaveSettingsAsync(model.Settings)); Drain();
                Check(model.ShowFiles && model.FilesAllDevices, "refresh does not navigate away from the global file workspace");
                var card = Card(list, "qa-offline-a");
                card.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
                Until(() => !model.ShowFiles);
                Check(model.ActivePeerId == "qa-offline-a", "clicking the already-selected card returns from global files to its conversation");
                foreach (var key in new[] { Key.Enter, Key.Space })
                {
                    window.OpenFileWorkspace(allDevices: true); Drain();
                    list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, key)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    Until(() => !model.ShowFiles);
                    Check(model.ActivePeerId == "qa-offline-a", "keyboard reopens the selected conversation: " + key);
                }

                foreach (var theme in new[] { "light", "dark" })
                {
                    Wait(model.SaveSettingsAsync(model.Settings with { Theme = theme })); Drain();
                    Check(list.SelectedItem is ConversationSummary { PeerId: "qa-offline-a" },
                        "refresh preserves the active selection: " + theme);
                    foreach (var id in new[] { "qa-offline-b", "qa-offline-a" })
                    {
                        Select(list, id); Until(() => model.Messages.Count == 1 && model.Messages[0].Text.StartsWith(id), () => $"wanted={id}; active={model.ActivePeerId}; selected={(list.SelectedItem as ConversationSummary)?.PeerId}; messages={string.Join(",", model.Messages.Select(m => m.Text))}");
                        card = Card(list, id);
                        var dot = Descendants<System.Windows.Shapes.Ellipse>(card).Single(n => n.Name == "StateDot");
                        var tile = Descendants<Border>(card).Single(n => n.Name == "ConversationIconTile");
                        var glyph = Descendants<Border>(card).Single(n => n.Name == "OfflinePlatformIcon");
                        Check(dot.Fill == root.FindResource("PanelAEB7C6Brush") && tile.Background == root.FindResource("PanelF8FAFDBrush") &&
                            glyph.Background == root.FindResource("MutedBrush") && glyph.Visibility == Visibility.Visible,
                            "discoverable offline device retains prototype gray dot, tile and glyph: " + theme + "/" + id);
                        Check(card.Background == root.FindResource("OfflineSelectedBrush") &&
                            card.BorderBrush == root.FindResource("OfflineSelectedBorderBrush"),
                            "selected offline card uses prototype palette: " + theme + "/" + id);
                    }
                    Capture(root, Path.Combine(output, "offline-" + theme + ".png"));
                    VerifyMessageRowBackgrounds(window, root, output, theme, Check);
                    VerifyKnownDeviceDiscovery(window, root, output, theme, Check);
                }
                VerifyTransferSettings(window, root, output, Check);
                VerifyNearbyRefresh(window, Check);
                model.Sessions.Add(new SessionSnapshot(Guid.NewGuid(), "qa-offline-b", "同名 Android 设备", "test-only",
                    ConnectionPhase.Connected, "isolated", DateTimeOffset.UtcNow));
                Wait(model.SaveSettingsAsync(model.Settings)); Drain();
                var connected = (ListBox)window.FindName("ConnectedDeviceList");
                Select(connected, "qa-offline-b"); Until(() => model.Messages.Any(m => m.Text.StartsWith("qa-offline-b")));
                Check(model.ActivePeerId == "qa-offline-b" && model.IsConnected && list.SelectedIndex == -1,
                    $"online selection clears the offline list and displays its own history: active={model.ActivePeerId}, connected={model.IsConnected}, offlineSelected={list.SelectedItem}, onlineSelected={connected.SelectedItem}");
                Select(list, "qa-offline-a"); Until(() => model.Messages.Count == 1 && model.Messages[0].Text.StartsWith("qa-offline-a"));
                Check(!model.IsConnected && connected.SelectedIndex == -1,
                    "returning offline clears the connected selection and disables sending");
                Check(GetForegroundWindow() == foreground && !window.IsActive, "all selection checks preserve desktop focus");
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                if (window is not null) { window.Close(); Wait(window.DisposeAsync().AsTask()); }
                app?.Shutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60))) throw new TimeoutException("Background conversation acceptance timed out.");
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
        { checks, failure = failure?.ToString(), physicalInputSent = false, nativeWindow = "transparent, outside virtual desktop, non-activating" }, new JsonSerializerOptions { WriteIndented = true }));
        if (failure is not null) throw new InvalidOperationException("Background conversation acceptance failed.", failure);
        Console.WriteLine($"Background conversation acceptance passed: {checks.Count} checks, {Directory.GetFiles(output, "*.png").Length} screenshots, native UI Automation, no physical input.");

        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            checks.Add(label);
        }
    }

    private static void VerifyKnownDeviceDiscovery(MainWindow window, FrameworkElement root, string output,
        string theme, Action<bool, string> check)
    {
        var model = window.ViewModel;
        var original = model.Devices.ToArray();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var completed = typeof(MainViewModel).GetField("_scanCompleted", flags)!;
        var resetting = typeof(MainViewModel).GetField("_resettingIdentity", flags)!;
        var originalCompleted = completed.GetValue(model);
        var originalResetting = resetting.GetValue(model);
        var apply = typeof(MainViewModel).GetMethod("ApplyDevices", flags)!;
        var refresh = typeof(MainViewModel).GetMethod("RefreshConversations", flags)!;
        var list = (ListBox)window.FindName("OfflineDeviceList");
        void Discovered(params NearbyDevice[] devices)
        {
            apply.Invoke(model, new object[] { devices });
            refresh.Invoke(model, null); Drain();
        }
        MenuItem ConnectItem()
        {
            var card = Card(list, "qa-offline-a");
            card.ContextMenu.PlacementTarget = card;
            card.ContextMenu.ApplyTemplate(); Drain();
            return card.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Name == "ConversationConnectMenu");
        }
        try
        {
            // Exercise the production menu and routing while explicitly blocking hardware dialing.
            resetting.SetValue(model, true);
            completed.SetValue(model, true);
            var known = new NearbyDevice("known-ble", "同名 Android 设备", "AA:BB:CC:DD:EE:FF", PeerPlatform.Android,
                LastSeen: DateTimeOffset.UtcNow, CanInitiate: true);
            Discovered(known);
            check(model.NearbyNewDevices.Count == 0 && model.OfflineConversations.All(peer => peer.IsConnectable),
                "known advertisements stay associated with history: " + theme);
            check(model.ScanFeedback == "扫描完成，发现 1 台设备（新设备 0 台）",
                "one known advertisement counts once even with two historical identities: " + theme);
            var connect = ConnectItem();
            check(connect.IsEnabled && connect.Visibility == Visibility.Visible,
                "discoverable historical peer exposes enabled connect menu: " + theme);
            var invoke = (IInvokeProvider)new MenuItemAutomationPeer(connect).GetPattern(PatternInterface.Invoke)!;
            invoke.Invoke(); Until(() => model.SelectedDevice?.Id == known.Id);
            check(!model.CanConnectSelected, "native connect menu routes to discovered endpoint without hardware dialing: " + theme);
            Capture(root, Path.Combine(output, "known-discovery-" + theme + ".png"));
            Discovered();
            check(model.ScanFeedback == "扫描完成，未发现附近设备" && !ConnectItem().IsEnabled,
                "vanished peer cannot connect and empty scan is explicit: " + theme);
            Discovered(known, new NearbyDevice("new-ble", "新的设备", "11:22:33:44:55:66", PeerPlatform.Android,
                LastSeen: DateTimeOffset.UtcNow, CanInitiate: true));
            check(model.NearbyNewDevices.Count == 1 && model.ScanFeedback == "扫描完成，发现 2 台设备（新设备 1 台）",
                "discovery feedback distinguishes all devices from new devices: " + theme);
        }
        finally
        {
            completed.SetValue(model, originalCompleted);
            resetting.SetValue(model, originalResetting);
            Discovered(original);
        }
    }

    private static void VerifyMessageRowBackgrounds(MainWindow window, FrameworkElement root, string output,
        string theme, Action<bool, string> check)
    {
        var messages = (ListBox)window.FindName("MessageList");
        var outgoing = new ChatItem(Guid.NewGuid(), "发出的消息保留蓝色气泡", true, DateTimeOffset.Now, MessageStatus.Delivered);
        window.ViewModel.Messages.Add(outgoing);
        var wrapped = new ChatItem(Guid.NewGuid(), "多行文本选择：中文和 emoji 📱\n" + string.Concat(Enumerable.Repeat("Windows Android selectable text 中文换行测试。", 5)), false, DateTimeOffset.Now, MessageStatus.Received);
        window.ViewModel.Messages.Add(wrapped);
        Drain(); messages.UpdateLayout();
        var hoverKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        try
        {
            foreach (var message in messages.Items.Cast<ChatItem>().ToArray())
            {
                messages.ScrollIntoView(message); Drain();
                var row = (ListBoxItem)messages.ItemContainerGenerator.ContainerFromItem(message);
                row.ApplyTemplate(); Drain();
                var bubble = Descendants<Border>(row).Single(b => b.Name == "Bubble");
                var text = Descendants<System.Windows.Controls.TextBox>(bubble).Single(n => n.Name == "MessageText");
                var provider = (ITextProvider)new TextBoxAutomationPeer(text).GetPattern(PatternInterface.Text);
                var range = provider.DocumentRange.Clone();
                range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
                range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 3);
                range.Select(); Drain();
                check(text.SelectedText == message.Text[..3] && provider.GetSelection().Single().GetText(-1) == message.Text[..3],
                    "native text range selects only part of the message: " + theme);
                check(text.IsReadOnly && text.Cursor == Cursors.IBeam, "message supports text selection without editing: " + theme);
                File.WriteAllText(Path.Combine(output, $"text-template-{theme}.xaml"), System.Windows.Markup.XamlWriter.Save(text.Template));
                var beforeSize = bubble.RenderSize;
                var context = text.ContextMenu;
                context.Opacity = 0; context.PlacementTarget = text;
                context.IsOpen = true; Drain();
                check(bubble.RenderSize == beforeSize && text.SelectedText == message.Text[..3],
                    "opening message context menu preserves bubble geometry and text selection: " + theme);
                var copy = context.Items.OfType<MenuItem>().First();
                ((IInvokeProvider)new MenuItemAutomationPeer(copy).GetPattern(PatternInterface.Invoke)).Invoke(); Drain();
                check(Clipboard.GetText() == message.Text[..3], "active selection copies only its current range: " + theme);
                context.IsOpen = false; Drain();
                check(bubble.RenderSize == beforeSize, "closing message menu preserves geometry: " + theme);
                range.Select(); Drain();
                root.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseDownEvent }); Drain();
                check(text.SelectionLength == 0, "clicking non-focusable conversation space clears the actual text selection: " + theme);
                text.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                    { RoutedEvent = UIElement.PreviewMouseDownEvent });
                context.PlacementTarget = text; context.IsOpen = true; Drain();
                ((IInvokeProvider)new MenuItemAutomationPeer(copy).GetPattern(PatternInterface.Invoke)).Invoke(); Drain();
                check(Clipboard.GetText() == message.Text, "right-click copy after outside press copies the full message, never the stale range: " + theme);
                context.IsOpen = false; Drain();
                range.Select(); Drain();
                var composer = (TextBox)window.FindName("MessageInput");
                composer.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, text, composer)
                    { RoutedEvent = Keyboard.PreviewGotKeyboardFocusEvent }); Drain();
                check(text.SelectionLength == 0, "moving keyboard focus to the composer clears the old range: " + theme);
                check(bubble.RenderSize == beforeSize, "selection lifecycle never changes bubble geometry: " + theme);
                text.Select(0, 0);
                var bubbleBackground = bubble.Background;
                var backgrounds = Descendants<Border>(row).Where(b => ReferenceEquals(b.TemplatedParent, row)).ToArray();
                check(backgrounds.Length > 0, "message test inspects the official item template: " + theme);
                foreach (var selected in new[] { false, true })
                foreach (var hover in new[] { false, true })
                {
                    messages.UnselectAll();
                    if (selected)
                        ((ISelectionItemProvider)new ListBoxItemAutomationPeer(message, new ListBoxAutomationPeer(messages))
                            .GetPattern(PatternInterface.SelectionItem)).Select();
                    row.SetValue(hoverKey, hover); Drain();
                    var label = $"message-{theme}-{(message.Outgoing ? "outgoing" : "incoming")}-{selected}-{hover}";
                    check(row.IsSelected == selected, "UIA selection remains functional: " + label);
                    check(backgrounds.All(b => b.Background is null or SolidColorBrush { Color.A: 0 }),
                        "message template never adds a row background: " + label);
                    check(ReferenceEquals(bubble.Background, bubbleBackground), "message bubble retains its own color: " + label);
                    Capture(root, Path.Combine(output, label + ".png"));
                }
                row.SetValue(hoverKey, false);
            }
            var send = Descendants<Wpf.Ui.Controls.Button>(root).Single(b =>
                b.Content?.ToString() == "发送" && b.Style is not null);
            var offlineSend = Descendants<Wpf.Ui.Controls.Button>(root).Single(b => b.Content?.ToString() == "离线");
            offlineSend.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            var originalVisibility = send.ReadLocalValue(UIElement.VisibilityProperty);
            send.SetValue(UIElement.VisibilityProperty, Visibility.Visible);
            send.SetCurrentValue(UIElement.IsEnabledProperty, true);
            send.ApplyTemplate(); Drain();
            foreach (var hover in new[] { false, true }) {
                send.SetValue(hoverKey, hover); Drain();
                var borders = Descendants<Border>(send).Where(b => b.TemplatedParent == send && b.Background is SolidColorBrush).ToArray();
                var blue = ((SolidColorBrush)root.FindResource("BlueBrush")).Color;
                Capture(root, Path.Combine(output, $"send-probe-{theme}-{hover}.png"));
                File.WriteAllText(Path.Combine(output, $"send-probe-{theme}-{hover}.json"), JsonSerializer.Serialize(new {
                    enabled = send.IsEnabled, background = send.Background.ToString(), foreground = send.Foreground.ToString(),
                    size = send.RenderSize.ToString(), borders = Descendants<Border>(send).Select(b => new { b.Name, parent = b.TemplatedParent?.GetType().Name, background = b.Background?.ToString() }).ToArray()
                }));
                check(borders.Any(b => ((SolidColorBrush)b.Background).Color == blue), "send button retains accent surface while hovered=" + hover + ": " + theme);
                check(send.Foreground is SolidColorBrush foreground && foreground.Color == ((SolidColorBrush)root.FindResource("OnAccentBrush")).Color,
                    "send text stays legible while hovered=" + hover + ": " + theme);
                Capture(root, Path.Combine(output, $"send-{theme}-{hover}.png"));
            }
            offlineSend.GetBindingExpression(UIElement.VisibilityProperty)?.UpdateTarget();
            send.SetValue(hoverKey, false);
            send.GetBindingExpression(UIElement.IsEnabledProperty)?.UpdateTarget();
            if (originalVisibility == DependencyProperty.UnsetValue) send.ClearValue(UIElement.VisibilityProperty);
            else send.SetValue(UIElement.VisibilityProperty, originalVisibility);
        }
        finally
        {
            messages.UnselectAll();
            window.ViewModel.Messages.Remove(outgoing);
            window.ViewModel.Messages.Remove(wrapped);
            Drain();
        }
    }

    private static void Select(ListBox list, string id)
    {
        var index = list.Items.Cast<ConversationSummary>().ToList().FindIndex(n => n.PeerId == id);
        list.UpdateLayout(); Drain();
        // The invisible host does not receive desktop UIA cache invalidation after item regeneration.
        var peer = new ListBoxAutomationPeer(list);
        ((ISelectionItemProvider)peer.GetChildren()[index].GetPattern(PatternInterface.SelectionItem)).Select();
        Drain();
    }

    private static Border Card(ListBox list, string id)
    {
        var item = list.Items.Cast<ConversationSummary>().Single(n => n.PeerId == id);
        return Descendants<Border>((DependencyObject)list.ItemContainerGenerator.ContainerFromItem(item)).Single(n => n.Name == "ConversationCard");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Capture(FrameworkElement root, string path)
    {
        root.UpdateLayout(); Drain();
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static void Wait(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Until(Func<bool> condition, Func<string>? detail = null)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition()) { if (deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Expected UI state was not reached. " + detail?.Invoke()); Drain(); Thread.Sleep(1); }
        Drain();
    }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
