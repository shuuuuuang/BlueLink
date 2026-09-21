using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BlueLink.Localization;

namespace BlueLink;

/// <summary>Selection gestures over actual message cards; never include timestamps or avatars.</summary>
internal sealed class MessageSelectionGesture : IDisposable
{
    internal sealed record Row(Guid Id, Rect Bounds);
    private readonly ListBox _list;
    private readonly Func<object, Guid?> _id;
    private readonly Func<IReadOnlySet<Guid>> _selected;
    private readonly Func<Guid?> _anchor;
    private readonly Action<IReadOnlySet<Guid>> _replace;
    private readonly Action<Guid, bool> _toggle;
    private readonly Grid _overlay;
    private readonly Canvas _canvas;
    private readonly Rectangle _rectangle;
    private readonly DispatcherTimer _scrollTimer;
    private readonly Dictionary<Guid, Rect> _knownBounds = [];
    private HashSet<Guid>? _before;
    private HashSet<Guid>? _pending;
    private ScrollViewer? _scroll;
    private Point _start, _pointer;
    private Guid? _clicked;
    private bool _extend, _enabled, _dragging, _updating;
    private double _lineHeight;

    internal Wpf.Ui.Controls.Button RangeButton { get; }
    internal Guid? RangeTarget { get; private set; }
    internal bool IsDragging => _dragging;
    internal bool IsInteracting => _before is not null;

