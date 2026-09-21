using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BlueLink.Domain;
using BlueLink.Files;
using BlueLink.Localization;

namespace BlueLink;

public partial class MessageSearchWindow
{
    private ContextMenu? _resultMenu;

    private void Result_Click(object sender, MouseButtonEventArgs e)
    {
        if (IsBatchSelecting) { e.Handled = true; return; }
        if (e.ChangedButton == MouseButton.Left && sender is FrameworkElement target)
            e.Handled = ActivateResult(target, (attachment, gallery) => FileInteractionService.Open(this, attachment, gallery));
    }

    internal bool ActivateResult(FrameworkElement target, Action<ChatAttachment, IEnumerable<ChatAttachment>> open)
    {
        if (IsBatchSelecting || target.DataContext is not Result result || result.Attachment is null) return false;
        var message = _messages.FirstOrDefault(item => item.Id == result.Message.Id) ?? _searchSource.FirstOrDefault(item => item.Id == result.Message.Id);
        var attachment = message?.Attachments?.FirstOrDefault(item => item.AttachmentId == result.Attachment.AttachmentId);
        if (attachment is null) return false;
        attachment = SearchResultActions.Current(attachment, _model?.AllTransfers ?? []);
        open(attachment, _queryResults.SelectMany(item => item.Attachments ?? []));
        return true;
    }

