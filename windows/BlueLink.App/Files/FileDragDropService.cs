using System.Windows;
using BlueLink.Domain;

namespace BlueLink.Files;

public static class FileDragDropService
{
    internal const string OutboundDragFormat = "BlueLink.OutboundFileDrag";

    internal static bool IsOutboundDrag(System.Windows.IDataObject data) => data.GetDataPresent(OutboundDragFormat, autoConvert: false);

    private const string PreferredDropEffectFormat = "Preferred DropEffect";

    public static IReadOnlyList<string> NormalizeFilePaths(IEnumerable<string>? paths)
    {
        if (paths is null) return [];
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in paths)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string path;
            try { path = Path.GetFullPath(candidate); }
            catch { continue; }
            if (!File.Exists(path) || !seen.Add(path)) continue;
            result.Add(path);
        }
        return result;
    }

    public static IReadOnlyList<string> ExtractFilePaths(System.Windows.IDataObject data)
    {
        if (!data.GetDataPresent(System.Windows.DataFormats.FileDrop) ||
            data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths) return [];
        return NormalizeFilePaths(paths);
    }

    public static long TotalBytes(IEnumerable<string> paths)
    {
        long total = 0;
        foreach (var path in paths)
        {
            try { total = checked(total + new FileInfo(path).Length); }
            catch (OverflowException) { return long.MaxValue; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return total;
    }

    public static bool CanCopyOut(ChatAttachment attachment) =>
        attachment.IsAvailable &&
        string.Equals(attachment.State, "Completed", StringComparison.OrdinalIgnoreCase);

    public static System.Windows.DragDropEffects BeginCopyDrag(DependencyObject source, ChatAttachment attachment)
    {
        if (!CanCopyOut(attachment) || string.IsNullOrWhiteSpace(attachment.LocalPath))
            return System.Windows.DragDropEffects.None;

        var data = CreateCopyDataObject(attachment.LocalPath);
        data.SetData(OutboundDragFormat, "BlueLink");
        return System.Windows.DragDrop.DoDragDrop(source, data, System.Windows.DragDropEffects.Copy);
    }

    public static System.Windows.DataObject CreateCopyDataObject(string path) => CreateCopyDataObject(new[] { path });
    public static System.Windows.DataObject CreateCopyDataObject(IEnumerable<string> paths)
    {
        var data = new System.Windows.DataObject();
        data.SetData(System.Windows.DataFormats.FileDrop, paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        data.SetData(PreferredDropEffectFormat,
            new MemoryStream(BitConverter.GetBytes((int)System.Windows.DragDropEffects.Copy)));
        return data;
    }

    public static string FormatBytes(long value) => value switch
    {
        long.MaxValue => "超大文件",
        >= 1L << 30 => $"{value / (double)(1L << 30):0.0} GiB",
        >= 1L << 20 => $"{value / (double)(1L << 20):0.0} MiB",
        >= 1L << 10 => $"{value / (double)(1L << 10):0.0} KiB",
        _ => $"{value} B"
    };
}
