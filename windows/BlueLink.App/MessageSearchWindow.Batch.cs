using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BlueLink.Domain;
using BlueLink.Files;
using BlueLink.Localization;

namespace BlueLink;

public partial class MessageSearchWindow
{
    public static readonly DependencyProperty IsBatchSelectingProperty = DependencyProperty.Register(
        nameof(IsBatchSelecting), typeof(bool), typeof(MessageSearchWindow), new PropertyMetadata(false));
    public bool IsBatchSelecting { get => (bool)GetValue(IsBatchSelectingProperty); private set => SetValue(IsBatchSelectingProperty, value); }
    private readonly HashSet<Guid> _searchSelectedIds = [];
    private MessageSelectionGesture? _searchSelectionGesture;
    private readonly HashSet<Guid> _deletedSearchIds = [];
    private Guid? _searchSelectionAnchor;
    private bool _syncingSearchSelection;
    private bool _searchBatchRunning;
    internal IReadOnlyList<ChatItem> SelectedSearchMessages => MessageBatch.Ordered(_searchSource, _searchSelectedIds);

    internal void SetSearchSelectionMode(bool enabled, Guid? initial = null)
    {
        if (_searchBatchRunning || enabled && _model is null) return;
        if (enabled) ClearSearchTextSelection();
        IsBatchSelecting = enabled; _searchSelectionAnchor = initial; _searchSelectedIds.Clear();
        _syncingSearchSelection = true;
        try { Results.UnselectAll(); Results.SelectionMode = enabled ? SelectionMode.Multiple : SelectionMode.Single; }
        finally { _syncingSearchSelection = false; }
        if (enabled && initial is { } id) _searchSelectedIds.Add(id);
        QueryInput.Visibility = SearchFilters.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        SearchSelectionHeader.Visibility = SearchBatchPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (enabled) { DateInput.ClosePopup(); EndDateInput.ClosePopup(); }
        SyncSearchSelection();
        if (enabled) Results.Focus();
    }
    internal void ToggleSearchSelection(Guid id, bool extend)
    {
        if (_searchBatchRunning || !IsBatchSelecting) return;
        BatchSelection.Toggle(_searchSelectedIds, Results.Items.OfType<Result>().Select(item => item.Message.Id).ToArray(),
            _searchSelectionAnchor, id, extend);
        if (_searchSelectedIds.Contains(id) && (!extend || _searchSelectionAnchor is null)) _searchSelectionAnchor = id;
        SyncSearchSelection();
    }
    private void ReplaceSearchSelection(IReadOnlySet<Guid> ids)
    {
        if (_searchBatchRunning || !IsBatchSelecting) return;
        _searchSelectedIds.Clear(); _searchSelectedIds.UnionWith(ids);
        if (_searchSelectionAnchor is null && ids.Count > 0)
            _searchSelectionAnchor = Results.Items.OfType<Result>().First(item => ids.Contains(item.Message.Id)).Message.Id;
        SyncSearchSelection();
    }
    private void SearchSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSearchSelection || !IsBatchSelecting) return;
        foreach (Result item in e.RemovedItems) if (Results.Items.Contains(item)) _searchSelectedIds.Remove(item.Message.Id);
        foreach (Result item in e.AddedItems) _searchSelectedIds.Add(item.Message.Id);
        UpdateSearchBatch();
    }
    private void SyncSearchSelection()
    {
        _syncingSearchSelection = true;
        try
        {
            foreach (var item in Results.Items.OfType<Result>())
                if (_searchSelectedIds.Contains(item.Message.Id)) { if (!Results.SelectedItems.Contains(item)) Results.SelectedItems.Add(item); }
                else if (Results.SelectedItems.Contains(item)) Results.SelectedItems.Remove(item);
        }
        finally { _syncingSearchSelection = false; }
        UpdateSearchBatch();
    }
    private void UpdateSearchBatch()
    {
        _searchSelectionAnchor = BatchSelection.ResolveAnchor(_searchSelectedIds, Results.Items.OfType<Result>().Select(item => item.Message.Id).ToArray(), _searchSelectionAnchor);
        if (SearchSelectionCount is null) return;
        SearchSelectionCount.Text = Strings.Format($"已选择 {_searchSelectedIds.Count} 项");
        SearchSelectionHeader.IsEnabled = Results.IsEnabled = !_searchBatchRunning;
        SearchBatchCopy.IsEnabled = SearchBatchDelete.IsEnabled = !_searchBatchRunning && _searchSelectedIds.Count > 0;
        _searchSelectionGesture?.SetEnabled(IsBatchSelecting && !_searchBatchRunning);
    }
    private void SearchBatchDone_Click(object sender, RoutedEventArgs e) => SetSearchSelectionMode(false);
    private void SearchBatchCopy_Click(object sender, RoutedEventArgs e) =>
        CopySearchSelection(data => Clipboard.SetDataObject(data, true));

    internal void CopySearchSelection(Action<System.Windows.DataObject> publish)
    {
        if (_searchBatchRunning || _searchSelectedIds.Count == 0) return;
        try
        {
            if (!MessageClipboard.TryCreateDataObject(SelectedSearchMessages, _model?.AllTransfers ?? [], out var data))
            { SearchToasts.Show("所选附件尚未完成或不可读取", ToastLevel.Warning); return; }
            publish(data!);
            SetSearchSelectionMode(false);
            SearchToasts.Show("已复制到剪贴板", ToastLevel.Success);
        }
        catch { SearchToasts.Show("复制失败，请稍后重试", ToastLevel.Error); }
    }
    internal async Task<IReadOnlyList<MessageBatchResult>> DeleteSearchSelectionAsync()
    {
        if (_searchBatchRunning || _model is null || _searchPeerId is null || _searchSelectedIds.Count == 0) return [];
        var completed = false;
        _searchBatchRunning = true; UpdateSearchBatch();
        try
        {
            var outcome = await _model.DeleteSelectedMessagesAsync(_searchPeerId, _searchSelectedIds.ToArray());
            _deletedSearchIds.UnionWith(outcome.Where(item => item.Deleted).Select(item => item.Id));
            _searchSelectedIds.ExceptWith(_deletedSearchIds);
            _searchSourceLoaded = false; _sourceIndex = null;
            await RefreshResultsAsync(debounce: false);
            completed = outcome.Any(item => item.Deleted);
            return outcome;
        }
        finally
        {
            _searchBatchRunning = false;
            if (completed) SetSearchSelectionMode(false);
            else SyncSearchSelection();
        }
    }
    private async void SearchBatchDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_searchBatchRunning || _searchSelectedIds.Count == 0) return;
        if (!ConfirmationWindow.Show(this, MainWindow.MessageBatchDeleteDocument(_searchSelectedIds.Count))) return;
        try
        {
            var results = await DeleteSearchSelectionAsync();
            var summary = Strings.Format($"已删除 {results.Count(item => item.Deleted)} 条，跳过或失败 {results.Count(item => !item.Deleted)} 条");
            if (results.Any(item => !item.Deleted)) BlueLinkDialog.Show(this, "批量操作结果", summary + Environment.NewLine +
                string.Join(Environment.NewLine, results.Where(item => !item.Deleted).Select(item => Strings.Get(item.Reason ?? "批量操作失败，请重试")).Distinct()));
            else SearchToasts.Show(summary, ToastLevel.Success);
        }
        catch { SearchToasts.Show("批量操作失败，请重试", ToastLevel.Error); }
    }
}
