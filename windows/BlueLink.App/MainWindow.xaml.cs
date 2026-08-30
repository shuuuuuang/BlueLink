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

public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private bool _disposed;
    private System.Windows.Point? _attachmentDragStart;
    private ChatAttachment? _dragAttachment;
    private ICollectionView? _transferPanelView;
    private string _transferFilter = "All";
    private ScrollViewer? _messageScrollViewer;
    private bool _messagePinnedToBottom = true;

    public MainWindow(bool initializeRuntime = true, string? dataRoot = null)
    {
        _model = new MainViewModel(dataRoot);
        InitializeComponent();
        DataContext = _model;
        ConfigureTransferView();
        _model.Messages.CollectionChanged += Messages_CollectionChanged;
        Loaded += (_, _) => AttachMessageScrollViewer();
        if (initializeRuntime)
            Loaded += async (_, _) => { await _model.InitializeAsync(); ApplyTransferPanel(_model.Settings.TransferPanelExpanded); };
        else
            ApplyTransferPanel(expanded: true);
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
        ApplyTransferPanel(expanded);
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await _model.ScanAsync();
    private async void Connect_Click(object sender, RoutedEventArgs e) => await _model.ConnectAsync();
    private void CancelConnection_Click(object sender, RoutedEventArgs e) => _model.CancelConnection();
    private async void ConnectDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NearbyDevice device }) return;
        _model.SelectedDevice = device;
        await _model.ConnectAsync();
    }
    private async void Send_Click(object sender, RoutedEventArgs e) => await SendCurrentAsync();
    private async void MessageInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; await SendCurrentAsync(); }
    }

    private async Task SendCurrentAsync()
    {
        var text = MessageInput.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        MessageInput.Clear();
        await _model.SendAsync(text);
        ScrollMessagesToBottom();
    }

    private async void File_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "选择要通过蓝牙发送的文件", Multiselect = true };
        if (picker.ShowDialog(this) == true) await SendFilesAsync(picker.FileNames);
    }

    private void Attachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatAttachment attachment })
            FileInteractionService.Open(this, attachment);
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
        FileDropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private async void ChatSurface_PreviewDrop(object sender, DragEventArgs e)
    {
        var files = FileDragDropService.ExtractFilePaths(e.Data);
        FileDropOverlay.Visibility = Visibility.Collapsed;
        e.Effects = _model.IsConnected && files.Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (_model.IsConnected && files.Count > 0) await SendFilesAsync(files);
    }

    private void UpdateFileDropFeedback(DragEventArgs e)
    {
        var files = FileDragDropService.ExtractFilePaths(e.Data);
        var canSend = _model.IsConnected && files.Count > 0;
        FileDropOverlay.Visibility = Visibility.Visible;
        FileDropOverlay.BorderBrush = (System.Windows.Media.Brush)FindResource(canSend ? "BlueBrush" : "WarningBrush");
        if (!_model.IsConnected)
        {
            FileDropTitle.Text = "设备离线，无法发送文件";
            FileDropDetail.Text = "重新连接设备后再拖入文件";
        }
        else if (files.Count == 0)
        {
            FileDropTitle.Text = "这里只接受文件";
            FileDropDetail.Text = "文件夹不会被自动压缩或发送";
        }
        else
        {
            FileDropTitle.Text = $"释放以发送给 {_model.ActivePeerTitle}";
            FileDropDetail.Text = $"{files.Count} 个文件 · {FileDragDropService.FormatBytes(FileDragDropService.TotalBytes(files))}";
        }
        e.Effects = canSend ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async Task SendFilesAsync(IEnumerable<string> paths)
    {
        var files = FileDragDropService.NormalizeFilePaths(paths);
        if (!_model.IsConnected || files.Count == 0) return;
        var failures = new List<string>();
        foreach (var path in files)
        {
            if (!_model.IsConnected)
            {
                failures.Add($"{Path.GetFileName(path)}：设备连接已断开");
                continue;
            }
            try { await _model.SendFileAsync(path); }
            catch (Exception failure) { failures.Add($"{Path.GetFileName(path)}：{failure.Message}"); }
        }
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
        if (_messageScrollViewer is null) return;
        _messagePinnedToBottom = _messageScrollViewer.ScrollableHeight <= 1 ||
            _messageScrollViewer.VerticalOffset >= _messageScrollViewer.ScrollableHeight - 2;
        if (_messagePinnedToBottom) NewMessagesButton.Visibility = Visibility.Collapsed;
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset) return;
        var outgoing = e.NewItems?.OfType<ChatItem>().Any(value => value.Outgoing) == true;
        Dispatcher.BeginInvoke(() =>
        {
            if (outgoing || _messagePinnedToBottom) ScrollMessagesToBottom();
            else NewMessagesButton.Visibility = Visibility.Visible;
        });
    }

    private void ScrollMessagesToBottom()
    {
        _messagePinnedToBottom = true;
        NewMessagesButton.Visibility = Visibility.Collapsed;
        if (MessageList.Items.Count > 0)
            MessageList.ScrollIntoView(MessageList.Items[MessageList.Items.Count - 1]);
        Dispatcher.BeginInvoke(() => _messageScrollViewer?.ScrollToEnd());
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
        var source = AllTransfersTab?.IsChecked == true ? _model.AllTransfers : _model.Transfers;
        _transferPanelView = CollectionViewSource.GetDefaultView(source);
        _transferPanelView.Filter = value => value is TransferItem transfer && TransferMatchesFilter(transfer);
        _transferPanelView.GroupDescriptions.Clear();
        _transferPanelView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TransferItem.PeerId)));
        TransferList.ItemsSource = _transferPanelView;
    }

    private bool TransferMatchesFilter(TransferItem transfer) => _transferFilter switch
    {
        "Active" => transfer.IsActive,
        "Completed" => transfer.IsCompleted,
        "Failed" => transfer.IsFailed,
        _ => true
    };

    private void TransferScope_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        ConfigureTransferView();
    }

    private void TransferFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true, Tag: string value }) _transferFilter = value;
        _transferPanelView?.Refresh();
    }

    private static ChatAttachment TransferAttachment(TransferItem item) => new(
        item.AttachmentId ?? Guid.Empty, item.Id, item.Name, item.MimeType, item.TotalBytes,
        item.LocalPath, item.Status.ToString(), null, item.CompletedBytes, item.BytesPerSecond);

    private void TransferOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem item })
            FileInteractionService.Open(this, TransferAttachment(item));
    }

    private async void RetryTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem item }) await _model.RetryTransferAsync(item);
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
            FileInteractionService.Open(this, TransferAttachment(item));
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

    private void TransferInfoMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TransferItem item }) return;
        var detail = $"文件名：{item.Name}{Environment.NewLine}" +
                     $"方向：{item.Direction}{Environment.NewLine}" +
                     $"状态：{item.StatusText}{Environment.NewLine}" +
                     $"进度：{item.Detail}{Environment.NewLine}" +
                     $"设备：{item.PeerId ?? "未知"}{Environment.NewLine}" +
                     $"本地位置：{item.LocalPath ?? "尚未保存"}";
        BlueLinkDialog.Show(this, "传输详情", detail);
    }

    private async void TransferDeleteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TransferItem item } || !item.CanDelete) return;
        if (!BlueLinkDialog.Confirm(this, "删除本机记录",
                $"只删除“{item.Name}”的本机传输记录？\n已保存文件不会被删除。")) return;
        await _model.DeleteTransferAsync(item);
        _transferPanelView?.Refresh();
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
        if (sender is FrameworkElement { DataContext: TransferItem transfer })
            await _model.CancelTransferAsync(transfer);
    }

    private async void TransferPanelToggle_Click(object sender, RoutedEventArgs e)
    {
        var expanded = FullTransferContent.Visibility != Visibility.Visible;
        ApplyTransferPanel(expanded);
        await _model.SetTransferPanelExpandedAsync(expanded);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow(_model) { Owner = this }.ShowDialog();
    private void AllTransfers_Click(object sender, RoutedEventArgs e) => new AllTransfersWindow(_model) { Owner = this }.Show();

    private async void Conversation_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox { SelectedItem: ConversationSummary item })
        {
            _messagePinnedToBottom = true;
            NewMessagesButton.Visibility = Visibility.Collapsed;
            await _model.SelectConversationAsync(item.PeerId);
            ScrollMessagesToBottom();
        }
    }

    private async void ConversationOpenMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConversationSummary item })
            await _model.SelectConversationAsync(item.PeerId);
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
        BlueLinkDialog.Show(this, "设备信息",
            $"设备：{item.PeerName}{Environment.NewLine}" +
            $"平台：{item.PlatformText}{Environment.NewLine}" +
            $"状态：{item.AvailabilityText}{Environment.NewLine}" +
            $"设备标识：{item.PeerId}{Environment.NewLine}" +
            $"蓝牙地址：{item.TransportAddress}");
    }

    private async void ConversationClearMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ConversationSummary item }) return;
        if (!BlueLinkDialog.Confirm(this, "清空会话记录",
                $"只删除本机中与“{item.PeerName}”的聊天记录？\n已接收文件不会被删除。")) return;
        await _model.ClearConversationAsync(item.PeerId);
    }

    private async void ConversationForgetMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ConversationSummary item }) return;
        if (!BlueLinkDialog.Confirm(this, "移除信任",
                $"移除对“{item.PeerName}”的信任？\n下次连接时需要重新核对安全码。")) return;
        await _model.ForgetPeerAsync(item.PeerId);
    }

    private void MessageCopyMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatItem item }) return;
        var value = item.HasText ? item.Text : string.Join(Environment.NewLine, item.Attachments?.Select(x => x.FileName) ?? []);
        if (!string.IsNullOrWhiteSpace(value)) System.Windows.Clipboard.SetText(value);
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
        if (!BlueLinkDialog.Confirm(this, "删除本机记录",
                "只删除这条消息的本机记录？\n已接收文件不会被删除。")) return;
        await _model.DeleteMessageAsync(item);
    }

    private void AttachmentOpenMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatAttachment attachment }) FileInteractionService.Open(this, attachment);
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

    private void AttachmentInfoMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatAttachment attachment })
            BlueLinkDialog.Show(this, "文件详情", FileInteractionService.Describe(attachment));
    }

    private async void AttachmentDeleteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatAttachment attachment }) return;
        if (!BlueLinkDialog.Confirm(this, "删除本机记录",
                $"只删除“{attachment.FileName}”所在消息的本机记录？\n已保存文件不会被删除。")) return;
        await _model.DeleteAttachmentMessageAsync(attachment);
    }

    private void ApplyTransferPanel(bool expanded)
    {
        TransferPanel.Visibility = Visibility.Visible;
        TransferGapColumn.Width = new GridLength(ResourceDouble(
            expanded ? "TransferPanelExpandedGap" : "TransferPanelCollapsedGap", expanded ? 16 : 8));
        TransferColumn.Width = new GridLength(ResourceDouble(
            expanded ? "TransferPanelExpandedWidth" : "TransferPanelCollapsedWidth", expanded ? 326 : 52));
        FullTransferContent.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        CollapsedTransferRail.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
    }

    private double ResourceDouble(string key, double fallback) => TryFindResource(key) is double value ? value : fallback;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Application.Current is App { ExitRequested: false })
        {
            e.Cancel = true;
            if (_model.Settings.KeepBackgroundSessions) Hide();
            else ((App)Application.Current).RequestExit();
            return;
        }
        base.OnClosing(e);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _model.Messages.CollectionChanged -= Messages_CollectionChanged;
        try
        {
            await _model.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Close();
        }
    }
}
