using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using BlueLink.Domain;
using BlueLink.Localization;
using UiButton = Wpf.Ui.Controls.Button;

namespace BlueLink;

internal sealed record FileFilterChoice(string Key, string Label, string Group = "");

// A column editor keeps a draft. Only Confirm commits it; dismissal is side-effect free.
internal sealed class FileFilterEditor : Border
{
    private readonly Dictionary<string,CheckBox> _choices = [];
    private readonly HashSet<string> _initial;
    private readonly HistoryDateRange _initialDates;
    private bool _changing;
    public UiButton ConfirmButton { get; }
    public UiButton ResetButton { get; }
    public IReadOnlySet<DateTime> MatchingDates { get; private set; } = new HashSet<DateTime>();
    public RecordDatePicker StartDate { get; } = new();
    public RecordDatePicker EndDate { get; } = new();
    public IReadOnlyDictionary<string,CheckBox> Choices => _choices;
    public string[] Selected => _choices.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToArray();
    public HistoryDateRange Dates => new(StartDate.SelectedDate,EndDate.SelectedDate);
    public event EventHandler? Confirmed;
    public event EventHandler? ResetRequested;
    public event EventHandler? DismissRequested;

    public FileFilterEditor(IEnumerable<FileFilterChoice> choices, IEnumerable<string> selected, HistoryDateRange dates = default, bool dateEditor = false, string title = "", IReadOnlySet<DateTime>? matchingDates = null)
    {
        _initial = selected.ToHashSet(); _initialDates = dates;
        MatchingDates = matchingDates ?? new HashSet<DateTime>();
        CornerRadius = new CornerRadius(10); BorderThickness = new Thickness(1);
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        MinWidth = dateEditor ? 320 : 224; MaxWidth = 336;
        SetResourceReference(BackgroundProperty,"SurfaceBrush"); SetResourceReference(BorderBrushProperty,"BorderBrush");
        Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Opacity = .12, Color = Colors.Black };
        KeyboardNavigation.SetTabNavigation(this,KeyboardNavigationMode.Cycle);
        PreviewKeyDown += (_,e) =>
        {
            if(e.Key != Key.Escape) return;
            e.Handled = true;
            if(HasActiveCalendar) { StartDate.ClosePopup(); EndDate.ClosePopup(); }
            else DismissRequested?.Invoke(this,EventArgs.Empty);
        };
        var grid = new Grid();
        for(var row = 0; row < 3; row++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new TextBlock
        {
            Text = title.Length > 0 ? title : Strings.Get("筛选"), FontSize = 14,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(16,14,16,8)
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty,"InkBrush");
        grid.Children.Add(heading);
        var body = new StackPanel { Margin = new Thickness(16,0,16,12) };
        var scroll = new ScrollViewer { Content = body, MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll,1); grid.Children.Add(scroll);
        string? lastGroup = null;
        foreach(var option in choices)
        {
            if(option.Group.Length > 0 && option.Group != lastGroup)
            {
                var label = new TextBlock { Text = option.Group, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,lastGroup is null ? 2 : 12,0,6) };
                label.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush"); body.Children.Add(label);
            }
            lastGroup = option.Group;
            var text = new TextBlock { Text = option.Label, MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty,"InkBrush");
            var box = new CheckBox { Content = text, ToolTip = option.Label, IsChecked = _initial.Contains(option.Key), MinHeight = 36, VerticalContentAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(box,option.Label);
            _choices.Add(option.Key,box); body.Children.Add(box);
            box.Checked += (_,_) => ChoiceChanged(option.Key); box.Unchecked += (_,_) => ChoiceChanged(option.Key);
        }
        if(dateEditor)
        {
            foreach(var (picker,label,value) in new[] { (StartDate,"开始日期",dates.Start),(EndDate,"结束日期",dates.End) })
            {
                picker.Label = Strings.Get(label); picker.MatchingDates = MatchingDates;
                picker.SelectedDate = value; picker.Margin = new Thickness(0,4,0,4);
                System.Windows.Automation.AutomationProperties.SetName(picker,Strings.Get(label));
                body.Children.Add(picker);
                picker.SelectedDateChanged += (_,_) =>
                {
                    if(_changing) return;
                    _changing=true;
                    try { RecordDatePicker.ResolveRangeConflict(picker,StartDate,EndDate); }
                    finally { _changing=false; }
                    UpdateActions();
                };
            }
        }
        var footer = new Border { BorderThickness = new Thickness(0,1,0,0), Padding = new Thickness(12,10,12,10) };
        footer.SetResourceReference(BorderBrushProperty,"BorderBrush");
        var actions = new Grid();
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ConfirmButton = Action("确认",primary: true); ResetButton = Action("重置");
        ResetButton.HorizontalAlignment = HorizontalAlignment.Left;
        ConfirmButton.Click += (_,_) => { if(ConfirmButton.IsEnabled) Confirmed?.Invoke(this,EventArgs.Empty); };
        ResetButton.Click += (_,_) => ResetRequested?.Invoke(this,EventArgs.Empty);
        actions.Children.Add(ResetButton); Grid.SetColumn(ConfirmButton,1); actions.Children.Add(ConfirmButton);
        footer.Child = actions;
        Grid.SetRow(footer,2); grid.Children.Add(footer); Child = grid;
        UpdateActions();
    }
    private bool HasActiveCalendar => StartDate.IsCalendarInteractionActive || EndDate.IsCalendarInteractionActive;

