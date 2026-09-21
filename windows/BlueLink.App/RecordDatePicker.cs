using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using BlueLink.Localization;

namespace BlueLink;

// Product composition of official controls: no editable date text box or replacement templates.
public sealed class RecordDatePicker : UserControl
{
    public static readonly DependencyProperty SelectedDateProperty = DependencyProperty.Register(nameof(SelectedDate), typeof(DateTime?), typeof(RecordDatePicker),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed));
    public static readonly DependencyProperty MatchingDatesProperty = DependencyProperty.Register(nameof(MatchingDates), typeof(IReadOnlySet<DateTime>), typeof(RecordDatePicker), new PropertyMetadata(null));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(RecordDatePicker), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(RecordDatePicker), new PropertyMetadata(false, Changed));
    public DateTime? SelectedDate { get => (DateTime?)GetValue(SelectedDateProperty); set => SetValue(SelectedDateProperty, value?.Date); }
    public IReadOnlySet<DateTime>? MatchingDates { get => (IReadOnlySet<DateTime>?)GetValue(MatchingDatesProperty); set => SetValue(MatchingDatesProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public bool Compact { get => (bool)GetValue(CompactProperty); set => SetValue(CompactProperty, value); }
    public event EventHandler<RoutedEventArgs>? SelectedDateChanged;
    internal Wpf.Ui.Controls.Button Trigger { get; }
    internal Calendar Calendar { get; }
    internal Border CalendarPanel { get; }
    internal Popup CalendarPopup { get; }
    private readonly TextBlock _caption;
    private readonly TextBlock _label;
    private readonly Wpf.Ui.Controls.FontIcon _icon;
    private bool _synchronizing;
    private bool _calendarPointerDown;
    private bool _closeAfterPointerUp;
    private System.Windows.Threading.DispatcherOperation? _pendingClose;
    internal bool IsCalendarInteractionActive { get; private set; }
    internal event EventHandler? CalendarInteractionChanged;

    internal void BeginCalendarInteraction()
    {
        IsCalendarInteractionActive = true;
        CalendarInteractionChanged?.Invoke(this, EventArgs.Empty);
    }
    private void EndCalendarInteraction()
    {
        if (!IsCalendarInteractionActive) return;
        IsCalendarInteractionActive = false;
        CalendarInteractionChanged?.Invoke(this, EventArgs.Empty);
    }
    internal static void ResolveRangeConflict(RecordDatePicker changed, RecordDatePicker start, RecordDatePicker end)
    {
        if (start.SelectedDate is { } first && end.SelectedDate is { } last && first > last)
            (ReferenceEquals(changed, start) ? end : start).SetCurrentValue(SelectedDateProperty, null);
    }

    public RecordDatePicker()
    {
        Focusable = false;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Strings.Language);
        var host = new Grid(); Content = host;
        _caption = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        _label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        _label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _icon = new Wpf.Ui.Controls.FontIcon { Glyph = "\uE787", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var buttonContent = new DockPanel();
        DockPanel.SetDock(_label, Dock.Left);
        DockPanel.SetDock(_icon, Dock.Right);
        buttonContent.Children.Add(_label);
        buttonContent.Children.Add(_icon);
        buttonContent.Children.Add(_caption);
        Trigger = new Wpf.Ui.Controls.Button { Content = buttonContent, MinHeight = 36, Padding = new Thickness(12, 0, 12, 0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        host.Children.Add(Trigger);
        Calendar = new Calendar { SelectionMode = CalendarSelectionMode.SingleDate, FirstDayOfWeek = DayOfWeek.Monday, IsTodayHighlighted = true, Width = 280, MinWidth = 0, DataContext = this, Language = Language };
        Calendar.SetResourceReference(StyleProperty, "DefaultCalendarStyle");
        Calendar.SetResourceReference(Calendar.CalendarDayButtonStyleProperty, "RecordCalendarDay");
        var body = new StackPanel();
        body.Children.Add(new Viewbox { Child = Calendar, Stretch = Stretch.Uniform, Width = 280 });
        var hint = new TextBlock { Text = Strings.Get("蓝点表示当天存在匹配记录"), FontSize = 11, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); body.Children.Add(hint);
        CalendarPanel = new Border
        {
            Child = body,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = .12, Color = Colors.Black }
        };
        CalendarPanel.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush"); CalendarPanel.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); CalendarPanel.SetResourceReference(TextElement.ForegroundProperty, "InkBrush");
        CalendarPopup = new Popup { Child = CalendarPanel, PlacementTarget = Trigger, Placement = PlacementMode.Bottom, VerticalOffset = 6, AllowsTransparency = true, StaysOpen = false };
        host.Children.Add(CalendarPopup);
        CalendarPopup.Opened += (_, _) => Calendar.Focus();
        CalendarPopup.Closed += (_, _) => { ResetPendingClose(); EndCalendarInteraction(); };
        Trigger.Click += (_, _) =>
        {
            if (CalendarPopup.IsOpen) { ClosePopup(); return; }
            SynchronizeCalendar();
            Calendar.DisplayDate = SelectedDate ?? DateTime.Today;
            ResetPendingClose();
            BeginCalendarInteraction();
            try { CalendarPopup.IsOpen = true; }
            catch { EndCalendarInteraction(); throw; }
        };
        Calendar.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, args) =>
        {
            if (args.ChangedButton == MouseButton.Left) _calendarPointerDown = true;
        }), handledEventsToo: true);
        Calendar.SelectedDatesChanged += (_, _) =>
        {
            if (_synchronizing) return;
            SetCurrentValue(SelectedDateProperty, Calendar.SelectedDate);
            // Calendar selects on MouseDown. Keep its HWND alive through MouseUp so
            // the release cannot hit the filter behind it and discard the draft.
            if (_calendarPointerDown) _closeAfterPointerUp = true;
            else QueueClose(); // Keyboard/UIA selection has no pending pointer release.
        };
        CalendarPanel.PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { ClosePopup(); args.Handled = true; } };
        // Close an already-selected day after its own mouse handlers finish.
        Calendar.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler((_, args) =>
        {
            if (args.ChangedButton != MouseButton.Left) return;
            _calendarPointerDown = false;
            var close = _closeAfterPointerUp;
            for (var node = args.OriginalSource as DependencyObject; node is not null && node != Calendar; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
                if (node is CalendarDayButton { IsEnabled: true, IsBlackedOut: false }) { close = true; break; }
            if (close) QueueClose();
        }), handledEventsToo: true);
        Unloaded += (_, _) => ClosePopup();
        Update();
    }
    public void ClosePopup()
    {
        CalendarPopup.IsOpen = false;
        ResetPendingClose();
        EndCalendarInteraction();
    }
    private void QueueClose()
    {
        if (_pendingClose is not null) return;
        _pendingClose = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            _pendingClose = null;
            ClosePopup();
        }));
    }
    private void ResetPendingClose()
    {
        _pendingClose?.Abort();
        _pendingClose = null;
        _calendarPointerDown = false;
        _closeAfterPointerUp = false;
    }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var picker = (RecordDatePicker)sender;
        if (picker.Trigger is null) return;
        picker.Update();
        if (args.Property == SelectedDateProperty) picker.SelectedDateChanged?.Invoke(picker, new RoutedEventArgs());
    }
    private void Update()
    {
        _caption.Text = SelectedDate?.ToString("yyyy-MM-dd", Strings.Culture) ?? (Compact ? Label : Strings.Get("不限"));
        _caption.HorizontalAlignment = Compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        _caption.SetResourceReference(TextBlock.ForegroundProperty, Compact || SelectedDate is not null ? "BlueBrush" : "InkBrush");
        _label.Text = Label;
        _label.Visibility = Compact ? Visibility.Collapsed : Visibility.Visible;
        Trigger.MinHeight = Compact ? 36 : 44;
        Trigger.Padding = new Thickness(Compact ? 6 : 12, 0, Compact ? 6 : 12, 0);
        Trigger.CornerRadius = new CornerRadius(8);
        System.Windows.Automation.AutomationProperties.SetName(Trigger, Label + (SelectedDate is { } date ? " " + date.ToString("yyyy-MM-dd") : ""));
        Trigger.Appearance = Compact ? Wpf.Ui.Controls.ControlAppearance.Transparent : Wpf.Ui.Controls.ControlAppearance.Secondary;
        Trigger.SetResourceReference(Control.ForegroundProperty, Compact ? "BlueBrush" : "InkBrush");
        _icon.Visibility = Compact ? Visibility.Collapsed : Visibility.Visible;
        SynchronizeCalendar();
    }
    private void SynchronizeCalendar()
    {
        _synchronizing = true;
        try
        {
            Calendar.SelectedDate = SelectedDate;
        }
        finally { _synchronizing = false; }
    }
}
