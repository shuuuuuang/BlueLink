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
    private string _fileScope = "@current";
    private bool _fileQuerySubscribed;
    private readonly HashSet<TransferItem> _trackedFileItems = [];
    private CancellationTokenSource? _fileCancellation;
    private int _fileRevision;
    private int _fileLimit = 100;
    private TransferItem[] _fileResults = [];
    private FileKind _fileKind;
    private HistorySort _fileSort;
    private bool _fileDescending = true;
    private HistoryDateRange _fileDates;

    private readonly Dictionary<string,HashSet<string>> _fileColumnSelections = new() {
        ["Kind"] = [], ["Status"] = [], ["Route"] = [], ["Date"] = [] };
    private Popup? _columnFilterPopup;
    private FileQueryOptions FileOptions
    {
        get
        {
            var peers = _fileColumnSelections["Route"].Where(key => key.StartsWith("peer:")).Select(key => key[5..]).ToArray();
            return new(_fileQuery, "All", _fileDirection,
                peers.Length > 0 ? null : _fileDevice == "@current" ? _model.ActivePeerId : _fileDevice,
                _fileKind, _fileDates, _fileSort, _fileDescending, Localization.Strings.Language,
                _fileColumnSelections["Status"].ToArray(),
                peers.Contains("@all") ? null : peers.Select(peer => peer == "@current" ? _model.ActivePeerId ?? "@missing" : peer).ToArray());
        }
    }

    private void TrackFileItems()
    {
        var current = _model.AllTransfers.ToHashSet();
        foreach(var old in _trackedFileItems.Where(item => !current.Contains(item)).ToArray())
        { old.PropertyChanged -= FileItemChanged; _trackedFileItems.Remove(old); }
        foreach(var item in current) if(_trackedFileItems.Add(item)) item.PropertyChanged += FileItemChanged;
    }
    private void FileSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if(e.Action == NotifyCollectionChangedAction.Reset) { TrackFileItems(); _selectedFileIds.IntersectWith(_model.AllTransfers.Select(item => item.Id)); }
        else
        {
            if(e.OldItems is not null) foreach(TransferItem item in e.OldItems)
                if(_trackedFileItems.Remove(item)) {
                    item.PropertyChanged -= FileItemChanged;
                    if (e.NewItems?.Cast<TransferItem>().Any(replacement => replacement.Id == item.Id) != true) _selectedFileIds.Remove(item.Id);
                }
            if(e.NewItems is not null) foreach(TransferItem item in e.NewItems)
                if(_trackedFileItems.Add(item)) item.PropertyChanged += FileItemChanged;
        }
        RefreshFileResults(); UpdateFileSelection();
    }
    private void FileItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if(e.PropertyName == nameof(TransferItem.Status)) RefreshFileResults();
        if (_model.FileSelectionMode && e.PropertyName is nameof(TransferItem.Status) or nameof(TransferItem.LocalPath)) UpdateFileSelection();
    }

    private void FileColumnFilter_Click(object? sender, EventArgs e)
    {
        if(sender is not FileTableHeader header || header.FilterColumn.Length == 0) return;
        if(_columnFilterPopup is { IsOpen: true }) _columnFilterPopup.IsOpen = false;
        var editor = CreateFileColumnEditor(header.FilterColumn);
        editor.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
        var popup = new Popup { Child = editor, PlacementTarget = header.FilterButton, Placement = PlacementMode.Bottom,
            AllowsTransparency = true, StaysOpen = false, VerticalOffset = 4, HorizontalOffset = Math.Min(0,header.FilterButton.ActualWidth-editor.DesiredSize.Width), PopupAnimation = PopupAnimation.Fade };
        editor.ManageCalendarPopups(popup);
        _columnFilterPopup = popup;
        editor.Confirmed += (_,_) => { popup.IsOpen = false; header.FilterButton.Focus(); };
        editor.ResetRequested += (_,_) => { popup.IsOpen = false; header.FilterButton.Focus(); };
        editor.DismissRequested += (_,_) => { popup.IsOpen = false; header.FilterButton.Focus(); };
        popup.Opened += (_,_) => { header.SetFilterOpen(true); editor.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First)); };
        popup.Closed += (_,_) => { header.SetFilterOpen(false); popup.Child = null; if(ReferenceEquals(_columnFilterPopup,popup)) _columnFilterPopup = null; };
        popup.IsOpen = true;
    }
    internal FileFilterEditor CreateFileColumnEditor(string column)
    {
        var editor = new FileFilterEditor(FileColumnChoices(column), _fileColumnSelections[column],
            column == "Date" ? _fileDates : default, column == "Date",
            Localization.Strings.Get(column switch { "Kind" => "文件类型", "Status" => "状态", "Route" => "来源 → 目标", _ => "日期范围" }),
            column == "Date" ? HistorySearch.Files(_model.AllTransfers.Select(FileQueryRecord.Capture), FileOptions with { Dates = default })
                .Select(item => item.Time.LocalDateTime.Date).ToHashSet() : null);
        editor.Confirmed += (_,_) => ApplyFileColumn(column,editor.Selected,editor.Dates);
        editor.ResetRequested += (_,_) => ApplyFileColumn(column,[],default);
        return editor;
    }
    private IEnumerable<FileFilterChoice> FileColumnChoices(string column)
    {
        string T(string key) => Localization.Strings.Get(key);
        if(column == "Kind") return new[] { new FileFilterChoice("Images",T("图片")),new FileFilterChoice("Files",T("文件")) };
        if(column == "Status") return new[] { ("Active","进行中"),("Completed","已完成"),("Incomplete","未完成"),("Failed","失败"),("Rejected","未接收"),("Canceled","已取消") }
            .Select(item => new FileFilterChoice(item.Item1,T(item.Item2)));
        if(column != "Route") return [];
        var result = new List<FileFilterChoice>();
        if(_model.HasActiveConversation) result.Add(new("peer:@current",T("当前设备"),T("设备")));
        result.Add(new("peer:@all",T("全部设备"),T("设备")));
        result.AddRange(_model.Conversations.Select(peer => new FileFilterChoice("peer:"+peer.PeerId,peer.PeerName,T("设备"))));
        // Preserve selected unavailable identities rather than silently broadening a filter.
        result.AddRange(_fileColumnSelections["Route"].Where(key => key.StartsWith("peer:") && result.All(item => item.Key != key))
            .Select(key => new FileFilterChoice(key,T("未知设备"),T("设备"))));
        result.Add(new("direction:Outgoing",T("发送"),T("方向"))); result.Add(new("direction:Incoming",T("接收"),T("方向")));
        return result;
    }
    private void ApplyFileColumn(string column,IEnumerable<string> selected,HistoryDateRange dates)
    {
        _fileColumnSelections[column] = selected.ToHashSet();
        if(column == "Date") _fileDates = dates;
        if(column == "Kind") _fileKind = _fileColumnSelections[column].Count == 1 ? Enum.Parse<FileKind>(_fileColumnSelections[column].Single()) : FileKind.All;
        if(column == "Route")
        {
            var directions = _fileColumnSelections[column].Where(key => key.StartsWith("direction:")).ToArray();
            _fileDirection = directions.Length == 1 ? directions[0][10..] : "All";
            if(!_fileColumnSelections[column].Any(key => key.StartsWith("peer:"))) _fileDevice = _fileScope;
        }
        ConfigureTransferView();
    }
    private void FileHeaderSort_Click(object? sender, EventArgs e)
    {
        if(sender is not FileTableHeader header || !Enum.TryParse<HistorySort>(header.SortKey,out var sort)) return;
        _fileDescending = _fileSort == sort ? !_fileDescending : sort != HistorySort.Name;
        _fileSort = sort;
        RefreshFileResults();
    }
    private void UpdateFileSortHeaders() => UpdateFileColumnFilters();
    private void FileClear_Click(object sender, RoutedEventArgs e)
    {
        foreach(var values in _fileColumnSelections.Values) values.Clear();
        _fileKind = FileKind.All; _fileDates = default;
        _fileDirection = "All"; _fileDevice = _fileScope;
        ConfigureTransferView();
    }
    private void FileMore_Click(object sender, RoutedEventArgs e) { _fileLimit += 100; RenderFileResults(); }

    private void SearchMessages_Click(object sender, RoutedEventArgs e) => ShowMessageSearch();
    internal async void ShowMessageSearch(string initialQuery = "")
    {
        if (!_model.HasActiveConversation) return;
        var peerId = _model.ActivePeerId!;
        var search = new MessageSearchWindow(_model.ActivePeerTitle, _model.Messages, _model.Settings.ShowImageThumbnails, _model) { Owner = this };
        if (initialQuery.Length > 0) search.ApplyFilter(initialQuery, HistoryKind.All);
        using var overlay = BlueLinkDialog.DimOwner(this, "#3D0F172A", 52);
        if (search.ShowDialog() != true || search.SelectedMessage is not { } message) return;
        if (!await _model.LoadMessageContextAsync(peerId, message.Id)) return;
        var target = _model.Messages.FirstOrDefault(item => item.Id == message.Id);
        if (target is null) return;
        _model.ShowFiles = false;
        MessagesViewButton.IsChecked = true;
        _messagePinnedToBottom = false;
        _ = Dispatcher.BeginInvoke(() => { MessageList.SelectedItem = target; MessageList.ScrollIntoView(target); MessageList.Focus(); });
    }

    private void FileQuery_Changed(object sender, TextChangedEventArgs e)
    {
        _fileQuery = FileSearchInput?.Text ?? "";
        _model.FileSearchQuery = _fileQuery;
        RefreshFileResults();
    }
    private void RefreshFileResults()
    {
        if (TransferList is null || _disposed) return;
        _fileCancellation?.Cancel(); _fileCancellation?.Dispose();
        var cancellation = new CancellationTokenSource(); _fileCancellation = cancellation;
        var token = cancellation.Token; var revision = ++_fileRevision; var options = FileOptions;
        _fileLimit = 100;
        if (!IsLoaded)
        {
            _fileResults = HistorySearch.Files(_model.AllTransfers.Select(FileQueryRecord.Capture), options).Select(item => item.Item).ToArray();
            RenderFileResults(); return;
        }
        _ = QueryFilesAsync(options, revision, token);
    }
    private async Task QueryFilesAsync(FileQueryOptions options, int revision, CancellationToken token)
    {
        try
        {
            FileCountText.Text = Localization.Strings.Get("正在查询…");
            // Retain the current rows until the replacement query is ready.
            FileEmptyState.Visibility = Visibility.Collapsed;
            FileMoreButton.Visibility = Visibility.Collapsed;
            await Task.Delay(125, token);
            var snapshot = _model.AllTransfers.Select(FileQueryRecord.Capture).ToArray();
            var results = await Task.Run(() => HistorySearch.Files(snapshot, options, token), token);
            if(revision != _fileRevision || _disposed) return;
            _fileResults = results.Select(item => item.Item).ToArray(); RenderFileResults();
        }
        catch(OperationCanceledException) { }
        catch(Exception error)
        {
            if(revision != _fileRevision || _disposed) return;
            Session.SessionLog.Write("History", "File search failed", error);
            FileCountText.Text = Localization.Strings.Get("查询失败，请重试");
        }
    }
    private void RenderFileResults()
    {
        var view = new ListCollectionView(_fileResults.Take(_fileLimit).ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TransferItem.GroupText)));
        _transferPanelView = view;
        _syncingFileSelection=true;
        try
        {
            TransferList.ItemsSource = view;
            if(_model.FileSelectionMode) foreach(var item in view.Cast<TransferItem>().Where(item=>_selectedFileIds.Contains(item.Id)))
                TransferList.SelectedItems.Add(item);
        }
        finally { _syncingFileSelection=false; }
        UpdateFileSelection();
        if(FileMoreButton is not null) FileMoreButton.Visibility = _fileLimit < _fileResults.Length ? Visibility.Visible : Visibility.Collapsed;
        UpdateFileResultCount();
    }
    private void UpdateFileResultCount()
    {
        if (FileEmptyText is null || _transferPanelView is null) return;
        var count = _fileResults.Length;
        var searching = !string.IsNullOrWhiteSpace(_fileQuery);
        var filtering = _fileDates.IsActive || _fileColumnSelections.Values.Any(values => values.Count > 0);
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
        UpdateFileSortHeaders();
        UpdateFileColumnFilters();
    }
    private void UpdateFileColumnFilters()
    {
        if(FileNameHeader is null || FileSizeHeader is null || FileRouteHeader is null || FileStatusHeader is null || FileTimeHeader is null) return;
        foreach(var (header,title,sort) in new[] {
            (FileNameHeader,"文件名",(HistorySort?)HistorySort.Name),(FileSizeHeader,"大小",(HistorySort?)HistorySort.Size),
            (FileRouteHeader,"来源 → 目标",(HistorySort?)null),(FileStatusHeader,"状态",(HistorySort?)null),(FileTimeHeader,"时间",(HistorySort?)HistorySort.Time) })
        {
            var column = header.FilterColumn;
            var detail = column == "Date" ? string.Join(" · ",new[] { _fileDates.Start is { } start ? $">= {start:d}" : null, _fileDates.End is { } end ? $"<= {end:d}" : null }.Where(value => value is not null)) :
                column.Length == 0 ? "" : string.Join("、",FileColumnChoices(column).Where(item => _fileColumnSelections[column].Contains(item.Key)).Select(item => item.Label));
            header.Update(Localization.Strings.Get(title),sort == _fileSort ? _fileDescending : null,
                column == "Date" ? _fileDates.IsActive : column.Length > 0 && _fileColumnSelections[column].Count > 0,detail);
        }
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
        FileColumnsHeader.Margin = new Thickness(12, 0, 26, 0);
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
    private async void TransferBluetooth_Click(object sender, RoutedEventArgs e)
    {
        var transfer = (sender as FrameworkElement)?.DataContext as TransferItem ?? AttachmentTransfer(sender);
        if (transfer?.CanSwitchToBluetooth != true) return;
        try { await _model.SwitchQueuedToBluetoothAsync(transfer); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowToast(Localization.Strings.Get(error.Message), ToastLevel.Error); }
    }

    private async void TransferReselect_Click(object sender, RoutedEventArgs e)
    {
        var transfer = (sender as FrameworkElement)?.DataContext as TransferItem ?? AttachmentTransfer(sender);
        if (transfer?.CanReselectSource != true) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Title = Localization.Strings.Get("重新选择原文件"), Multiselect = false, CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try { await _model.RetryTransferFromAsync(transfer, picker.FileName); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowToast(Localization.Strings.Get(error.Message), ToastLevel.Error); }
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
}