    internal MessageSelectionGesture(ListBox list, Func<object, Guid?> id,
        Func<IReadOnlySet<Guid>> selected, Func<Guid?> anchor,
        Action<IReadOnlySet<Guid>> replace, Action<Guid, bool> toggle, bool rangeOnly = false)
    {
        _list = list; _id = id; _selected = selected; _anchor = anchor; _replace = replace; _toggle = toggle;
        _overlay = new Grid { ClipToBounds = true, Visibility = Visibility.Collapsed };
        _overlay.SetBinding(FrameworkElement.MarginProperty, new Binding(nameof(list.Margin)) { Source = list });
        Grid.SetRow(_overlay, Grid.GetRow(list)); Grid.SetColumn(_overlay, Grid.GetColumn(list));
        Panel.SetZIndex(_overlay, 20);
        ((Panel)list.Parent).Children.Add(_overlay);
        _canvas = new Canvas { IsHitTestVisible = false };
        _rectangle = new Rectangle { Visibility = Visibility.Collapsed, StrokeThickness = 1, Opacity = 0.5 };
        _rectangle.SetResourceReference(Shape.FillProperty, "SoftBlueBrush");
        _rectangle.SetResourceReference(Shape.StrokeProperty, "BlueBrush");
        _canvas.Children.Add(_rectangle); _overlay.Children.Add(_canvas);
        RangeButton = new Wpf.Ui.Controls.Button
        {
            Height = 32, MinHeight = 32, Padding = new Thickness(12, 0, 12, 0), FontSize = 12,
            CornerRadius = new CornerRadius(16), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 4, 0, 4),
            Visibility = Visibility.Collapsed, Focusable = false
        };
        RangeButton.SetResourceReference(FrameworkElement.StyleProperty, "BlueLinkUiButtonStyle");
        RangeButton.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
        RangeButton.SetResourceReference(Control.ForegroundProperty, "MutedBrush");
        RangeButton.Click += Range_Click;
        _overlay.Children.Add(RangeButton);
        _scrollTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Input, AutoScroll, list.Dispatcher);
        _scrollTimer.Stop();
        if (!rangeOnly)
        {
            list.PreviewMouseLeftButtonDown += MouseDown;
            list.PreviewMouseMove += MouseMove;
            list.PreviewMouseLeftButtonUp += MouseUp;
            list.LostMouseCapture += LostCapture;
        }
        list.LayoutUpdated += LayoutUpdated;
        list.Unloaded += Unloaded;
        ((INotifyCollectionChanged)list.Items).CollectionChanged += ItemsChanged;
    }

    internal void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled) { Cancel(false); RangeTarget = null; RangeButton.Visibility = Visibility.Collapsed; }
        _overlay.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    private double Offset => _scroll?.VerticalOffset ?? 0;
    internal Rect Viewport
    {
        get
        {
            _scroll ??= Children<ScrollViewer>(_list).FirstOrDefault();
            var presenter = _scroll is null ? null : Children<ScrollContentPresenter>(_scroll).FirstOrDefault();
            return presenter is null ? new Rect(0, 0, _list.ActualWidth, _list.ActualHeight) : Bounds(presenter);
        }
    }

    internal IReadOnlyList<Row> Rows() => Children<ListBoxItem>(_list)
        .Where(item => !HasCollapsedAncestor(item))
        .Select(item => (_id(item.DataContext), Children<Border>(item).FirstOrDefault(border => border.Name is "Bubble" or "ResultCard" or "FileTableRow")))
        .Where(pair => pair.Item1.HasValue && pair.Item2 is { ActualHeight: > 0, ActualWidth: > 0 })
        .Select(pair => new Row(pair.Item1!.Value, Bounds(pair.Item2!))).ToArray();

    private bool HasCollapsedAncestor(DependencyObject item)
    {
        for (var node = item; node is not null && node != _list; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: not Visibility.Visible }) return true;
        return false;
    }

    private Rect Bounds(FrameworkElement element) => element.TransformToAncestor(_list)
        .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    internal static bool Hits(Rect selection, Rect message, double singleLineHeight)
    {
        if (selection.IsEmpty || message.IsEmpty || singleLineHeight <= 0 || !double.IsFinite(singleLineHeight)) return false;
        var overlap = Math.Min(selection.Bottom, message.Bottom) - Math.Max(selection.Top, message.Top);
        return overlap >= singleLineHeight * 0.5;
    }

    internal static Guid? FindRangeTarget(IReadOnlyList<Guid> order, IReadOnlySet<Guid> selected,
        Guid? anchor, IReadOnlyList<Row> rows, Rect viewport)
    {
        var start = anchor is { } id ? Index(order, id) : -1;
        if (start < 0) return null;
        var visible = rows.Where(row => row.Bounds.Height > 0 && viewport.Contains(row.Bounds))
            .Select(row => Index(order, row.Id)).Where(index => index >= 0).Order().ToArray();
        if (visible.Length == 0) return null;
        var first = visible[0]; var last = visible[^1];
        foreach (var end in start > (first + last) / 2 ? new[] { first, last } : new[] { last, first })
            if (end != start && order.Skip(Math.Min(start, end)).Take(Math.Abs(end - start) + 1).Any(id => !selected.Contains(id)))
                return order[end];
        return null;
    }

    internal void Refresh()
    {
        if (_updating || !_enabled) return;
        _updating = true;
        try
        {
            var viewport = Viewport;
            if (_dragging) { ApplyBox(viewport); return; }
            var order = Order();
            RangeTarget = FindRangeTarget(order, _selected(), _anchor(), Rows(), viewport);
            RangeButton.Visibility = RangeTarget is null ? Visibility.Collapsed : Visibility.Visible;
            if (RangeTarget is not { } target) return;
            var above = Index(order, target) < Index(order, _anchor()!.Value);
            RangeButton.VerticalAlignment = above ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            var label = (above ? "↓  " : "↑  ") + Strings.Get("选择到这里");
            if (!Equals(RangeButton.Content, label)) RangeButton.Content = label;
            System.Windows.Automation.AutomationProperties.SetName(RangeButton, Strings.Get("选择到这里"));
        }
        finally { _updating = false; }
    }

    private IReadOnlyList<Guid> Order() => _list.Items.Cast<object>().Select(_id).OfType<Guid>().ToArray();
    private void Range_Click(object sender, RoutedEventArgs args)
    {
        Refresh();
        if (_enabled && RangeTarget is { } target) _toggle(target, true);
        args.Handled = true;
    }

    internal void Begin(Point point, Guid? clicked, bool extend = false)
    {
        if (!_enabled || !Viewport.Contains(point)) return;
        _before = _selected().ToHashSet(); _knownBounds.Clear(); _pointer = point;
        _start = new Point(point.X, point.Y + Offset); _clicked = clicked; _extend = extend;
        var measure = new TextBlock { Text = "Ag国", FontSize = (double)(_list.TryFindResource("ClientFont13") ?? 13d) };
        if (_list.TryFindResource("HomeFont") is FontFamily font) measure.FontFamily = font;
        measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _lineHeight = measure.DesiredSize.Height + 20; // Standard one-line bubble includes 10 DIP top/bottom padding.
    }

    internal void Move(Point point)
    {
        if (_before is null) return;
        _pointer = point;
        if (!_dragging && Math.Abs(point.X - _start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y + Offset - _start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragging = true; RangeButton.Visibility = Visibility.Collapsed;
        if (_list.IsMouseCaptured) _scrollTimer.Start();
        Refresh();
    }

    private void ApplyBox(Rect viewport)
    {
        if (_before is null) return;
        foreach (var row in Rows())
        {
            var bounds = row.Bounds; bounds.Offset(0, Offset); _knownBounds[row.Id] = bounds;
        }
        var end = new Point(Math.Clamp(_pointer.X, viewport.Left, viewport.Right),
            Math.Clamp(_pointer.Y, viewport.Top, viewport.Bottom) + Offset);
        var box = new Rect(_start, end);
        var current = Order().ToHashSet();
        var result = _before.ToHashSet();
        result.SymmetricExceptWith(_knownBounds.Where(pair => current.Contains(pair.Key) && Hits(box, pair.Value, _lineHeight)).Select(pair => pair.Key));
        result.IntersectWith(current);
        _pending = result; // Commit once on release; dragging only updates the rectangle.
        box.Offset(0, -Offset); box.Intersect(viewport);
        _rectangle.Visibility = box.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (box.IsEmpty) return;
        Canvas.SetLeft(_rectangle, box.Left); Canvas.SetTop(_rectangle, box.Top);
        _rectangle.Width = box.Width; _rectangle.Height = box.Height;
    }

    internal void End(Point point)
    {
        if (_before is null) return;
        if (_dragging) Move(point);
        var click = !_dragging ? _clicked : null; var extend = _extend;
        var pending = _dragging ? _pending : null;
        Cancel(false);
        if (pending is not null && !pending.SetEquals(_selected())) _replace(pending);
        if (click is { } id) _toggle(id, extend);
        Refresh();
    }

    internal void Cancel(bool restore)
    {
        _before = null; _pending = null; _dragging = false; _clicked = null; _knownBounds.Clear(); _scrollTimer.Stop();
        _rectangle.Visibility = Visibility.Collapsed;
        if (_list.IsMouseCaptured) _list.ReleaseMouseCapture();
        // No selection changes are published until End; cancellation discards only the preview.
    }

    private void MouseDown(object sender, MouseButtonEventArgs args)
    {
        if (!_enabled) return;
        Guid? clicked = null;
        for (var node = args.OriginalSource as DependencyObject; node is not null && node != _list; node = Parent(node))
        {
            if (node is ScrollBar) return;
            if (node is ListBoxItem item) clicked = _id(item.DataContext);
        }
        Begin(args.GetPosition(_list), clicked, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        if (_before is null) return;
        args.Handled = true;
        if (!_list.CaptureMouse()) Cancel(true);
    }
    private void MouseMove(object sender, MouseEventArgs args)
    {
        if (_before is null) return;
        if (args.LeftButton != MouseButtonState.Pressed) { Cancel(true); Refresh(); return; }
        Move(args.GetPosition(_list)); args.Handled = true;
    }
    private void MouseUp(object sender, MouseButtonEventArgs args)
    {
        if (_before is null) return;
        End(args.GetPosition(_list)); args.Handled = true;
    }
    private void LostCapture(object sender, MouseEventArgs args) { Cancel(true); Refresh(); }
    private void Unloaded(object sender, RoutedEventArgs args) => Cancel(true);
    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs args) { Cancel(true); Refresh(); }
    private void LayoutUpdated(object? sender, EventArgs args) => Refresh();
    private void AutoScroll(object? sender, EventArgs args)
    {
        if (!_dragging || _scroll is null) return;
        var viewport = Viewport;
        var delta = _pointer.Y < viewport.Top + 24 ? -18 : _pointer.Y > viewport.Bottom - 24 ? 18 : 0;
        if (delta == 0) return;
        _scroll.ScrollToVerticalOffset(Math.Clamp(Offset + delta, 0, _scroll.ScrollableHeight));
    }

    public void Dispose()
    {
        _enabled = false; Cancel(false);
        _list.PreviewMouseLeftButtonDown -= MouseDown; _list.PreviewMouseMove -= MouseMove;
        _list.PreviewMouseLeftButtonUp -= MouseUp; _list.LostMouseCapture -= LostCapture;
        _list.LayoutUpdated -= LayoutUpdated; _list.Unloaded -= Unloaded;
        ((INotifyCollectionChanged)_list.Items).CollectionChanged -= ItemsChanged;
        _scrollTimer.Tick -= AutoScroll; RangeButton.Click -= Range_Click;
        if (_overlay.Parent is Panel parent) parent.Children.Remove(_overlay);
    }
    private static int Index(IReadOnlyList<Guid> order, Guid id)
    {
        for (var i = 0; i < order.Count; i++) if (order[i] == id) return i;
        return -1;
    }
    private static DependencyObject? Parent(DependencyObject node) => node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in Children<T>(child)) yield return descendant;
        }
    }
}
