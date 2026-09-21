using System.IO;
using System.Windows;
using System.Windows.Controls;
using BlueLink.Domain;
using BlueLink.Files;

namespace BlueLink;

public partial class ImagePreviewWindow
{
    private void PreviewFileMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { DataContext: ChatAttachment attachment } menu) return;
        foreach (var item in menu.Items.OfType<MenuItem>())
            item.IsEnabled = item.Tag as string == "copy-name" || File.Exists(attachment.LocalPath);
    }

    private void PreviewFileMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || PreviewImage.ContextMenu.DataContext is not ChatAttachment attachment) return;
        e.Handled = true;
        try
        {
            switch (item.Tag as string)
            {
                case "copy": FileInteractionService.CopyToClipboard(this, attachment); break;
                case "copy-name": FileInteractionService.CopyFileName(attachment.FileName, PreviewToasts); break;
                case "locate": FileInteractionService.Locate(this, attachment); break;
                case "save": FileInteractionService.SaveCopy(this, attachment); break;
            }
        }
        catch (Exception) { PreviewToasts.Show("操作失败，请稍后重试", ToastLevel.Error); }
    }
}
