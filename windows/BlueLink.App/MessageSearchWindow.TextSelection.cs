using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BlueLink.Domain;

namespace BlueLink;

public partial class MessageSearchWindow
{
    private RichTextBox? _selectedSearchText;

    private void SearchText_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RichTextBox text) return;
        if (!text.Selection.IsEmpty)
        {
            ClearSearchTextSelection(text);
            _selectedSearchText = text;
        }
        else if (ReferenceEquals(_selectedSearchText, text)) _selectedSearchText = null;
    }

    private void ClearSearchTextSelection(RichTextBox? keep = null)
    {
        if (_selectedSearchText is not { } selected || ReferenceEquals(selected, keep)) return;
        _selectedSearchText = null;
        selected.Selection.Select(selected.CaretPosition, selected.CaretPosition);
    }

    internal string ResultTextToCopy(ChatItem message) =>
        _selectedSearchText is { DataContext: Result result } text && result.Message.Id == message.Id && !text.Selection.IsEmpty
            ? text.Selection.Text : message.Text;

    private static RichTextBox? SearchTextAt(DependencyObject? source)
    {
        for (var node = source; node is not null;)
        {
            if (node is RichTextBox { Name: "SearchSelectableText" } text) return text;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private void SearchText_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Preserve selection while opening this message's context menu or more button.
        for (var node = e.OriginalSource as DependencyObject; node is not null;)
        {
            if (node is FrameworkElement { DataContext: Result result } && _selectedSearchText?.DataContext is Result selected && result.Message.Id == selected.Message.Id) return;
            if (node is ContextMenu) return;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        ClearSearchTextSelection();
    }
}
