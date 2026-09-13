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
    private HistoryKind _kind;
    private readonly string _peerName;
    private readonly bool _showThumbnails;
    private bool _compactResults;
    public static readonly DependencyProperty MatchingDatesProperty = DependencyProperty.Register(
        nameof(MatchingDates), typeof(IReadOnlySet<DateTime>), typeof(MessageSearchWindow));
    public IReadOnlySet<DateTime> MatchingDates
    {
        get => (IReadOnlySet<DateTime>?)GetValue(MatchingDatesProperty) ?? new HashSet<DateTime>();
        private set => SetValue(MatchingDatesProperty, value);
    }
    public ChatItem? SelectedMessage { get; private set; }

    public MessageSearchWindow(string peerName, ObservableCollection<ChatItem> messages, bool showThumbnails = true)
    {
        _messages = messages;
        _peerName = peerName;
        _showThumbnails = showThumbnails;
        InitializeComponent();
        DataContext = this;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Localization.Strings.Language);
        _ = new Appearance.WindowSizePersistence(this, "search");
        SearchTitle.Text = Localization.Strings.Format($"与 {peerName} 的消息记录");
        DateInput.SelectedDate = DateTime.Today;
        messages.CollectionChanged += MessagesChanged;
        Closed += (_, _) => messages.CollectionChanged -= MessagesChanged;
        Loaded += (_, _) => { RefreshResults(); QueryInput.Focus(); };
        ((FrameworkElement)Content).SizeChanged += (_, args) =>
        {
            var compact = args.NewSize.Width < 760;
            if (_compactResults == compact) return;
            _compactResults = compact;
            CalendarPane.Width = compact ? 230 : 260;
            if (IsLoaded) RefreshResults();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    private void MessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshResults();
    private void Filter_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) RefreshResults(); }
    private void Kind_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string kind }) _kind = Enum.Parse<HistoryKind>(kind);
        if (!IsLoaded) return;
        RefreshResults();
    }
    internal void ApplyFilter(string query, HistoryKind kind, DateTime? day = null)
    {
        _kind = kind;
        QueryInput.Text = query;
        if (day is { } value) DateInput.SelectedDate = value;
        foreach (var radio in SearchRadioButtons()) radio.IsChecked = radio.Tag as string == kind.ToString();
        RefreshResults();
    }
    private IEnumerable<RadioButton> SearchRadioButtons()
    {
        var panel = (DockPanel)ResultCount.Parent;
        return panel.Children.OfType<StackPanel>().SelectMany(stack => stack.Children.OfType<RadioButton>());
    }
    private void RefreshResults()
    {
        var dateView = _kind == HistoryKind.Date;
        CalendarPane.Visibility = DateResultHeader.Visibility = dateView ? Visibility.Visible : Visibility.Collapsed;
        MatchingDates = _messages.Where(item => HistoryQuery.Matches(item, QueryInput.Text, HistoryKind.All))
            .Select(item => item.CreatedAt.LocalDateTime.Date).ToHashSet();
        var items = _messages.Where(item => HistoryQuery.Matches(item, QueryInput.Text, _kind, DateInput.SelectedDate))
            .OrderByDescending(item => item.CreatedAt).Select(item => new Result(item, _peerName, _showThumbnails,
                QueryInput.Text, _kind, _compactResults)).ToList();
        var view = new ListCollectionView(items);
        if (!dateView) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Result.DateLabel)));
        Results.ItemsSource = view;
        ResultCount.Text = Localization.Strings.Format($"{items.Count} 个结果");
        DateResultCount.Text = Localization.Strings.Format($"{items.Count} 条记录");
        DateResultTitle.Text = (DateInput.SelectedDate ?? DateTime.Today).ToString(Localization.Strings.Get("M月d日"), Localization.Strings.Culture);
        EmptyMessage.Text = string.IsNullOrWhiteSpace(QueryInput.Text) ? Localization.Strings.Get("没有找到相关消息")
            : Localization.Strings.Format($"未找到包含“{QueryInput.Text.Trim()}”的消息");
        EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Result_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (Results.SelectedItem is not Result result) return;
        SelectedMessage = result.Message;
        DialogResult = true;
    }
    private sealed record Result(ChatItem Message, string PeerName, bool ShowThumbnails, string Query, HistoryKind Kind, bool Compact)
    {
        private (int Width, int Height)? ImageDimensions { get; } = Kind == HistoryKind.Images &&
            Message.Attachments?.FirstOrDefault(a => a.IsImage) is { } imageAttachment
                ? FileInteractionService.ReadImageDimensions(imageAttachment) : null;
        public ImageSource? Thumbnail { get; } = ShowThumbnails &&
            Message.Attachments?.FirstOrDefault(a => a.IsImage && a.CanOpen) is { } image
                ? FileInteractionService.LoadThumbnail(image, 144) : null;
        public bool HasThumbnail => Thumbnail is not null;
        public double ThumbnailWidth => Kind == HistoryKind.Images && !Compact ? 140 : 104;
        public double ThumbnailHeight => Kind == HistoryKind.Images && !Compact ? 90 : 68;
        public bool Stacked => Kind == HistoryKind.Date || Compact;
        public int ActionRow => Stacked ? 1 : 0;
        public Thickness ActionMargin => Stacked ? new(0, 8, 0, 0) : new(0);
        public Thickness ContentMargin => new(14, 0, Stacked ? 0 : 120, 0);
        public ImageSource? Icon => Message.Attachments?.FirstOrDefault() is { } attachment
            ? FileTypeIcons.ForFile(attachment.FileName, attachment.MimeType)
            : new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/BlueLink;component/Assets/Figma/history-message.png"));
        public string DateLabel => Message.CreatedAt.LocalDateTime.Date == DateTime.Today ? Localization.Strings.Get("今天") :
            Message.CreatedAt.LocalDateTime.Date == DateTime.Today.AddDays(-1) ? Localization.Strings.Get("昨天") : Message.CreatedAt.LocalDateTime.ToString(Localization.Strings.Get("yyyy年M月d日"), Localization.Strings.Culture);
        public string Title => Message.HasText ? Message.Text : string.Join("、", Message.Attachments?.Select(a => a.FileName) ?? []);
        public string Meta => $"{(Message.Outgoing ? Localization.Strings.Get("本机") : PeerName)} · {DateLabel} {Message.CreatedAt.LocalDateTime:HH:mm}";
        public string Detail => Message.Attachments?.FirstOrDefault() is { } attachment
            ? ImageDimensions is { } size ? $"{attachment.SizeText} · {size.Width} × {size.Height}"
                : $"{attachment.SizeText} · {attachment.StateText}" : Localization.Strings.Format($"文本消息 · {Message.StatusText}");
    }
}