    // A child popup must own capture until its date click finishes; otherwise the parent
    // treats that click as an outside dismissal and discards the unconfirmed draft.
    internal void ManageCalendarPopups(System.Windows.Controls.Primitives.Popup parent)
    {
        void Changed(object? sender, EventArgs args)
        {
            if(HasActiveCalendar) parent.StaysOpen=true;
            else Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                if(!HasActiveCalendar) parent.StaysOpen=false;
            }));
        }
        StartDate.CalendarInteractionChanged += Changed;
        EndDate.CalendarInteractionChanged += Changed;
        parent.Closed += (_,_) => { StartDate.ClosePopup(); EndDate.ClosePopup(); };
    }

    private static UiButton Action(string label, bool primary = false)
    {
        var button = new UiButton
        {
            Content = Strings.Get(label), FontSize = 13, Padding = new Thickness(12,0,12,0),
            MinHeight = 32, MinWidth = primary ? 72 : 0, CornerRadius = new CornerRadius(6)
        };
        if(primary)
        {
            button.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
        }
        else
        {
            button.SetResourceReference(FrameworkElement.StyleProperty,"LinkButtonStyle");
            button.Resources["ButtonBackgroundDisabled"] = Brushes.Transparent;
            button.Resources["ButtonBorderBrushDisabled"] = Brushes.Transparent;
        }
        return button;
    }
    private void ChoiceChanged(string key)
    {
        if(_changing) return;
        _changing = true;
        if(_choices[key].IsChecked == true && key.StartsWith("peer:"))
        {
            foreach(var pair in _choices.Where(pair => pair.Key.StartsWith("peer:") && pair.Key != key))
                if(key == "peer:@all" || pair.Key == "peer:@all") pair.Value.IsChecked = false;
        }
        _changing = false; UpdateActions();
    }
    private void UpdateActions()
    {
        if(ConfirmButton is null) return;
        var valid = Dates.Start is null || Dates.End is null || Dates.Start <= Dates.End;
        ConfirmButton.IsEnabled = valid && (!_initial.SetEquals(Selected) || Dates != _initialDates);
        ResetButton.IsEnabled = _initial.Count > 0 || _initialDates.IsActive || Selected.Length > 0 || Dates.IsActive;
        ConfirmButton.SetResourceReference(Control.ForegroundProperty,ConfirmButton.IsEnabled ? "OnAccentBrush" : "MutedBrush");
        ResetButton.SetResourceReference(Control.ForegroundProperty,ResetButton.IsEnabled ? "InkBrush" : "MutedBrush");
        ConfirmButton.ToolTip = valid ? null : Strings.Get("开始日期不能晚于结束日期");
    }
}