    private void Result_RightDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void Result_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!IsBatchSelecting && sender is FrameworkElement { DataContext: Result result } target)
            OpenResultMenu(target, result.Message, e.GetPosition(target));
    }

    private void ResultMore_Click(object sender, RoutedEventArgs e)
    {
        if (IsBatchSelecting) { e.Handled = true; return; }
        if (sender is FrameworkElement { DataContext: Result result } target) OpenResultMenu(target, result.Message, target.IsMouseOver ? Mouse.GetPosition(target) : null);
        e.Handled = true;
    }

    private void Results_KeyDown(object sender, KeyEventArgs e)
    {
        if (IsBatchSelecting) return;
        var source = SearchTextAt(e.OriginalSource as DependencyObject);
        var result = source?.DataContext as Result ?? Results.SelectedItem as Result;
        if (result is null) return;
        if (source is not null && e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) return;
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopyResultText(result.Message); e.Handled = true;
        }
        else if (e.Key == Key.Enter && source is null)
        {
            if (Results.ItemContainerGenerator.ContainerFromItem(result) is FrameworkElement target)
                e.Handled = ActivateResult(target, (attachment, gallery) => FileInteractionService.Open(this, attachment, gallery));
        }
        else if (e.Key == Key.Apps || e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            if (Results.ItemContainerGenerator.ContainerFromItem(result) is FrameworkElement target)
                OpenResultMenu(target, result.Message);
            e.Handled = true;
        }
    }

    private void OpenResultMenu(FrameworkElement target, ChatItem message, Point? pointer = null)
    {
        if (_resultMenu is not null) _resultMenu.IsOpen = false;
        _resultMenu = BuildResultMenu(message);
        ConfigureResultMenuPlacement(_resultMenu, target, pointer);
        _resultMenu.IsOpen = true;
    }

    internal static void ConfigureResultMenuPlacement(ContextMenu menu, FrameworkElement target, Point? pointer)
    {
        menu.PlacementTarget = target;
        menu.Placement = pointer.HasValue ? PlacementMode.RelativePoint : PlacementMode.Bottom;
        menu.HorizontalOffset = pointer?.X ?? 0;
        menu.VerticalOffset = pointer?.Y ?? 0;
    }

    internal ContextMenu BuildResultMenu(ChatItem original)
    {
        var message = _messages.FirstOrDefault(m => m.Id == original.Id) ?? _searchSource.FirstOrDefault(m => m.Id == original.Id) ?? original;
        var attachment = SearchResultActions.Attachment(message, QueryInput.Text, _kind);
        if (attachment is not null) attachment = SearchResultActions.Current(attachment, _model?.AllTransfers ?? []);
        var menu = new ContextMenu { Style = (Style)FindResource("BlueLinkContextMenuStyle") };
        MenuItem Item(string tag, string label, string iconName, bool danger = false)
        {
            var icon = new Image { Width = 18, Height = 18 };
            icon.SetResourceReference(Image.SourceProperty, "FigmaIcon-" + iconName);
            var item = new MenuItem { Tag = tag, Header = Strings.Get(label), Icon = icon,
                Style = (Style)FindResource("BlueLinkMenuItemStyle"), CommandParameter = danger ? "danger" : null };
            if (tag == "select") item.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.TaskListLtr24, FontSize = 18 };
            item.Click += async (_, _) => await ExecuteResultActionAsync(message.Id, attachment?.AttachmentId, tag);
            return item;
        }
        if (attachment is not null)
        {
            foreach (var (tag, icon) in new[] { ("copy-name", "menu-copy"), ("open", "menu-open"),
                ("copy", "menu-copy"), ("locate", "menu-folder"), ("save", "menu-download"),
                ("info", "menu-details"), ("pause", "menu-pause"), ("resume", "menu-play"),
                ("retry", "menu-play"), ("reselect", "menu-folder"), ("bluetooth", "menu-play"), ("failure", "menu-alert"), ("cancel", "menu-cancel"), ("delete", "menu-trash") })
                menu.Items.Add(Item(tag, "", icon, tag is "cancel" or "delete"));
            MainWindow.ConfigureFileContextMenu(menu, attachment, message.Outgoing);
            foreach (var item in menu.Items.OfType<MenuItem>())
            {
                if (item.Tag as string is "pause" or "resume" or "retry" or "reselect" or "bluetooth" or "cancel")
                    item.IsEnabled = _model?.IsConnected == true && _model.AllTransfers.Any(t => t.Id == attachment.TransferId);
                if (item.Tag as string == "delete" && _model is null) item.Visibility = Visibility.Collapsed;
            }
        }
        if (message.HasText) menu.Items.Insert(0, Item("copy-text", "复制文本", "menu-copy"));
        // Keep explicit navigation separate from copy/open; opening a menu never closes search.
        var tail = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Tag as string is "cancel" or "delete");
        var index = tail is null ? menu.Items.Count : menu.Items.IndexOf(tail);
        menu.Items.Insert(index++, Item("message", "定位原消息", "menu-message"));
        if (attachment is null && _model is not null) menu.Items.Add(Item("delete", "删除本机消息", "menu-trash", true));
        if (_model is not null) menu.Items.Add(Item("select", "多选", "menu-multiselect"));
        var ordering = new[] { "message", "open", "copy", "copy-name", "copy-text", "select", "locate", "save", "info",
            "pause", "resume", "retry", "reselect", "bluetooth", "failure", "cancel", "delete" };
        var ordered = menu.Items.OfType<MenuItem>().OrderBy(item => Array.IndexOf(ordering, item.Tag as string)).ToArray();
        menu.Items.Clear(); foreach (var item in ordered) menu.Items.Add(item);
        var destructive = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Visibility == Visibility.Visible && i.Tag as string is "cancel" or "delete");
        if (destructive is not null) menu.Items.Insert(menu.Items.IndexOf(destructive),
            new Separator { Style = (Style)FindResource("BlueLinkMenuSeparatorStyle") });
        return menu;
    }

    private void CopyResultText(ChatItem message)
    {
        if (!message.HasText) return;
        try { Clipboard.SetText(ResultTextToCopy(message)); SearchToasts.Show("已复制到剪贴板", ToastLevel.Success); }
        catch (Exception) { SearchToasts.Show("复制失败，请稍后重试", ToastLevel.Error); }
    }

    private async Task ExecuteResultActionAsync(Guid messageId, Guid? attachmentId, string action)
    {
        if ((_messages.FirstOrDefault(m => m.Id == messageId) ?? _searchSource.FirstOrDefault(m => m.Id == messageId)) is not { } message) return;
        var attachment = message.Attachments?.FirstOrDefault(a => a.AttachmentId == attachmentId);
        var transfer = attachment is null ? null : _model?.AllTransfers.FirstOrDefault(t => t.Id == attachment.TransferId);
        if (attachment is not null) attachment = SearchResultActions.Current(attachment, _model?.AllTransfers ?? []);
        try
        {
            switch (action)
            {
                case "copy-text": CopyResultText(message); break;
                case "copy-name" when attachment is not null: FileInteractionService.CopyFileName(attachment.FileName, SearchToasts); break;
                case "select": SetSearchSelectionMode(true, message.Id); break;
                case "message": SelectedMessage = message; DialogResult = true; break;
                case "open" when attachment is not null: FileInteractionService.Open(this, attachment, _queryResults.SelectMany(item => item.Attachments ?? [])); break;
                case "copy" when attachment is not null: FileInteractionService.CopyToClipboard(this, attachment); break;
                case "locate" when attachment is not null: FileInteractionService.Locate(this, attachment); break;
                case "save" when attachment is not null: FileInteractionService.SaveCopy(this, attachment); break;
                case "info" or "failure":
                    if (attachment is not null && _model is not null)
                    {
                        var document = await _model.DescribeAttachmentAsync(attachment, transfer, action == "failure");
                        if (IsVisible) InformationWindow.Show(this, document);
                    }
                    else BlueLinkDialog.Show(this, attachment is null ? "消息详情" : "文件详情",
                        attachment is null ? message.Text : FileInteractionService.Describe(attachment));
                    break;
                case "delete" when _model is not null && attachment?.IsTransferActive != true:
                    if (ConfirmationWindow.Show(this, ConfirmationDocument.DeleteRecord(Strings.Get("确定删除这条本机消息记录吗？"))))
                    {
                        await _model.DeleteMessageAsync(message);
                        _searchSourceLoaded = false; QueueResults();
                        SearchToasts.Show("已删除本机记录", ToastLevel.Success);
                    }
                    break;
                case "pause" when _model?.IsConnected == true && transfer is { CanPause: true }:
                    await _model.PauseTransferAsync(transfer); break;
                case "resume" when _model?.IsConnected == true && transfer is { CanResume: true }:
                    await _model.ResumeTransferAsync(transfer); break;
                case "retry" when _model?.IsConnected == true && transfer is { CanRetry: true }:
                    await _model.RetryTransferAsync(transfer); break;
                case "bluetooth" when _model is not null && transfer is { CanSwitchToBluetooth: true }:
                    await _model.SwitchQueuedToBluetoothAsync(transfer); break;
                case "reselect" when _model is not null && transfer is { CanReselectSource: true }:
                    var picker = new Microsoft.Win32.OpenFileDialog { Title = Strings.Get("重新选择原文件"), CheckFileExists = true };
                    if (picker.ShowDialog(this) == true) await _model.RetryTransferFromAsync(transfer, picker.FileName);
                    break;
                case "cancel" when _model?.IsConnected == true && transfer is { CanCancel: true }:
                    if (ConfirmationWindow.Show(this, ConfirmationDocument.CancelTransfer(transfer.Name, transfer.Outgoing)))
                        await _model.CancelTransferAsync(transfer);
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Session.SessionLog.Write("Search", "Search result action failed", error);
            if (IsVisible) SearchToasts.Show("操作失败，请稍后重试", ToastLevel.Error);
        }
    }
}
