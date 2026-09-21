using System.IO;
using System.Windows;

namespace BlueLink;

internal sealed record ImagePreviewEntry(string Path, string Title);

public partial class ImagePreviewWindow
{
    private string _currentPath = "";
    private ImagePreviewEntry[] _gallery = [];
    private int _galleryIndex;

    internal void SetGallery(IEnumerable<ImagePreviewEntry> entries)
    {
        _gallery = entries.Where(item => File.Exists(item.Path)).DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        _galleryIndex = Array.FindIndex(_gallery, item => string.Equals(item.Path, _currentPath, StringComparison.OrdinalIgnoreCase));
        if (_galleryIndex < 0) { _gallery = [new(_currentPath, TitleBarFileName.Text), .. _gallery]; _galleryIndex = 0; }
        PreviousImageZone.Visibility = NextImageZone.Visibility = _gallery.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PreviousImage_Click(object sender, RoutedEventArgs e) { e.Handled = true; NavigateImage(-1); }
    private void NextImage_Click(object sender, RoutedEventArgs e) { e.Handled = true; NavigateImage(1); }

    internal bool NavigateImage(int step)
    {
        if (step is not (-1 or 1)) return false;
        var next = _galleryIndex + step;
        while (next >= 0 && next < _gallery.Length)
        {
            try { LoadEntry(_gallery[next].Path, _gallery[next].Title); _galleryIndex = next; return true; }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException or FormatException or ArgumentException or System.Runtime.InteropServices.COMException)
            { next += step; }
        }
        PreviewToasts.Show(Localization.Strings.Get(next == _galleryIndex + step
            ? step < 0 ? "已经是第一张图片" : "已经是最后一张图片" : "没有更多可用图片"), ToastLevel.Info);
        return false;
    }
}
