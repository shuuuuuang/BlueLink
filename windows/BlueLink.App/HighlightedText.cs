using System.Globalization;
using BlueLink.Domain;
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

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.RegisterAttached(
        "Background", typeof(Brush), typeof(HighlightedText), new PropertyMetadata(null, Update));
    public static Brush? GetBackground(DependencyObject target) => (Brush?)target.GetValue(BackgroundProperty);
    public static void SetBackground(DependencyObject target, Brush? value) => target.SetValue(BackgroundProperty, value);

    public static string GetText(DependencyObject target) => (string)target.GetValue(TextProperty);
    public static void SetText(DependencyObject target, string value) => target.SetValue(TextProperty, value);
    public static string GetQuery(DependencyObject target) => (string)target.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject target, string value) => target.SetValue(QueryProperty, value);
    public static Brush? GetBrush(DependencyObject target) => (Brush?)target.GetValue(BrushProperty);
    public static void SetBrush(DependencyObject target, Brush value) => target.SetValue(BrushProperty, value);

    public static readonly DependencyProperty PreviewProperty = DependencyProperty.RegisterAttached(
        "Preview", typeof(bool), typeof(HighlightedText), new PropertyMetadata(false, Update));
    public static bool GetPreview(DependencyObject target) => (bool)target.GetValue(PreviewProperty);
    public static void SetPreview(DependencyObject target, bool value) => target.SetValue(PreviewProperty, value);
    public static readonly DependencyProperty FileNameProperty = DependencyProperty.RegisterAttached(
        "FileName", typeof(bool), typeof(HighlightedText), new PropertyMetadata(false, Update));
    public static bool GetFileName(DependencyObject target) => (bool)target.GetValue(FileNameProperty);
    public static void SetFileName(DependencyObject target, bool value) => target.SetValue(FileNameProperty, value);
    private static void Resized(object sender, SizeChangedEventArgs args)
    {
        if (args.WidthChanged) Update((DependencyObject)sender, default);
    }

    private static void Update(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is RichTextBox editor)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            AddMatches(paragraph.Inlines, GetText(target) ?? "", (GetQuery(target) ?? "").Trim(), GetBrush(target), GetBackground(target));
            var document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) };
            foreach (var property in new[] { TextElement.FontFamilyProperty, TextElement.FontSizeProperty, TextElement.FontStyleProperty,
                TextElement.FontWeightProperty, TextElement.FontStretchProperty, TextElement.ForegroundProperty })
                System.Windows.Data.BindingOperations.SetBinding(document, property,
                    new System.Windows.Data.Binding(property.Name) { Source = editor });
            editor.Document = document;
            document.PagePadding = new Thickness(0);
            if (editor is SelectableSearchText selectable) selectable.RefreshSelectionFormatting();
            return;
        }
        if (target is not TextBlock label) return;
        var text = GetText(target) ?? "";
        var query = (GetQuery(target) ?? "").Trim();
        var brush = GetBrush(target);
        label.SizeChanged -= Resized;
        if (GetPreview(label))
        {
            label.SizeChanged += Resized;
            var width = Math.Max(1, label.ActualWidth > 0 ? label.ActualWidth : 300);
            var typeface = new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch);
            FormattedText Measure(string value) => new(value, CultureInfo.CurrentCulture, label.FlowDirection,
                typeface, label.FontSize, brush ?? Brushes.Black, VisualTreeHelper.GetDpi(label).PixelsPerDip)
                { MaxTextWidth = width };
            var lineHeight = Measure("Ag").Height;
            var low = 1; var high = Math.Min(200, Math.Max(1, text.Length));
            var best = SearchExcerpt.Create(text, query, 1, GetFileName(label));
            // Most results are short. Measure their complete excerpt once before searching a budget.
            var complete = SearchExcerpt.Create(text, query, high, GetFileName(label));
            var completeSize = Measure(complete.Text);
            if (completeSize.Height <= lineHeight * 2 + .5 && completeSize.WidthIncludingTrailingWhitespace <= width + .5)
                { best = complete; low = high + 1; }
            while (low <= high)
            {
                var middle = (low + high) / 2;
                var candidate = SearchExcerpt.Create(text, query, middle, GetFileName(label));
                var measured = Measure(candidate.Text);
                if (measured.Height <= lineHeight * 2 + .5 && measured.WidthIncludingTrailingWhitespace <= width + .5)
                    { best = candidate; low = middle + 1; }
                else high = middle - 1;
            }
            label.Inlines.Clear();
            var cursor = 0;
            foreach (var span in best.Highlights)
            {
                label.Inlines.Add(new Run(best.Text[cursor..span.Start]));
                var run = new Run(best.Text.Substring(span.Start, span.Length));
                if (brush is not null) run.Foreground = brush;
                run.Background = GetBackground(target);
                label.Inlines.Add(run); cursor = span.Start + span.Length;
            }
            label.Inlines.Add(new Run(best.Text[cursor..]));
            return;
        }
        label.Inlines.Clear();
        AddMatches(label.Inlines, text, query, brush, GetBackground(target));
    }

    private static void AddMatches(InlineCollection inlines, string text, string query, Brush? brush, Brush? background)
    {
        var offset = 0;
        while (query.Length > 0 && text.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase) is var index && index >= 0)
        {
            inlines.Add(new Run(text[offset..index]));
            var match = new Run(text.Substring(index, query.Length));
            if (brush is not null) match.Foreground = brush;
            match.Background = background;
            inlines.Add(match);
            offset = index + query.Length;
        }
        inlines.Add(new Run(text[offset..]));
    }
}
