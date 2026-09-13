using System.Globalization;
using System.Windows;
using System.Windows.Data;
using BlueLink.Domain;

namespace BlueLink;

public sealed class MessageTimeGapConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [ChatItem current, ChatItem previous] && HistoryQuery.HasMessageTimeGap(current, previous)
            ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
