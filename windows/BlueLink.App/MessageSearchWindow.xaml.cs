using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using BlueLink.Domain;
using BlueLink.Files;

namespace BlueLink;

public partial class MessageSearchWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly ObservableCollection<ChatItem> _messages;
    private readonly MainViewModel? _model;
    private readonly string? _searchPeerId;
    private ChatItem[] _searchSource = [];
    private bool _searchSourceLoaded;
    private Dictionary<Guid, ChatItem>? _sourceIndex;
    private HistoryKind _kind;
    private readonly string _peerName;
    private readonly bool _showThumbnails;
    private bool _compactResults;
    private bool _changingDates;
    private CancellationTokenSource? _queryCancellation;
    private int _queryRevision;
    private int _resultLimit = 100;
    private ChatItem[] _queryResults = [];
    private HistoryDateRange Dates => new(DateInput.SelectedDate, EndDateInput.SelectedDate);
    public static readonly DependencyProperty MatchingDatesProperty = DependencyProperty.Register(
        nameof(MatchingDates), typeof(IReadOnlySet<DateTime>), typeof(MessageSearchWindow));
    public IReadOnlySet<DateTime> MatchingDates
    {
        get => (IReadOnlySet<DateTime>?)GetValue(MatchingDatesProperty) ?? new HashSet<DateTime>();
        private set => SetValue(MatchingDatesProperty, value);
    }
    public ChatItem? SelectedMessage { get; private set; }

    public MessageSearchWindow(string peerName, ObservableCollection<ChatItem> messages, bool showThumbnails = true, MainViewModel? model = null)
    {
        _messages = messages;
        _model = model;
        _searchPeerId = model?.ActivePeerId;
        _peerName = peerName;
        _showThumbnails = showThumbnails;
        InitializeComponent();
        _searchSelectionGesture = new MessageSelectionGesture(Results, item => (item as Result)?.Message.Id,
            () => _searchSelectedIds, () => _searchSelectionAnchor, ReplaceSearchSelection, ToggleSearchSelection);
        PreviewMouseDown += SearchText_PreviewMouseDown;
        DataContext = this;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Localization.Strings.Language);
        _ = new Appearance.WindowSizePersistence(this, "search");
        SearchTitle.Text = Localization.Strings.Format($"与 {peerName} 的消息记录");
        messages.CollectionChanged += MessagesChanged;
        Closed += (_, _) => { _searchSelectionGesture?.Dispose(); _queryCancellation?.Cancel(); _queryRevision++; messages.CollectionChanged -= MessagesChanged; if (_resultMenu is not null) _resultMenu.IsOpen = false; DateInput.ClosePopup(); EndDateInput.ClosePopup(); SearchToasts.Dispose(); };
        Loaded += (_, _) => { QueueResults(); QueryInput.Focus(); };
        ((FrameworkElement)Content).SizeChanged += (_, args) =>
        {
            var compact = args.NewSize.Width < 760;
            if (_compactResults == compact) return;
            _compactResults = compact;
            foreach(var tab in SearchRadioButtons())
            {
                tab.Width = compact ? 48 : 64;
                tab.Margin = new Thickness(0,0,compact ? 4 : 14,0);
                ((Grid)tab.Content).Width = tab.Width;
            }
            if (IsLoaded) QueueResults();
        };
        Closing += (_, e) => { if (_searchBatchRunning) e.Cancel = true; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _resultMenu?.IsOpen != true)
            {
                if (DateInput.CalendarPopup.IsOpen || EndDateInput.CalendarPopup.IsOpen) { DateInput.ClosePopup(); EndDateInput.ClosePopup(); }
                else if (IsBatchSelecting) { if (!_searchBatchRunning) SetSearchSelectionMode(false); }
                else Close();
                e.Handled = true;
            }
            else if (IsBatchSelecting && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.A) { e.Handled = true; }
                if (e.Key == Key.C) { SearchBatchCopy_Click(this, e); e.Handled = true; }
            }
        };
    }

    private void MessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset) { _searchSourceLoaded = false; _sourceIndex = null; }
        else if (_sourceIndex is not null)
        {
            if(e.OldItems is not null) foreach(ChatItem item in e.OldItems) _sourceIndex.Remove(item.Id);
            if(e.NewItems is not null) foreach(ChatItem item in e.NewItems) _sourceIndex[item.Id] = item;
        }
        _ = RefreshResultsAsync(debounce:false);
    }
    private void Filter_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) QueueResults(); }
    private void Kind_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string kind }) _kind = Enum.Parse<HistoryKind>(kind);
        if (!IsLoaded) return;
        QueueResults();
    }
    internal void ApplyFilter(string query, HistoryKind kind, DateTime? day = null)
    {
        _kind = kind;
        _changingDates=true;
        try { DateInput.SelectedDate=day ?? (kind==HistoryKind.Date ? DateTime.Today : null); EndDateInput.SelectedDate=DateInput.SelectedDate; }
        finally { _changingDates=false; }
        QueryInput.Text = query;
        foreach (var radio in SearchRadioButtons()) radio.IsChecked = radio.Tag as string == kind.ToString();
        RefreshResults();
    }
    private IEnumerable<RadioButton> SearchRadioButtons() => SearchKinds.Children.OfType<RadioButton>();
    private void DateFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_changingDates || ClearDateFilters is null) return;
        _changingDates=true;
        try { RecordDatePicker.ResolveRangeConflict((RecordDatePicker)sender,DateInput,EndDateInput); }
        finally { _changingDates=false; }
        UpdateDateFilters();
        if (IsLoaded) QueueResults();
    }
    private void UpdateDateFilters() => ClearDateFilters.Visibility = Dates.IsActive ? Visibility.Visible : Visibility.Collapsed;
    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _changingDates=true;
        try { DateInput.SelectedDate=null; EndDateInput.SelectedDate=null; }
        finally { _changingDates=false; }
        UpdateDateFilters();
        QueueResults();
    }
    private void QueueResults() => _ = RefreshResultsAsync();
    internal async Task RefreshResultsAsync(bool debounce = true)
    {
        _queryCancellation?.Cancel(); _queryCancellation?.Dispose();
        var cancellation = new CancellationTokenSource(); _queryCancellation = cancellation;
        var token = cancellation.Token; var revision = ++_queryRevision;
        _resultLimit = 100;
        var query = QueryInput.Text; var kind = _kind; var dates = Dates;
        _syncingSearchSelection = true;
        try { Results.ItemsSource = null; }
        finally { _syncingSearchSelection = false; }
        MoreResultsButton.Visibility = Visibility.Collapsed;
        ResultCount.Text = Localization.Strings.Get("正在查询…"); EmptyState.Visibility = Visibility.Collapsed;
        try
        {
            if (debounce) await Task.Delay(125, token);
            var snapshot = _model is not null && _searchPeerId is not null
                ? _searchSourceLoaded ? (_sourceIndex?.Values.ToArray() ?? _searchSource) : (await _model.LoadSearchHistoryAsync(_searchPeerId)).ToArray()
                : _messages.ToArray();
            snapshot = snapshot.Where(item => !_deletedSearchIds.Contains(item.Id)).ToArray();
            token.ThrowIfCancellationRequested();
            var result = await Task.Run(() => Query(snapshot, query, kind, dates, token), token);
            if (revision != _queryRevision || token.IsCancellationRequested) return;
            _searchSourceLoaded = true; _sourceIndex = snapshot.ToDictionary(item => item.Id); _searchSource = snapshot; _queryResults = result.Items; MatchingDates = result.Days; RenderResults();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (revision != _queryRevision) return;
            Session.SessionLog.Write("History", "Search failed", error);
            ResultCount.Text = Localization.Strings.Get("查询失败，请重试");
            _queryResults = []; Results.ItemsSource = null;
        }
    }
    private static (ChatItem[] Items, HashSet<DateTime> Days) Query(ChatItem[] snapshot, string query, HistoryKind kind,
        HistoryDateRange dates, CancellationToken token = default)
    {
        var matches = HistorySearch.Messages(snapshot, query, kind, token: token);
        return (matches.Where(item => dates.Contains(item.CreatedAt.LocalDateTime)).ToArray(),
            matches.Select(item => item.CreatedAt.LocalDateTime.Date).ToHashSet());
    }
    // Used by deterministic offscreen fixtures before a native window is loaded.
    private void RefreshResults()
    {
        _queryCancellation?.Cancel(); _queryRevision++; _resultLimit = 100;
        var snapshot = _messages.ToArray();
        var result = Query(snapshot, QueryInput.Text, _kind, Dates);
        _searchSourceLoaded = true; _sourceIndex = snapshot.ToDictionary(item => item.Id); _searchSource = snapshot; _queryResults = result.Items; MatchingDates = result.Days; RenderResults();
    }
    private void MoreResults_Click(object sender, RoutedEventArgs e) { _resultLimit += 100; RenderResults(); }
    private void RenderResults()
    {
        UpdateDateFilters();
        var items = _queryResults.Take(_resultLimit).Select(item => new Result(item, _showThumbnails,
            QueryInput.Text, _kind, _compactResults)).ToList();
        var view = new ListCollectionView(items);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Result.DateLabel)));
        _syncingSearchSelection = true;
        try { Results.ItemsSource = view; }
        finally { _syncingSearchSelection = false; }
        if (IsBatchSelecting) _searchSelectedIds.IntersectWith(_queryResults.Select(item => item.Id));
        SyncSearchSelection();
        ResultCount.Text = Localization.Strings.Format($"{_queryResults.Length} 个结果");
        EmptyMessage.Text = string.IsNullOrWhiteSpace(QueryInput.Text) ? Localization.Strings.Get("没有符合当前筛选条件的消息")
            : Localization.Strings.Format($"未找到包含“{QueryInput.Text.Trim()}”的消息");
        EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MoreResultsButton.Visibility = _resultLimit < _queryResults.Length ? Visibility.Visible : Visibility.Collapsed;
    }
    private sealed record Result(ChatItem Message, bool ShowThumbnails, string Query, HistoryKind Kind, bool Compact)
    {
        public ChatAttachment? Attachment { get; } = SearchResultActions.Attachment(Message, Query, Kind);
        private (int Width, int Height)? ImageDimensions { get; } = Kind == HistoryKind.Images &&
            SearchResultActions.Attachment(Message, Query, Kind) is { IsImage: true } imageAttachment
                ? FileInteractionService.ReadImageDimensions(imageAttachment) : null;
        public ImageSource? Thumbnail { get; } = ShowThumbnails &&
            SearchResultActions.Attachment(Message, Query, Kind) is { IsImage: true, CanOpen: true } image
                ? ChatThumbnailLoader.Load(image) : null;
        public bool HasThumbnail => Thumbnail is not null;
        public double ThumbnailWidth => Thumbnail?.Width ?? 0;
        public double ThumbnailHeight => Thumbnail?.Height ?? 0;
        public bool HasAttachment => Attachment is not null;
        public double BubbleMaxWidth => Compact ? 240 : 440;
        public ImageSource? Icon => Attachment is { } attachment
            ? FileTypeIcons.ForFile(attachment.FileName, attachment.MimeType)
            : new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/BlueLink;component/Assets/Figma/history-message.png"));
        public string DateLabel => Message.CreatedAt.LocalDateTime.Date == DateTime.Today ? Localization.Strings.Get("今天") :
            Message.CreatedAt.LocalDateTime.Date == DateTime.Today.AddDays(-1) ? Localization.Strings.Get("昨天") : Message.CreatedAt.LocalDateTime.ToString(Localization.Strings.Get("yyyy年M月d日"), Localization.Strings.Culture);
        public string Title => IsFileName ? Attachment!.FileName : Message.Text;
        public bool IsFileName => Attachment is not null;
        private string DisplayStatus => Attachment is { } attachment &&
            (attachment.State != "Completed" || attachment.RecoveryPending || attachment.IsMissing)
                ? attachment.StateText : Message.StatusText;
        public string StatusTime => $"{DisplayStatus} · {Message.CreatedAt.LocalDateTime:HH:mm}";
        public string FileToolTip => ImageDimensions is { } size
            ? $"{Title}\n{size.Width} × {size.Height}" : Title;
    }
}
