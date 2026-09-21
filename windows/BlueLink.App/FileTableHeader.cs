using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using System.Windows.Automation;
using BlueLink.Localization;
using UiButton = Wpf.Ui.Controls.Button;

namespace BlueLink;

// Composition of official buttons and geometry; no replacement control template.
public sealed class FileTableHeader : StackPanel
{
    private readonly TextBlock _title = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _triangles = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7,0,1,0) };
    private readonly Path _up = new() { Data = Geometry.Parse("M0,4 L4,0 L8,4 Z"), Width = 8, Height = 4, Margin = new Thickness(0,0,0,2) };
    private readonly Path _down = new() { Data = Geometry.Parse("M0,0 L4,4 L8,0 Z"), Width = 8, Height = 4 };
    private readonly Wpf.Ui.Controls.SymbolIcon _filter = new()
    {
        Symbol = Wpf.Ui.Controls.SymbolRegular.Filter20,
        FontSize = 14,
        VerticalAlignment = VerticalAlignment.Center
    };
    private bool _filterOpen;
    public UiButton SortButton { get; }
    public UiButton FilterButton { get; }
    public string SortKey { get; set; } = "";
    public string FilterColumn { get; set; } = "";
    public bool? Descending { get; private set; }
    public bool FilterActive { get; private set; }
    public event EventHandler? SortRequested;
    public event EventHandler? FilterRequested;

    public FileTableHeader()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        _title.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        _triangles.Children.Add(_up); _triangles.Children.Add(_down);
        label.Children.Add(_title); label.Children.Add(_triangles);
        SortButton = Button(label); SortButton.Click += (_,_) => { if(SortKey.Length > 0) SortRequested?.Invoke(this,EventArgs.Empty); else FilterRequested?.Invoke(this,EventArgs.Empty); };
        FilterButton = Button(_filter); FilterButton.Width = 26;
        FilterButton.Click += (_,_) => FilterRequested?.Invoke(this,EventArgs.Empty);
        Children.Add(SortButton); Children.Add(FilterButton);
    }
    private static UiButton Button(object content)
    {
        var button = new UiButton { Content = content, Padding = new Thickness(0), MinHeight = 0, Height = 32, VerticalContentAlignment = VerticalAlignment.Center };
        button.SetResourceReference(FrameworkElement.StyleProperty,"LinkButtonStyle");
        return button;
    }
    public void Update(string title, bool? descending, bool filterActive, string detail = "")
    {
        _title.Text = title; Descending = descending; FilterActive = filterActive;
        _triangles.Visibility = SortKey.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        FilterButton.Visibility = FilterColumn.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _up.SetResourceReference(Shape.FillProperty,descending == false ? "BlueBrush" : "MutedBrush");
        _down.SetResourceReference(Shape.FillProperty,descending == true ? "BlueBrush" : "MutedBrush");
        UpdateFilterAppearance();
        var sortHint = title + " · " + Strings.Get(descending is null ? "排序" : descending.Value ? "降序" : "升序");
        AutomationProperties.SetName(SortButton,SortKey.Length > 0 ? sortHint : title + " · " + Strings.Get("筛选"));
        SortButton.ToolTip = SortKey.Length > 0 ? Strings.Get("点击切换升降序") : title + " · " + detail;
        FilterButton.ToolTip = title + " · " + (detail.Length > 0 ? detail : Strings.Get("筛选"));
        AutomationProperties.SetName(FilterButton,FilterButton.ToolTip.ToString());
    }
    public void SetFilterOpen(bool open)
    {
        _filterOpen = open;
        UpdateFilterAppearance();
    }
    private void UpdateFilterAppearance() =>
        _filter.SetResourceReference(Control.ForegroundProperty, FilterActive || _filterOpen ? "BlueBrush" : "MutedBrush");
}
