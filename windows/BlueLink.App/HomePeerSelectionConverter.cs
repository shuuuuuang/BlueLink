using System.Globalization;
using System.Windows.Data;

namespace BlueLink;

public sealed class HomePeerSelectionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [string peerId, string activePeerId] &&
        string.Equals(peerId, activePeerId, StringComparison.OrdinalIgnoreCase);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
