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
        var path = attachment.LocalPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            BlueLinkDialog.Show(owner, "无法打开文件", "文件尚未下载完成，或已从保存目录中移除。");
            return;
        }
        if (attachment.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            new ImagePreviewWindow(path, attachment.FileName) { Owner = owner }.ShowDialog();
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
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
        try { Clipboard.SetDataObject(FileDragDropService.CreateCopyDataObject(path), copy: true); }
        catch (Exception failure)
        {
            BlueLinkDialog.Show(owner, "复制文件失败", failure.Message, BlueLinkDialogTone.Error);
        }
    }

    public static string Describe(ChatAttachment attachment) =>
        $"文件名：{attachment.FileName}{Environment.NewLine}" +
        $"大小：{attachment.SizeText}{Environment.NewLine}" +
        $"类型：{attachment.MimeType}{Environment.NewLine}" +
        $"状态：{attachment.StateText}{Environment.NewLine}" +
        $"本地位置：{attachment.LocalPath ?? "尚未保存"}";

    public static ImageSource? LoadThumbnail(ChatAttachment attachment, int decodeWidth = 320)
    {
        if (!attachment.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(attachment.DisplayPath) || !File.Exists(attachment.DisplayPath)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.UriSource = new Uri(attachment.DisplayPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    private static bool TryGetLocalPath(Window owner, ChatAttachment attachment, out string path)
    {
        path = attachment.LocalPath ?? string.Empty;
        if (File.Exists(path)) return true;
        BlueLinkDialog.Show(owner, "文件不可用", "文件尚未下载完成，或已从保存目录中移除。");
        return false;
    }

}
