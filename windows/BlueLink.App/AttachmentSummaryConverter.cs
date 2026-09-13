using System.Globalization;
using System.Windows;
using System.Windows.Data;
using BlueLink.Domain;
using BlueLink.Localization;

namespace BlueLink;

public sealed class AttachmentSummaryConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length > 0 && values[0] is ChatAttachment attachment
            ? Describe(attachment, values.Length > 1 && values[1] is true) : string.Empty;

    internal static string Describe(ChatAttachment attachment, bool outgoing)
        => $"{attachment.SizeText} · {DescribeState(attachment, outgoing)}";

    internal static string DescribeState(ChatAttachment attachment, bool outgoing)
    {
        var state = attachment.IsMissing ? "文件已移动或删除" : attachment.State switch
        {
            "Offered" or "Queued" => outgoing ? "等待对端接受" : "等待接收",
            "Transferring" => outgoing ? "传输中" : "接收中",
            "Paused" => outgoing ? "传输已暂停" : "已暂停",
            "RemotePaused" => outgoing ? "等待对端继续接收" : "等待对端继续发送",
            "Completed" => outgoing ? "已发送" : "已接收",
            "Failed" => outgoing ? "传输失败" : "接收失败",
            _ => attachment.StateText
        };
        return Strings.Get(state);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
