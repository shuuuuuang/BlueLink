using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BlueLink.Domain;
using BlueLink.Localization;

namespace BlueLink;

public partial class MainWindow
{
    private void Thumbnail_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Image thumbnail)
            thumbnail.Clip = new RectangleGeometry(new Rect(e.NewSize), 14, 14);
    }

    private void PreserveSelectionOnRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => e.Handled = true;

    private void FileContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { PlacementTarget: FrameworkElement target } menu) return;
        var outgoing = target.DataContext is TransferItem transfer && transfer.Outgoing;
        for (DependencyObject? current = target; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is not ListBoxItem row) continue;
            if (row.DataContext is ChatItem message) outgoing = message.Outgoing;
            else row.IsSelected = true;
            break;
        }
        ConfigureFileContextMenu(menu, target.DataContext, outgoing);
    }

    private void FileNameCopy_Click(object sender, RoutedEventArgs e)
    {
        var name = (sender as FrameworkElement)?.DataContext switch
        {
            ChatAttachment attachment => attachment.FileName,
            TransferItem transfer => transfer.Name,
            _ => null
        };
        if (name is not null) Files.FileInteractionService.CopyFileName(name, Toasts);
    }

    private void NearbyContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu { DataContext: NearbyDevice device } menu && menu.Items[0] is MenuItem connect)
            connect.IsEnabled = device.CanInitiate && !device.IsConnecting && _model.CanStartConnection;
    }

    private void NearbyInfoMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NearbyDevice device }) return;
        InformationWindow.Show(this, _model.DescribeDevice(device));
    }

    internal static void ConfigureFileContextMenu(ContextMenu menu, object source, bool outgoing)
    {
        var (state, canOpen, isImage, active, failed) = source switch
        {
            TransferItem transfer => (transfer.Status.ToString(), transfer.CanOpen,
                transfer.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase), transfer.IsActive, transfer.IsRetryableTerminal),
            ChatAttachment attachment => (attachment.State, attachment.CanOpen, attachment.IsImage,
                attachment.IsTransferActive, attachment.IsRetryableTerminal),
            _ => throw new ArgumentException("A file menu requires a transfer or attachment.", nameof(source))
        };
        menu.DataContext = source;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            var (label, visible) = (item.Tag as string) switch
            {
                "open" => (isImage ? "图片预览" : "打开", canOpen),
                "copy" => ("复制文件", canOpen),
                "copy-name" => ("复制文件名", true),
                "select-many" => ("多选", source is TransferItem),
                "message-select" => ("多选", source is ChatAttachment),
                "locate" => ("在文件夹中显示", canOpen),
                "save" => ("另存为", canOpen),
                "info" => ("查看文件详情", true),
                "pause" => (outgoing ? "暂停传输" : "暂停接收", state is "Transferring" or "Resuming"),
                "resume" => (outgoing ? "继续传输" : "继续接收", state == "Paused"),
                "retry" => (source is TransferItem { RecoveryPending: true } or ChatAttachment { RecoveryPending: true } ? "恢复传输" : "重试", outgoing && failed && (source is TransferItem retry ? retry.CanRetry :
                    source is ChatAttachment attachment && !string.IsNullOrWhiteSpace(attachment.LocalPath) && System.IO.File.Exists(attachment.LocalPath))),
                "bluetooth" => ("改用蓝牙", outgoing && (source is TransferItem queued ? queued.CanSwitchToBluetooth : source is ChatAttachment { State: "Queued", QueuedForUsb: true })),
                "reselect" => ("重新选择原文件", outgoing && failed && (source is TransferItem repair ? repair.CanReselectSource : source is ChatAttachment repairAttachment && repairAttachment.SourceSha256?.Length == 64)),
                "cancel" => (outgoing ? "取消传输" : "取消接收", active),
                "failure" => (source is TransferItem { RecoveryPending: true } or ChatAttachment { RecoveryPending: true } ? "恢复说明" : "查看失败原因", failed),
                "delete" => ("删除本机消息", !active),
                _ => throw new InvalidOperationException("Unknown file menu action.")
            };
            item.Header = Strings.Get(label);
            item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (item.Tag as string == "open" && item.Icon is Image icon)
                icon.SetResourceReference(Image.SourceProperty, isImage ? "FigmaIcon-menu-preview" : "FigmaIcon-menu-open");
        }
        foreach (var separator in menu.Items.OfType<Separator>())
            separator.Visibility = (separator.Tag as string == "active" ? active : !active) ? Visibility.Visible : Visibility.Collapsed;
    }
}
