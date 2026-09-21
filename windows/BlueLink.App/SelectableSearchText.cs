using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Controls;

namespace BlueLink;

// Keep the official RichTextBox selection/editing infrastructure while sizing short
// message bubbles to their text, as the chat's read-only TextBox does.
public sealed class SelectableSearchText : Wpf.Ui.Controls.RichTextBox
{
    private bool _formatting;

    protected override void OnSelectionChanged(RoutedEventArgs e)
    {
        base.OnSelectionChanged(e);
        RefreshSelectionFormatting();
    }

    internal void RefreshSelectionFormatting()
    {
        if (_formatting || Document is null) return;
        _formatting = true;
        try
        {
            // RichTextBox uses an overlay for native selection. Format the selected range
            // instead so opaque backgrounds never cover glyphs. Restore search runs first.
            var all = new TextRange(Document.ContentStart, Document.ContentEnd);
            all.ApplyPropertyValue(TextElement.ForegroundProperty, Foreground);
            all.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Transparent);
            var value = HighlightedText.GetText(this) ?? "";
            var query = (HighlightedText.GetQuery(this) ?? "").Trim();
            for (var offset = 0; query.Length > 0 && value.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase) is var index && index >= 0; offset = index + query.Length)
            {
                var match = new TextRange(PositionAt(index), PositionAt(index + query.Length));
                match.ApplyPropertyValue(TextElement.ForegroundProperty, HighlightedText.GetBrush(this) ?? Foreground);
                match.ApplyPropertyValue(TextElement.BackgroundProperty, HighlightedText.GetBackground(this) ?? Brushes.Transparent);
            }
            if (!Selection.IsEmpty)
            {
                Selection.ApplyPropertyValue(TextElement.BackgroundProperty, SelectionBrush);
                Selection.ApplyPropertyValue(TextElement.ForegroundProperty, SelectionTextBrush);
            }
        }
        finally { _formatting = false; }
    }

    private TextPointer PositionAt(int offset)
    {
        var pointer = Document.ContentStart;
        while (pointer is not null)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var count = pointer.GetTextRunLength(LogicalDirection.Forward);
                if (offset <= count) return pointer.GetPositionAtOffset(offset)!;
                offset -= count;
                pointer = pointer.GetPositionAtOffset(count);
            }
            else pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }
        return Document.ContentEnd;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var text = new FormattedText(HighlightedText.GetText(this) ?? "", CultureInfo.CurrentCulture, FlowDirection,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var width = Math.Min(constraint.Width, Math.Max(1, Math.Ceiling(text.WidthIncludingTrailingWhitespace) + Document.PagePadding.Left + Document.PagePadding.Right + 4));
        return base.MeasureOverride(new Size(width, constraint.Height));
    }
}
