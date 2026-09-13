using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlueLink.Domain;
using Microsoft.Win32;

namespace BlueLink.Files;

public static class FileInteractionService
{
    public static void Open(Window owner, ChatAttachment attachment)
    {
        if (!TryGetLocalPath(owner, attachment, out var path)) return;
        if (attachment.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            using var overlay = BlueLinkDialog.DimOwner(owner, "#3D0F172A", owner is MainWindow ? 52 : 0);
            new ImagePreviewWindow(path, attachment.FileName) { Owner = owner }.ShowDialog();
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception failure) { BlueLinkDialog.Show(owner, "无法打开文件", failure.Message, BlueLinkDialogTone.Error); }
    }

    public static void Locate(Window owner, ChatAttachment attachment)
    {
        if (!TryGetLocalPath(owner, attachment, out var path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    public static void SaveCopy(Window owner, ChatAttachment attachment)
    {
        if (!TryGetLocalPath(owner, attachment, out var source)) return;
        var picker = new SaveFileDialog
        {
            Title = "保存文件副本",
            FileName = attachment.FileName,
            OverwritePrompt = true,
            AddExtension = true
        };
        if (picker.ShowDialog(owner) != true) return;
        try { File.Copy(source, picker.FileName, overwrite: true); }
        catch (Exception failure)
        {
            BlueLinkDialog.Show(owner, "保存副本失败", failure.Message, BlueLinkDialogTone.Error);
        }
    }

    public static void CopyToClipboard(Window owner, ChatAttachment attachment)
    {
        if (!TryGetLocalPath(owner, attachment, out var path)) return;
        try
        {
            Clipboard.SetDataObject(FileDragDropService.CreateCopyDataObject(path), copy: true);
            if (owner is MainWindow main) main.ShowToast("已复制文件", ToastLevel.Success);
        }
        catch (Exception failure)
        {
            if (owner is MainWindow main) main.ShowToast("复制失败，请稍后重试", ToastLevel.Error);
            else BlueLinkDialog.Show(owner, "复制文件失败", failure.Message, BlueLinkDialogTone.Error);
        }
    }

    public static string Describe(ChatAttachment attachment) =>
        $"文件名：{attachment.FileName}{Environment.NewLine}" +
        $"大小：{attachment.SizeText}{Environment.NewLine}" +
        $"类型：{attachment.MimeType}{Environment.NewLine}" +
        $"状态：{attachment.StateText}{Environment.NewLine}" +
        $"本地位置：{attachment.LocalPath ?? "尚未保存"}";

    public static string ThumbnailDirectory { get; set; } = Path.Combine(BlueLink.Storage.AppStoragePaths.UserDirectory, "Cache", "Thumbnails");

    public static ImageSource? LoadThumbnail(ChatAttachment attachment, int decodeWidth = 320)
    {
        if (!attachment.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(attachment.DisplayPath) || !File.Exists(attachment.DisplayPath)) return null;
        try
        {
            return DisplayThumbnailCache.Load(ThumbnailDirectory, attachment.DisplayPath, decodeWidth, () =>
            {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.UriSource = new Uri(attachment.DisplayPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
            });
        }
        catch { return null; }
    }

    internal static (int Width, int Height)? ReadImageDimensions(ChatAttachment attachment)
    {
        if (!attachment.IsImage || !attachment.CanOpen) return null;
        try
        {
            using var source = File.OpenRead(attachment.LocalPath!);
            var decoder = BitmapDecoder.Create(source, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch { return null; }
    }

    private static bool TryGetLocalPath(Window owner, ChatAttachment attachment, out string path)
    {
        if (TryGetLocalPath(attachment, out path)) return true;
        FileAvailabilityWindow.Show(owner, attachment);
        return false;
    }

    internal static bool TryGetLocalPath(ChatAttachment attachment, out string path)
    {
        path = attachment.LocalPath ?? string.Empty;
        return attachment.CanOpen && File.Exists(path);
    }

}
