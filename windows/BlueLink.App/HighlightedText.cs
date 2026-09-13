using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace BlueLink;

/// <summary>Highlights query matches without changing a label's layout or trimming.</summary>
public static class HighlightedText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(HighlightedText), new PropertyMetadata("", Update));
    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(HighlightedText), new PropertyMetadata("", Update));
    public static readonly DependencyProperty BrushProperty = DependencyProperty.RegisterAttached(
        "Brush", typeof(Brush), typeof(HighlightedText), new PropertyMetadata(null, Update));

    public static string GetText(DependencyObject target) => (string)target.GetValue(TextProperty);
    public static void SetText(DependencyObject target, string value) => target.SetValue(TextProperty, value);
    public static string GetQuery(DependencyObject target) => (string)target.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject target, string value) => target.SetValue(QueryProperty, value);
    public static Brush? GetBrush(DependencyObject target) => (Brush?)target.GetValue(BrushProperty);
    public static void SetBrush(DependencyObject target, Brush value) => target.SetValue(BrushProperty, value);

    private static void Update(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBlock label) return;
        var text = GetText(target) ?? "";
        var query = (GetQuery(target) ?? "").Trim();
        var brush = GetBrush(target);
        label.Inlines.Clear();
        var offset = 0;
        while (query.Length > 0 && text.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase) is var index && index >= 0)
        {
            label.Inlines.Add(new Run(text[offset..index]));
            var match = new Run(text.Substring(index, query.Length));
            if (brush is not null) match.Foreground = brush;
            label.Inlines.Add(match);
            offset = index + query.Length;
        }
        label.Inlines.Add(new Run(text[offset..]));
    }
}
