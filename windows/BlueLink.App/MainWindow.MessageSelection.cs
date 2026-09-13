using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BlueLink;

public partial class MainWindow
{
    private TextBox? _selectedMessageText;

    private void MessageText_SelectionChanged(object sender, RoutedEventArgs args)
    {
        if (sender is not TextBox text) return;
        if (text.SelectionLength > 0)
        {
            ClearMessageSelectionExcept(text);
            _selectedMessageText = text;
        }
        else if (ReferenceEquals(_selectedMessageText, text)) _selectedMessageText = null;
    }

    private void ClearMessageSelectionExcept(TextBox? keep)
    {
        if (_selectedMessageText is not { } selected || ReferenceEquals(selected, keep)) return;
        _selectedMessageText = null;
        selected.Select(selected.CaretIndex, 0);
    }

    private static TextBox? MessageTextAt(DependencyObject? source)
    {
        for (var node = source; node is not null;)
        {
            if (node is TextBox { Name: "MessageText" } text) return text;
            // Opening the selected text's menu must retain its active selection for Copy.
            if (node is ContextMenu menu) return MessageTextAt(menu.PlacementTarget);
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
