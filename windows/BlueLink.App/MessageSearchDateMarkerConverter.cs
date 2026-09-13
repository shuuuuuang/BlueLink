using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BlueLink;

public sealed class MessageSearchDateMarkerConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [DateTime day, IReadOnlySet<DateTime> matches] && matches.Contains(day.Date)
            ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
