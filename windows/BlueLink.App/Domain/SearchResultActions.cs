namespace BlueLink.Domain;

internal static class SearchResultActions
{
    internal static ChatAttachment? Attachment(ChatItem message, string query, HistoryKind kind = HistoryKind.All)
    {
        var attachments = message.Attachments ?? [];
        var term = query.Trim();
        return (term.Length > 0 ? attachments.FirstOrDefault(a => a.FileName.Contains(term, StringComparison.OrdinalIgnoreCase)) : null)
            ?? attachments.FirstOrDefault(a => kind == HistoryKind.Images ? a.IsImage : kind != HistoryKind.Files || !a.IsImage);
    }

    internal static ChatAttachment Current(ChatAttachment attachment, IEnumerable<TransferItem> transfers)
    {
        var transfer = transfers.FirstOrDefault(t => t.Id == attachment.TransferId);
        return attachment.WithTransfer(transfer);
    }
}
