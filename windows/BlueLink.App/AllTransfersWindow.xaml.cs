using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BlueLink.Domain;
using BlueLink.Files;

namespace BlueLink;

public partial class AllTransfersWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly MainViewModel _model;
    private readonly ICollectionView _view;

    public AllTransfersWindow(MainViewModel model)
    {
        InitializeComponent(); _model = model; DataContext = model;
        _view = CollectionViewSource.GetDefaultView(model.AllTransfers);
        _view.GroupDescriptions.Clear();
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TransferItem.PeerId)));
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var index = FilterBox.SelectedIndex;
        _view.Filter = value => value is TransferItem transfer && index switch
        {
            1 => transfer.Status is TransferStatus.Offered or TransferStatus.Queued or TransferStatus.Transferring
                or TransferStatus.Paused or TransferStatus.Resuming or TransferStatus.Verifying or TransferStatus.Committing,
            2 => transfer.Status == TransferStatus.Completed,
            3 => transfer.Status is TransferStatus.Rejected or TransferStatus.Failed or TransferStatus.Canceled,
            _ => true,
        };
    }

    private void Locate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem item })
            FileInteractionService.Open(this, new ChatAttachment(item.AttachmentId ?? Guid.Empty, item.Id,
                item.Name, item.MimeType, item.TotalBytes, item.LocalPath, item.Status.ToString()));
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem item }) await _model.PauseTransferAsync(item);
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem item }) await _model.ResumeTransferAsync(item);
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem item }) await _model.CancelTransferAsync(item);
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TransferItem item }) await _model.RetryTransferAsync(item);
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (BlueLinkDialog.Confirm(this, "全部传输", "清除所有传输记录？已接收文件不会被删除。"))
            await _model.ClearTransferHistoryAsync();
    }
}
