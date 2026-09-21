using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BlueLink.Domain;
using BlueLink.Files;
using BlueLink.Localization;

namespace BlueLink;

public partial class MainWindow
{
    private readonly HashSet<Guid> _selectedMessageIds = [];
    private MessageSelectionGesture? _messageSelectionGesture;
    private bool _syncingMessageSelection;
    private bool _messageBatchRunning;
    private string? _messageSelectionPeer;
    private Guid? _messageSelectionAnchor;

    private void InitializeMessageBatch()
    {
        _messageSelectionGesture = new MessageSelectionGesture(MessageList, item => (item as ChatItem)?.Id,
            () => _selectedMessageIds, () => _messageSelectionAnchor, ReplaceMessageSelection, ToggleMessageBatch);
        _fileSelectionGesture = new MessageSelectionGesture(TransferList, item => (item as TransferItem)?.Id,
            () => _selectedFileIds, () => _fileSelectionAnchor, _ => { }, ToggleFileBatch, rangeOnly: true);
        _model.Messages.CollectionChanged += (_, _) =>
        {
            if (_model.MessageSelectionMode) Dispatcher.BeginInvoke(new Action(SyncMessageSelection));
        };
        _model.PropertyChanged += (_, args) =>
        {
            if (_model.FileSelectionMode)
            {
                if (!_fileBatchRunning && (_fileSelectionPeer != _model.ActivePeerId || !_model.ShowFiles || _model.IsSettingsOpen)) SetFileSelectionMode(false);
                else if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName is nameof(MainViewModel.IsConnected) or nameof(MainViewModel.ActiveSessionCount)) UpdateFileSelection();
            }
            if (_model.MessageSelectionMode && (_messageSelectionPeer != _model.ActivePeerId || _model.ShowFiles || _model.IsSettingsOpen))
                SetMessageSelectionMode(false);
        };
    }

    internal void SetMessageSelectionMode(bool enabled, Guid? initial = null)
    {
        if (_messageBatchRunning) return;
        _messageSelectionAnchor = initial;
        _syncingMessageSelection = true;
        try
        {
            _messageSelectionPeer = enabled ? _model.ActivePeerId : null;
            _model.MessageSelectionMode = enabled;
            _selectedMessageIds.Clear(); MessageList.UnselectAll();
            MessageList.SelectionMode = enabled ? SelectionMode.Multiple : SelectionMode.Single;
            if (enabled && initial is { } id) _selectedMessageIds.Add(id);
            if (enabled) { ClearMessageSelectionExcept(null); MessageList.Focus(); }
        }
        finally { _syncingMessageSelection = false; }
        SyncMessageSelection();
    }

    private void MessageMultiSelectMenu_Click(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.DataContext;
        var id = item is ChatItem message ? message.Id : item is ChatAttachment file
            ? _model.Messages.FirstOrDefault(value => value.Attachments?.Any(attachment => attachment.AttachmentId == file.AttachmentId) == true)?.Id : null;
        if (id is { } initial) SetMessageSelectionMode(true, initial);
    }

    private void MessageBatchSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingMessageSelection || !_model.MessageSelectionMode) return;
        // Replacement of a status snapshot keeps its stable ID selected until reconciliation.
        foreach (ChatItem item in e.RemovedItems)
            if (MessageList.Items.Contains(item)) _selectedMessageIds.Remove(item.Id);
        foreach (ChatItem item in e.AddedItems) _selectedMessageIds.Add(item.Id);
        UpdateMessageBatch();
    }

    private void ReplaceMessageSelection(IReadOnlySet<Guid> ids)
    {
        if (_messageBatchRunning || !_model.MessageSelectionMode) return;
        _selectedMessageIds.Clear(); _selectedMessageIds.UnionWith(ids);
        if (_messageSelectionAnchor is null && ids.Count > 0)
            _messageSelectionAnchor = MessageList.Items.OfType<ChatItem>().First(item => ids.Contains(item.Id)).Id;
        SyncMessageSelection();
    }

    internal void ToggleMessageBatch(Guid id, bool extend)
    {
        if (_messageBatchRunning || !_model.MessageSelectionMode) return;
        BatchSelection.Toggle(_selectedMessageIds, MessageList.Items.OfType<ChatItem>().Select(item => item.Id).ToArray(),
            _messageSelectionAnchor, id, extend);
        if (_selectedMessageIds.Contains(id) && (!extend || _messageSelectionAnchor is null)) _messageSelectionAnchor = id;
        SyncMessageSelection();
    }

    private void SyncMessageSelection()
    {
        if (MessageBatchPanel is null) return;
        _selectedMessageIds.IntersectWith(_model.Messages.Select(item => item.Id));
        _syncingMessageSelection = true;
        try
        {
            foreach (var item in MessageList.Items.OfType<ChatItem>())
                if (_selectedMessageIds.Contains(item.Id)) { if (!MessageList.SelectedItems.Contains(item)) MessageList.SelectedItems.Add(item); }
                else if (MessageList.SelectedItems.Contains(item)) MessageList.SelectedItems.Remove(item);
        }
        finally { _syncingMessageSelection = false; }
        UpdateMessageBatch();
    }

    internal IReadOnlyList<ChatItem> SelectedBatchMessages => MessageBatch.Ordered(_model.Messages, _selectedMessageIds);

    private void UpdateMessageBatch()
    {
        _messageSelectionAnchor = BatchSelection.ResolveAnchor(_selectedMessageIds, MessageList.Items.OfType<ChatItem>().Select(item => item.Id).ToArray(), _messageSelectionAnchor);
        MessageBatchPanel.Visibility = _model.MessageSelectionMode ? Visibility.Visible : Visibility.Collapsed;
        MessageComposer.Visibility = _model.MessageSelectionMode ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceHeaderContent.Visibility = _model.MessageSelectionMode ? Visibility.Collapsed : Visibility.Visible;
        MessageSelectionHeader.Visibility = _model.MessageSelectionMode ? Visibility.Visible : Visibility.Collapsed;
        MessageSelectionHeader.IsEnabled = !_messageBatchRunning;
        MessageList.Margin = _model.MessageSelectionMode ? new Thickness(-14, 0, -14, 0) : new Thickness(0);
        MessageBatchCount.Text = Strings.Format($"已选择 {_selectedMessageIds.Count} 项");
        MessageBatchControls.IsEnabled = MessageList.IsEnabled = !_messageBatchRunning;
        MessageBatchCopy.IsEnabled = MessageBatchDelete.IsEnabled = !_messageBatchRunning && _selectedMessageIds.Count > 0;
        _messageSelectionGesture?.SetEnabled(_model.MessageSelectionMode && !_messageBatchRunning);
    }

    private void MessageBatchDone_Click(object sender, RoutedEventArgs e) => SetMessageSelectionMode(false);

    private void MessageBatchCopy_Click(object sender, RoutedEventArgs e) =>
        CopyMessageSelection(data => Clipboard.SetDataObject(data, true));

    internal void CopyMessageSelection(Action<System.Windows.DataObject> publish)
    {
        if (_messageBatchRunning || _selectedMessageIds.Count == 0) return;
        try
        {
            if (!MessageClipboard.TryCreateDataObject(SelectedBatchMessages, _model.AllTransfers, out var data))
            { ShowToast("所选附件尚未完成或不可读取", ToastLevel.Warning); return; }
            publish(data!);
            SetMessageSelectionMode(false);
            ShowToast("已复制到剪贴板", ToastLevel.Success);
        }
        catch { ShowToast("复制失败，请稍后重试", ToastLevel.Error); }
    }


    internal static ConfirmationDocument MessageBatchDeleteDocument(int count) => ConfirmationDocument.DeleteRecord(
        Strings.Format($"确定删除所选的 {count} 条本机消息记录吗？") + Environment.NewLine + Strings.Get("正在发送或待恢复的消息会跳过。"));

    internal async Task<IReadOnlyList<MessageBatchResult>> DeleteMessageSelectionAsync()
    {
        if (_messageBatchRunning || _messageSelectionPeer is not { } peer || _selectedMessageIds.Count == 0) return [];
        var ids = SelectedBatchMessages.Select(item => item.Id).ToArray();
        var completed = false;
        _messageBatchRunning = true; UpdateMessageBatch();
        try
        {
            var results = await _model.DeleteSelectedMessagesAsync(peer, ids);
            if (_messageSelectionPeer == peer) _selectedMessageIds.ExceptWith(results.Where(item => item.Deleted).Select(item => item.Id));
            completed = results.Any(item => item.Deleted);
            return results;
        }
        finally
        {
            _messageBatchRunning = false;
            if (completed || _messageSelectionPeer != _model.ActivePeerId || _model.ShowFiles || _model.IsSettingsOpen) SetMessageSelectionMode(false);
            else SyncMessageSelection();
        }
    }

    private async void MessageBatchDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_messageBatchRunning || _messageSelectionPeer is null || _selectedMessageIds.Count == 0) return;
        if (!ConfirmationWindow.Show(this, MessageBatchDeleteDocument(_selectedMessageIds.Count))) return;
        try
        {
            var results = await DeleteMessageSelectionAsync();
            var summary = Strings.Format($"已删除 {results.Count(item => item.Deleted)} 条，跳过或失败 {results.Count(item => !item.Deleted)} 条");
            if (results.Any(item => !item.Deleted))
                BlueLinkDialog.Show(this, "批量操作结果", summary + Environment.NewLine + string.Join(Environment.NewLine,
                    results.Where(item => !item.Deleted).Select(item => Strings.Get(item.Reason ?? "批量操作失败，请重试")).Distinct()));
            else ShowToast(summary, ToastLevel.Success);
        }
        catch { ShowToast("批量操作失败，请重试", ToastLevel.Error); }
    }
}
