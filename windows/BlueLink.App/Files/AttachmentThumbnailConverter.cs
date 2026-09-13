using System.Globalization;
using System.Windows.Data;
using BlueLink.Domain;

namespace BlueLink.Files;

public sealed class AttachmentThumbnailConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ChatAttachment attachment ? ChatThumbnailLoader.Load(attachment) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
