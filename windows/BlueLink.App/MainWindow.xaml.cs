using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using BlueLink.Domain;
using BlueLink.Files;
using BlueLink.Session;

namespace BlueLink;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly MainViewModel _model;
    private bool _disposed;
    private System.Windows.Interop.HwndSource? _usbEventSource;
    internal MainViewModel ViewModel => _model;
    private System.Windows.Point? _attachmentDragStart;
    private ChatAttachment? _dragAttachment;
    private ICollectionView? _transferPanelView;
    private ScrollViewer? _messageScrollViewer;
    private bool _messagePinnedToBottom = true;
    internal Appearance.WindowSizePersistence WindowSizing { get; }
    internal SettingsPage? ActiveSettingsPage { get; private set; }

    public MainWindow(bool initializeRuntime = true, string? dataRoot = null)
    {
        _model = new MainViewModel(dataRoot);
        InitializeComponent();
        Closed += (_,_) => { if(_columnFilterPopup is { } popup) popup.IsOpen = false; };
        PreviewMouseDown += (_, args) => ClearMessageSelectionExcept(MessageTextAt(args.OriginalSource as DependencyObject));
        PreviewGotKeyboardFocus += (_, args) => ClearMessageSelectionExcept(MessageTextAt(args.NewFocus as DependencyObject));
        Deactivated += (_, _) => { if (_selectedMessageText?.ContextMenu?.IsOpen != true) ClearMessageSelectionExcept(null); };
        WindowSizing = new(this, "main", _model.DataDirectory);
        DataContext = _model;
        InitializeComposer();
        InitializeMessageBatch();
        InitializeMessageHistory();
        _model.TransientNoticeRequested += OnTransientNoticeRequested;
        ConfigureTransferView();
        _model.Messages.CollectionChanged += Messages_CollectionChanged;
        Loaded += (_, _) => AttachMessageScrollViewer();
        Activated += async (_, _) => { await _model.SetWindowFocusAsync(true); if (initializeRuntime) await _model.RefreshBluetoothStatusAsync(); };
        Deactivated += async (_, _) => await _model.SetWindowFocusAsync(false);
        _model.PropertyChanged += async (_, args) => { if (args.PropertyName == nameof(MainViewModel.ShowMessageSurface) && IsActive) await _model.SetWindowFocusAsync(true); };
        _model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.Settings)) Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateFileResultCount();
            }));
        };
        if (initializeRuntime) SourceInitialized += (_, _) =>
        {
            _usbEventSource = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            _usbEventSource?.AddHook(UsbDeviceChange);
        };
        if (initializeRuntime)
            Loaded += async (_, _) => await _model.InitializeAsync();
    }

    private IntPtr UsbDeviceChange(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_DEVICECHANGE / DBT_DEVNODES_CHANGED is broadcast even while the window is hidden in the tray.
        if (!_disposed && message == 0x0219 && wParam.ToInt64() is 0x0007 or 0x8000 or 0x8004)
            _model.RefreshUsbDiscovery();
        return IntPtr.Zero;
    }

    internal void LoadVisualFixture(bool expanded = true)
    {
        var now = DateTimeOffset.Now;
        var connected = new ConversationSummary("fixture-connected", "Navi 的 REDMI K80 Pro",
            PeerPlatform.Android, DeviceAvailability.Connected, Guid.NewGuid(), "5C:40:71:**:**:39", 2, now);
        var offline = new ConversationSummary("fixture-offline", "DESKTOP-OFFLINE",
            PeerPlatform.Windows, DeviceAvailability.Offline, null, "离线", 0, now.AddHours(-3));
        _model.ConnectedConversations.Add(connected);
        _model.OfflineConversations.Add(offline);
        _model.Conversations.Add(connected);
        _model.Conversations.Add(offline);
        _model.Sessions.Add(new SessionSnapshot(connected.SessionId!.Value, connected.PeerId,
            connected.PeerName, connected.TransportAddress, ConnectionPhase.Connected,
            "端到端加密 · BTX/1.1", now.AddMinutes(-8)));
        _model.Devices.Add(new NearbyDevice("fixture-nearby", "附近 Android 设备", "65:F6:1A:**:**:10",
            PeerPlatform.Android, -58, now, true));
        _model.Messages.Add(new ChatItem(Guid.NewGuid(), "蓝牙连接已建立，消息仅在设备间传输。", false,
            now.AddMinutes(-4), MessageStatus.Received));
        _model.Messages.Add(new ChatItem(Guid.NewGuid(), "收到，我正在验证文件传输。", true,
            now.AddMinutes(-3), MessageStatus.Delivered));
        var attachment = new ChatAttachment(Guid.NewGuid(), Guid.NewGuid(), "BlueLink-验收报告.pdf",
            "application/pdf", 1_572_864, null, "Transferring", null, 917_504, 164_000);
        _model.Messages.Add(new ChatItem(Guid.NewGuid(), "", false, now.AddMinutes(-2),
            MessageStatus.Received, ChatItemKind.File, [attachment]));
        var transfer = new TransferItem
        {
            Id = attachment.TransferId, Name = attachment.FileName, TotalBytes = attachment.Size,
            CompletedBytes = attachment.CompletedBytes, Outgoing = false, Status = TransferStatus.Transferring,
            MimeType = attachment.MimeType, PeerId = connected.PeerId
        };
        _model.Transfers.Add(transfer);
        _model.AllTransfers.Add(transfer);
        _model.UseVisualFixture(connected);
        MessageList.Items.Refresh();
        _model.ShowFiles = false;
    }

    private async void RefreshNearby_Click(object sender, RoutedEventArgs e) => await _model.ScanAsync();
    private async void Connect_Click(object sender, RoutedEventArgs e) => await _model.ConnectAsync();
    private void CancelConnection_Click(object sender, RoutedEventArgs e) => _model.CancelConnection();
    private async void ConnectDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NearbyDevice device }) return;
        _model.SelectedDevice = device;
        await _model.ConnectAsync();
    }
    private async void Send_Click(object sender, RoutedEventArgs e) => await SendCurrentAsync();
    private bool _composerImeActive;

    internal bool IsComposerSendKey(Key key, ModifierKeys modifiers) =>
        ComposerShortcuts.IsSendKey(_model.Settings.SendShortcut, key, modifiers, _composerImeActive);

    private async void MessageInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsComposerSendKey(e.Key, Keyboard.Modifiers)) return;
        // Mark handled before awaiting: RichTextBox otherwise inserts a newline first.
        e.Handled = true;
        if (!e.IsRepeat) await SendCurrentAsync();
    }

    private async void DraftInput_LostFocus(object sender, KeyboardFocusChangedEventArgs args) => await _model.FlushDraftsAsync();
    private async Task SendCurrentAsync()
    {
        if (_composerDrafts is null || _composerPeer is not { } peer || !_model.IsConnected || _composerImports.GetValueOrDefault(peer) > 0 || _sendingComposers.Contains(peer) || !PersistComposer()) return;
        _sendingComposers.Add(peer);
        try
        {
            var parts = _composerDrafts.Take(peer);
            _model.DraftText = ""; LoadComposer();
            foreach (var part in parts)
            {
                if (part.File is { } file) await _model.SendComposerFileAsync(peer, file.Path, file.Name);
                else if (!await _model.SendComposerTextAsync(peer, part.Text!)) throw new IOException("Text send failed");
                _composerDrafts.Acknowledge(peer, part.Id);
            }
            ScrollMessagesToBottom();
        }
        catch { ShowToast(Localization.Strings.Get("部分内容未发送，已保留在输入框中"), ToastLevel.Error); }
        finally
        {
            try { _composerDrafts.Restore(peer); if (_composerPeer == peer) { LoadComposer(); Composer_TextChanged(MessageInput, null!); } }
            catch { ShowToast(Localization.Strings.Get("输入草稿未能读取，请检查存储空间"), ToastLevel.Error); }
            _sendingComposers.Remove(peer);
        }
    }

    private void File_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = Localization.Strings.Get("添加附件"), Multiselect = true };
        if (picker.ShowDialog(this) == true) StageComposerFiles(picker.FileNames);
    }

    private void Attachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatAttachment attachment })
            FileInteractionService.Open(this, attachment, _model.Messages.SelectMany(message => message.Attachments ?? []));
    }

    private void Attachment_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatAttachment attachment } &&
            FileDragDropService.CanCopyOut(attachment))
        {
            _attachmentDragStart = e.GetPosition(this);
            _dragAttachment = attachment;
        }
        else
        {
            _attachmentDragStart = null;
            _dragAttachment = null;
        }
    }

    private void Attachment_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _attachmentDragStart is not { } origin ||
            _dragAttachment is not { } attachment || sender is not DependencyObject source) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _attachmentDragStart = null;
        _dragAttachment = null;
        if (sender is UIElement element) element.ReleaseMouseCapture();
        e.Handled = true;
        FileDragDropService.BeginCopyDrag(source, attachment);
    }

    private void ChatSurface_PreviewDragEnter(object sender, DragEventArgs e) => UpdateFileDropFeedback(e);

    private void ChatSurface_PreviewDragOver(object sender, DragEventArgs e) => UpdateFileDropFeedback(e);

    private void ChatSurface_PreviewDragLeave(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        FileDropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private async void ChatSurface_PreviewDrop(object sender, DragEventArgs e)
    {
        // Keep copy-out drags available to other applications, but never feed them back
        // into BlueLink's staging/sending or the RichTextBox's native drop handler.
        if (FileDragDropService.IsOutboundDrag(e.Data))
        {
            FileDropOverlay.Visibility = Visibility.Collapsed;
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = FileDragDropService.ExtractFilePaths(e.Data);
        FileDropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
        if (!ComposerDrafts.AcceptsDrop(files.Count))
        {
            e.Effects = DragDropEffects.None;
            if (files.Count > ComposerDrafts.MaximumDropFiles) ShowToast(Localization.Strings.Get("每次最多拖入10个文件，请分批添加"), ToastLevel.Warning);
            return;
        }
        if (IsComposerDrop(e))
        {
            e.Effects = _model.CanEditDraft ? DragDropEffects.Copy : DragDropEffects.None;
            if (_model.CanEditDraft)
            {
                var position = MessageInput.GetPositionFromPoint(e.GetPosition(MessageInput), true);
                if (position is not null) MessageInput.CaretPosition = position;
                StageComposerFiles(files);
            }
        }
        else
        {
            e.Effects = _model.IsConnected ? DragDropEffects.Copy : DragDropEffects.None;
            if (_model.IsConnected) await SendFilesAsync(files);
        }
    }

    private void UpdateFileDropFeedback(DragEventArgs e)
    {
        // Keep copy-out drags available to other applications, but never feed them back
        // into BlueLink's staging/sending or the RichTextBox's native drop handler.
        if (FileDragDropService.IsOutboundDrag(e.Data))
        {
            FileDropOverlay.Visibility = Visibility.Collapsed;
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = FileDragDropService.ExtractFilePaths(e.Data);
        var composer = IsComposerDrop(e);
        var accepted = ShowFileDropFeedback(files.Count, composer);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    internal bool ShowFileDropFeedback(int fileCount, bool composer = false)
    {
        var canSend = (composer ? _model.CanEditDraft : _model.IsConnected) && ComposerDrafts.AcceptsDrop(fileCount);
        FileDropOverlay.SetValue(Grid.RowProperty, composer ? 3 : 0);
        FileDropOverlay.SetValue(Grid.RowSpanProperty, composer ? 1 : 3);
        FileDropOverlay.Margin = composer ? new Thickness(0, 8, 0, 8) : new Thickness(-14, 0, -14, 0);
        FileDropOutline.Margin = composer ? new Thickness(0) : new Thickness(12);
        FileDropOutline.StrokeThickness = composer ? 1 : 2;
        FileDropBody.ColumnDefinitions[0].Width = composer ? new GridLength(32) : new GridLength(0);
        FileDropBody.RowDefinitions[0].Height = composer ? new GridLength(0) : GridLength.Auto;
        Grid.SetRow(FileDropIcon, composer ? 1 : 0);
        Grid.SetColumn(FileDropIcon, 0);
        Grid.SetColumnSpan(FileDropIcon, composer ? 1 : 2);
        FileDropIcon.Width = FileDropIcon.Height = composer ? 28 : 54;
        FileDropTitle.FontSize = (double)FindResource(composer ? "ClientFont14" : "ClientFont22");
        FileDropTitle.FontWeight = composer ? FontWeights.SemiBold : FontWeights.Bold;
        FileDropTitle.TextAlignment = composer ? TextAlignment.Left : TextAlignment.Center;
        FileDropTitle.Margin = composer ? new Thickness(0) : new Thickness(0, 16, 0, 0);
        FileDropDetail.FontSize = composer ? 12 : (double)FindResource("ClientFont14");
        FileDropDetail.TextAlignment = composer ? TextAlignment.Left : TextAlignment.Center;
        FileDropDetail.Margin = new Thickness(0, composer ? 4 : 10, 0, 0);
        FileDropOverlay.Visibility = Visibility.Visible;
        FileDropOutline.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, canSend ? "BlueBrush" : "WarningBrush");
        FileDropOutline.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, canSend ? "SoftBlueBrush" : "SoftWarningBrush");
        FileDropIcon.SetResourceReference(System.Windows.Controls.Image.SourceProperty, canSend ? "FigmaIcon-drop-send" : "FigmaIcon-drop-blocked");
        if (fileCount > ComposerDrafts.MaximumDropFiles)
        {
            FileDropTitle.Text = Localization.Strings.Get("每次最多拖入10个文件，请分批添加");
            FileDropDetail.Text = Localization.Strings.Get("本批文件不会发送或添加到输入框");
        }
        else if (composer && canSend)
        {
            FileDropIcon.SetResourceReference(System.Windows.Controls.Image.SourceProperty, "FigmaIcon-attachment");
            FileDropTitle.Text = Localization.Strings.Get("释放以添加待发送附件");
            FileDropDetail.Text = Localization.Strings.Get("可以继续输入文字，点击发送后才会发送");
        }
        else if (!_model.IsConnected)
        {
            FileDropTitle.Text = Localization.Strings.Get("设备未连接，无法发送文件");
            FileDropDetail.Text = Localization.Strings.Get("重新连接设备后再拖放文件");
        }
        else if (fileCount == 0)
        {
            FileDropTitle.Text = Localization.Strings.Get("这里只接受文件");
            FileDropDetail.Text = Localization.Strings.Get("文件夹不会被自动压缩或发送");
        }
        else
        {
            FileDropTitle.Text = Localization.Strings.Get("释放以发送文件");
            FileDropDetail.Text = Localization.Strings.Format($"文件将发送至 {_model.ActivePeerTitle}");
        }
        return canSend;
    }

    private async Task SendFilesAsync(IEnumerable<string> paths)
    {
        var files = FileDragDropService.NormalizeFilePaths(paths);
        if (!_model.IsConnected || files.Count == 0) return;
        var failures = new List<string>();
        await Task.WhenAll(files.Select(async path =>
        {
            try { await _model.SendFileAsync(path); }
            catch (OperationCanceledException) { }
            catch (Exception failure) { failures.Add($"{Path.GetFileName(path)}：{failure.Message}"); }
        }));
        if (failures.Count > 0)
            BlueLinkDialog.Show(this, "部分文件发送失败", string.Join(Environment.NewLine, failures),
                BlueLinkDialogTone.Error);
        ScrollMessagesToBottom();
    }

    private void AttachMessageScrollViewer()
    {
        _messageScrollViewer ??= FindVisualChild<ScrollViewer>(MessageList);
        if (_messageScrollViewer is null) return;
        _messageScrollViewer.ScrollChanged -= MessageScroll_Changed;
        _messageScrollViewer.ScrollChanged += MessageScroll_Changed;
        ScrollMessagesToBottom();
    }

    private void MessageScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        if (_messageScrollViewer is null || !ReferenceEquals(e.OriginalSource, _messageScrollViewer) || _restoringHistoryViewport || _model.IsPrependingMessages) return;
        if (e.VerticalChange < 0 && e.ExtentHeightChange == 0) QueueEarlierHistory();
        _messagePinnedToBottom = _messageScrollViewer.ScrollableHeight <= 1 ||
            _messageScrollViewer.VerticalOffset >= _messageScrollViewer.ScrollableHeight - 2;
        if (_messagePinnedToBottom) NewMessagesButton.Visibility = Visibility.Collapsed;
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset) { _messageScrollRevision++; _historyRetryAfter = default; return; }
        if (e.Action != NotifyCollectionChangedAction.Add || _model.IsPrependingMessages) return;
        var revision = _messageScrollRevision;
        var conversation = _model.MessageHistoryVersion;
        var outgoing = e.NewItems?.OfType<ChatItem>().Any(value => value.Outgoing) == true;
        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || revision != _messageScrollRevision || conversation != _model.MessageHistoryVersion || _restoringHistoryViewport) return;
            if (outgoing || _messagePinnedToBottom) ScrollMessagesToBottom();
            else NewMessagesButton.Visibility = Visibility.Visible;
        });
    }

    private void ScrollMessagesToBottom()
    {
        var revision = ++_messageScrollRevision;
        var conversation = _model.MessageHistoryVersion;
        _messagePinnedToBottom = true;
        NewMessagesButton.Visibility = Visibility.Collapsed;
        if (MessageList.Items.Count > 0)
            MessageList.ScrollIntoView(MessageList.Items[MessageList.Items.Count - 1]);
        Dispatcher.BeginInvoke(() =>
        {
            if (!_disposed && revision == _messageScrollRevision && conversation == _model.MessageHistoryVersion && !_restoringHistoryViewport)
                _messageScrollViewer?.ScrollToEnd();
        });
    }

    private void NewMessages_Click(object sender, RoutedEventArgs e) => ScrollMessagesToBottom();

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void ConfigureTransferView()
    {
        if (TransferList is null) return;
        _model.FilesAllDevices = _fileDevice != "@current" || _fileColumnSelections["Route"].Any(key => key.StartsWith("peer:") && key != "peer:@current");
        if (!_fileQuerySubscribed)
        {
            _fileQuerySubscribed = true;
            _model.AllTransfers.CollectionChanged += FileSourceChanged;
            TrackFileItems();
        }
        RefreshFileResults();
    }


    private static ChatAttachment TransferAttachment(TransferItem item) => new(
        item.AttachmentId ?? Guid.Empty, item.Id, item.Name, item.MimeType, item.TotalBytes,
        item.LocalPath, item.Status.ToString(), null, item.CompletedBytes, item.BytesPerSecond);

    private void TransferOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem item })
            FileInteractionService.Open(this, TransferAttachment(item), _fileResults.Select(TransferAttachment));
    }

    private void TransferMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is FrameworkElement { ContextMenu: not null } host)
            {
                host.ContextMenu.PlacementTarget = element;
                host.ContextMenu.IsOpen = true;
                return;
            }
            current = VisualTreeHelper.GetParent(current);
        }
    }

    private void TransferOpenMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem item })
            FileInteractionService.Open(this, TransferAttachment(item), _fileResults.Select(TransferAttachment));
    }

    private void TransferLocateMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem item })
            FileInteractionService.Locate(this, TransferAttachment(item));
    }

    private void TransferSaveAsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem item })
            FileInteractionService.SaveCopy(this, TransferAttachment(item));
    }

    private async void TransferInfoMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TransferItem item }) return;
        await ShowAttachmentInformationAsync(TransferAttachment(item), item);
    }

    private async void TransferDeleteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TransferItem item } || !item.CanDelete) return;
        if (!ConfirmationWindow.Show(this, ConfirmationDocument.DeleteRecord(Localization.Strings.Format($"确定删除“{item.Name}”的本机传输记录吗？")))) return;
        await WithToastAsync(() => _model.DeleteTransferAsync(item), "已删除本机记录", "删除本机记录失败");
        RefreshFileResults();
    }

    private async void PauseTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem transfer })
            await _model.PauseTransferAsync(transfer);
    }

    private async void ResumeTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem transfer })
            await _model.ResumeTransferAsync(transfer);
    }

    private async void CancelTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem transfer } &&
            ConfirmationWindow.Show(this, ConfirmationDocument.CancelTransfer(transfer.Name, transfer.Outgoing)))
            await _model.CancelTransferAsync(transfer);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    private void ChangeReceiveDirectory_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
        ActiveSettingsPage!.ShowFiles();
    }

    internal void OpenSettings(bool connections = false)
    {
        if (ActiveSettingsPage is null)
        {
            ActiveSettingsPage = new SettingsPage(_model);
            ActiveSettingsPage.LeaveRequested += LeaveSettings;
            SettingsHost.Content = ActiveSettingsPage;
        }
        _model.IsSettingsOpen = true;
        HomeWorkspace.Visibility = Visibility.Collapsed;
        SettingsHost.Visibility = Visibility.Visible;
        if (connections) ActiveSettingsPage.ShowConnections();
        ActiveSettingsPage.Focus();
    }

    private void LeaveSettings() => TryCloseSettings();
    internal bool TryCloseSettings()
    {
        if (ActiveSettingsPage is null) return true;
        if (!ActiveSettingsPage.ConfirmLeave()) return false;
        ActiveSettingsPage.Dispose();
        ActiveSettingsPage = null;
        SettingsHost.Content = null;
        SettingsHost.Visibility = Visibility.Collapsed;
        HomeWorkspace.Visibility = Visibility.Visible;
        _model.IsSettingsOpen = false;
        _ = _model.SetWindowFocusAsync(IsActive);
        RefreshFileResults();
        MessageList.Items.Refresh();
        return true;
    }
    private void AllTransfers_Click(object sender, RoutedEventArgs e) => ShowGlobalFiles();

    private async void Conversation_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Binding refreshes must not reopen a conversation or reset the file workspace.
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is ConversationSummary item &&
            !string.Equals(item.PeerId, _model.ActivePeerId, StringComparison.OrdinalIgnoreCase))
            await OpenConversationAsync(item);
    }

    private async void ConversationCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConversationSummary item } && _model.ShowFiles &&
            string.Equals(item.PeerId, _model.ActivePeerId, StringComparison.OrdinalIgnoreCase))
        {
            // Clicking an already-selected item does not raise SelectionChanged.
            e.Handled = true;
            await OpenConversationAsync(item);
        }
    }

    private async void ConversationList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || sender is not ListBox { SelectedItem: ConversationSummary item }) return;
        e.Handled = true;
        await OpenConversationAsync(item);
    }

    internal async Task OpenConversationAsync(ConversationSummary item)
    {
        _model.ShowFiles = false;
        MessagesViewButton.IsChecked = true;
        _messagePinnedToBottom = true;
        NewMessagesButton.Visibility = Visibility.Collapsed;
        await _model.SelectConversationAsync(item.PeerId);
        ScrollMessagesToBottom();
    }

    private async void ConversationOpenMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConversationSummary item })
            await OpenConversationAsync(item);
    }

    private async void ConversationConnectMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConversationSummary item })
            await _model.ConnectConversationAsync(item);
    }

    private async void ConversationDisconnectMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConversationSummary item })
            await _model.DisconnectConversationAsync(item);
    }

    private void ConversationInfoMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ConversationSummary item }) return;
        InformationWindow.Show(this, _model.DescribeDevice(item));
    }

    private async void ConversationClearMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ConversationSummary item }) return;
        if (!ConfirmationWindow.Show(this, ConfirmationDocument.ClearConversation(item.PeerName))) return;
        await WithToastAsync(() => _model.ClearConversationAsync(item.PeerId), "已清空本机会话", "清空本机会话失败");
    }

    private async void ConversationForgetMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ConversationSummary item }) return;
        if (!ConfirmationWindow.Show(this, ConfirmationDocument.RemoveTrust(item.PeerName))) return;
        await WithToastAsync(() => _model.ForgetPeerAsync(item.PeerId), "已移除设备信任", "移除设备信任失败");
    }

    private void MessageCopyMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatItem item }) return;
        var value = item.HasText ? item.Text : string.Join(Environment.NewLine, item.Attachments?.Select(x => x.FileName) ?? []);
        if (sender is MenuItem menuItem && ItemsControl.ItemsControlFromItemContainer(menuItem) is ContextMenu menu &&
            menu.PlacementTarget is System.Windows.Controls.TextBox { SelectionLength: > 0 } text)
            value = text.SelectedText;
        if (string.IsNullOrWhiteSpace(value)) return;
        try { System.Windows.Clipboard.SetText(value); ShowToast("已复制到剪贴板", ToastLevel.Success); }
        catch (Exception) { ShowToast("复制失败，请稍后重试", ToastLevel.Error); }
    }

    private void MessageInfoMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatItem item }) return;
        BlueLinkDialog.Show(this, "消息详情",
            $"方向：{(item.Outgoing ? "发送" : "接收")}{Environment.NewLine}" +
            $"时间：{item.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
            $"状态：{item.StatusText}{Environment.NewLine}" +
            $"消息标识：{item.Id:N}");
    }

    private async void MessageDeleteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatItem item }) return;
        if (!ConfirmationWindow.Show(this, ConfirmationDocument.DeleteRecord(Localization.Strings.Get("确定删除这条本机消息记录吗？")))) return;
        await WithToastAsync(() => _model.DeleteMessageAsync(item), "已删除本机记录", "删除本机记录失败");
    }

    private void AttachmentOpenMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatAttachment attachment }) FileInteractionService.Open(this, attachment, _model.Messages.SelectMany(message => message.Attachments ?? []));
    }

    private void AttachmentLocateMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatAttachment attachment }) FileInteractionService.Locate(this, attachment);
    }

    private void AttachmentSaveAsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatAttachment attachment }) FileInteractionService.SaveCopy(this, attachment);
    }

    private void AttachmentCopyMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatAttachment attachment }) FileInteractionService.CopyToClipboard(this, attachment);
    }

    private async void AttachmentInfoMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatAttachment attachment })
            await ShowAttachmentInformationAsync(attachment);
    }

    private async void AttachmentDeleteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatAttachment attachment }) return;
        if (!ConfirmationWindow.Show(this, ConfirmationDocument.DeleteRecord(Localization.Strings.Format($"确定删除“{attachment.FileName}”所在的本机消息记录吗？")))) return;
        await WithToastAsync(() => _model.DeleteAttachmentMessageAsync(attachment), "已删除本机记录", "删除本机记录失败");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (ActiveSettingsPage is { CanLeave: false }) { e.Cancel = true; return; }
        if (Application.Current is App { ExitRequested: false })
        {
            e.Cancel = true;
            if (_model.Settings.KeepBackgroundSessions) Hide();
            else Dispatcher.BeginInvoke(new Action(((App)Application.Current).RequestExit));
            return;
        }
        base.OnClosing(e);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _messageScrollRevision++;
        if (_messageScrollViewer is not null) _messageScrollViewer.ScrollChanged -= MessageScroll_Changed;
        _messageSelectionGesture?.Dispose();
        _fileSelectionGesture?.Dispose();
        _usbEventSource?.RemoveHook(UsbDeviceChange);
        _usbEventSource = null;
        _model.TransientNoticeRequested -= OnTransientNoticeRequested;
        Title = Localization.Strings.Get("正在结束传输，请稍候…");
        IsEnabled = false;
        ActiveSettingsPage?.Dispose();
        _model.Messages.CollectionChanged -= Messages_CollectionChanged;
        _fileCancellation?.Cancel(); _fileRevision++;
        _model.AllTransfers.CollectionChanged -= FileSourceChanged;
        foreach (var item in _trackedFileItems) item.PropertyChanged -= FileItemChanged;
        try
        {
            await _model.DisposeAsync();
        }
        finally
        {
            Toasts.Dispose();
            Close();
        }
    }
}
