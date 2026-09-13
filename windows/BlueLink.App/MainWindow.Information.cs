using BlueLink.Domain;

namespace BlueLink;

public partial class MainWindow
{
    private async Task ShowAttachmentInformationAsync(ChatAttachment attachment, TransferItem? transfer = null, bool failure = false)
    {
        try
        {
            var document = await _model.DescribeAttachmentAsync(attachment, transfer, failure);
            if (IsVisible && !_disposed) InformationWindow.Show(this, document);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception error)
        {
            if (IsVisible && !_disposed) BlueLinkDialog.Show(this, "无法读取详情", error.Message, BlueLinkDialogTone.Error);
        }
    }
}
