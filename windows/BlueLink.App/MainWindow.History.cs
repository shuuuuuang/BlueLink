using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using BlueLink.Domain;
using BlueLink.Files;

namespace BlueLink;

public partial class MainWindow
{
    private string _fileQuery = "";
    private string _fileDirection = "All";
    private string _fileDevice = "@current";
    private bool _updatingFileDevices;
    private bool _updatingFileStatus;

    private void FileStatus_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedValue: string value }) SetTransferFilter(value);
    }

    private void SetTransferFilter(string value)
    {
        if (_updatingFileStatus) return;
        _updatingFileStatus = true;
        try
        {
            _transferFilter = value;
            if (FileStatusFilters is not null)
                foreach (RadioButton button in FileStatusFilters.Children) button.IsChecked = Equals(button.Tag, value);
            if (FileStatusFilter is not null) FileStatusFilter.SelectedValue = value;
        }
        finally { _updatingFileStatus = false; }
        RefreshFileResults();
    }

    private void SearchMessages_Click(object sender, RoutedEventArgs e) => ShowMessageSearch();
    internal void ShowMessageSearch(string initialQuery = "")
    {
        if (!_model.HasActiveConversation) return;
        var search = new MessageSearchWindow(_model.ActivePeerTitle, _model.Messages, _model.Settings.ShowImageThumbnails) { Owner = this };
        if (initialQuery.Length > 0) search.ApplyFilter(initialQuery, HistoryKind.All);
        using var overlay = BlueLinkDialog.DimOwner(this, "#3D0F172A", 52);
        if (search.ShowDialog() != true || search.SelectedMessage is not { } message) return;
        _model.ShowFiles = false;
        MessagesViewButton.IsChecked = true;
        _messagePinnedToBottom = false;
        Dispatcher.BeginInvoke(() => { MessageList.SelectedItem = message; MessageList.ScrollIntoView(message); MessageList.Focus(); });
    }

    private void FileQuery_Changed(object sender, TextChangedEventArgs e)
    {
        _fileQuery = FileSearchInput?.Text ?? "";
        _model.FileSearchQuery = _fileQuery;
        RefreshFileResults();
    }
    private void FileDirection_Changed(object sender, SelectionChangedEventArgs e)
    {
        _fileDirection = (FileDirectionFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "All";
        RefreshFileResults();
    }
    private void FileDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingFileDevices || FileDeviceFilter.SelectedItem is not FileDeviceChoice choice) return;
        _fileDevice = choice.Id;
        ConfigureTransferView();
    }
    private void FileDevices_Opened(object? sender, EventArgs e) => RefreshFileDeviceChoices();
    private void RefreshFileDeviceChoices()
    {
        _updatingFileDevices = true;
        var choices = new List<FileDeviceChoice>();
        if (_model.HasActiveConversation) choices.Add(new("@current", Localization.Strings.Get("当前设备")));
        choices.Add(new("", Localization.Strings.Get("全部设备")));
        choices.AddRange(_model.Conversations.Select(peer => new FileDeviceChoice(peer.PeerId, peer.PeerName)));
        if (!choices.Any(choice => choice.Id == _fileDevice)) _fileDevice = "";
        FileDeviceFilter.ItemsSource = choices;
        FileDeviceFilter.SelectedItem = choices.First(choice => choice.Id == _fileDevice);
        _updatingFileDevices = false;
    }
    private void RefreshFileResults()
    {
        _transferPanelView?.Refresh();
        UpdateFileResultCount();
    }
    private void UpdateFileResultCount()
    {
        if (FileEmptyText is null || _transferPanelView is null) return;
        var count = _transferPanelView.Cast<TransferItem>().Count();
        var searching = !string.IsNullOrWhiteSpace(_fileQuery);
        var filtering = _transferFilter != "All" || _fileDirection != "All" || _fileDevice is not ("" or "@current");
        FileEmptyText.Text = searching ? Localization.Strings.Format($"未找到包含“{_fileQuery.Trim()}”的文件记录")
            : Localization.Strings.Get(filtering ? "没有符合当前筛选条件的文件" : "暂无文件记录");
        FileEmptyDetail.Text = Localization.Strings.Get(searching ? "请尝试更换关键词，或调整状态、设备及方向筛选"
            : filtering ? "调整设备、方向或状态筛选条件后重试。" : "通过消息发送或接收文件后，记录会显示在这里。");
        FileEmptyText.FontSize = searching ? 17 : 24;
        FileEmptyText.FontWeight = searching ? FontWeights.Normal : filtering ? FontWeights.Medium : FontWeights.Bold;
        FileEmptyDetail.FontSize = searching ? 13 : 16;
        FileEmptyIconBackground.Width = FileEmptyIconBackground.Height = searching ? 60 : 72;
        FileEmptyIconBackground.SetResourceReference(Border.BackgroundProperty, searching ? "SearchEmptyBackgroundBrush" : "SoftBlueBrush");
        FileEmptyIcon.Width = FileEmptyIcon.Height = searching ? 38 : 32;
        FileEmptyIcon.SetResourceReference(Image.SourceProperty, searching ? "FigmaIcon-file-search-empty"
            : filtering ? "FigmaIcon-file-filter-empty" : "FigmaIcon-file-empty");
        FileEmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FileCountText.Text = Localization.Strings.Format($"{count} 个文件");
    }
    private void TransferGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle) return;
        DependencyObject? parent = toggle;
        while (parent is not null && parent is not GroupItem) parent = VisualTreeHelper.GetParent(parent);
        if (parent is GroupItem group && FindVisualChild<ItemsPresenter>(group) is { } presenter)
            presenter.Visibility = toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TransferList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer scroll || FileColumnsHeader is null) return;
        // Match the row viewport while the header background continues across the entire table.
        FileColumnsHeader.Margin = new Thickness(12, 0, 12 + scroll.Padding.Right, 0);
    }

    private void TransferRow_Loaded(object sender, RoutedEventArgs e)
    {
        // The official item template has a fixed one-DIP inset, independent of BorderThickness.
        // Keep the template and selection behavior; the row draws its own separator.
        if (sender is ListBoxItem item && item.Template.FindName("Border", item) is Border border)
            border.BorderThickness = new Thickness(0);
    }

    private void TransferGroup_Loaded(object sender, RoutedEventArgs e)
    {
        // WPF's grouped presenter indents child rows; a table shares the header's full width.
        if (sender is GroupItem group && FindVisualChild<ItemsPresenter>(group) is { } presenter)
            presenter.Margin = new Thickness(0);
    }
    private TransferItem? AttachmentTransfer(object sender) => sender is FrameworkElement { DataContext: ChatAttachment attachment }
        ? _model.AllTransfers.FirstOrDefault(item => item.Id == attachment.TransferId) : null;
    private async void AttachmentPause_Click(object sender, RoutedEventArgs e)
    {
        if (AttachmentTransfer(sender) is { CanPause: true } transfer) await _model.PauseTransferAsync(transfer);
    }
    private async void AttachmentResume_Click(object sender, RoutedEventArgs e)
    {
        if (AttachmentTransfer(sender) is { CanResume: true } transfer) await _model.ResumeTransferAsync(transfer);
    }
    private async void AttachmentCancel_Click(object sender, RoutedEventArgs e)
    {
        if (AttachmentTransfer(sender) is not { CanCancel: true } transfer) return;
        if (ConfirmationWindow.Show(this, ConfirmationDocument.CancelTransfer(transfer.Name, transfer.Outgoing)))
            await _model.CancelTransferAsync(transfer);
    }
    private async void RetryTransfer_Click(object sender, RoutedEventArgs e)
    {
        var transfer = (sender as FrameworkElement)?.DataContext as TransferItem ?? AttachmentTransfer(sender);
        if (transfer is null) return;
        try { await _model.RetryTransferAsync(transfer); }
        catch (OperationCanceledException) { /* The transfer row already reflects cancellation or disconnection. */ }
        catch (Exception error)
        {
            Session.SessionLog.Write("Transfer", "重试传输失败", error);
            ShowToast(Localization.Strings.Get("请连接设备并确认原文件仍可读取，然后重试。"), ToastLevel.Error);
        }
    }
    private async void TransferFailure_Click(object sender, RoutedEventArgs e)
    {
        var transfer = (sender as FrameworkElement)?.DataContext as TransferItem ?? AttachmentTransfer(sender);
        if (transfer is not null) await ShowAttachmentInformationAsync(TransferAttachment(transfer), transfer, failure: true);
        else if (sender is FrameworkElement { DataContext: ChatAttachment attachment })
            await ShowAttachmentInformationAsync(attachment, failure: true);
    }
    private void TransferCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem item }) FileInteractionService.CopyToClipboard(this, TransferAttachment(item));
    }
    private sealed record FileDeviceChoice(string Id, string Name) { public override string ToString() => Name; }
}
