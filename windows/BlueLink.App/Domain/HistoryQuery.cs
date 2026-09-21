namespace BlueLink.Domain;

public enum HistoryKind { All, Text, Images, Files, Date }

public static class HistoryQuery
{
    // The first message of each day already has a date group header.
    public static bool HasMessageTimeGap(ChatItem current, ChatItem previous) =>
        current.CreatedAt.LocalDateTime.Date == previous.CreatedAt.LocalDateTime.Date &&
        current.CreatedAt - previous.CreatedAt >= TimeSpan.FromMinutes(10);

    public static bool Matches(ChatItem item, string query, HistoryKind kind, DateTime? date = null)
    {
        if (kind == HistoryKind.Text && (item.Kind != ChatItemKind.Text || !item.HasText)) return false;
        if (kind == HistoryKind.Images && item.Kind != ChatItemKind.Image && item.Attachments?.Any(a => a.IsImage) != true) return false;
        if (kind == HistoryKind.Files && item.Kind != ChatItemKind.File && item.Attachments?.Any(a => !a.IsImage) != true) return false;
        if (date is { } day && item.CreatedAt.LocalDateTime.Date != day.Date) return false;
        var term = query.Trim();
        return term.Length == 0 || item.Text.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            item.Attachments?.Any(a => a.FileName.Contains(term, StringComparison.OrdinalIgnoreCase)) == true;
    }

    public static bool Matches(TransferItem item, string query, string status, string direction, string? peerId)
    {
        if (!string.IsNullOrEmpty(peerId) && !string.Equals(item.PeerId, peerId, StringComparison.OrdinalIgnoreCase)) return false;
        if (direction == "Outgoing" && !item.Outgoing || direction == "Incoming" && item.Outgoing) return false;
        if (status == "Active" && !item.IsActive || status == "Completed" && !item.IsCompleted || status == "Incomplete" && !item.IsRetryableTerminal || status == "Failed" && item.Status != TransferStatus.Failed ||
            status == "Rejected" && item.Status != TransferStatus.Rejected || status == "Canceled" && !item.IsCanceled) return false;
        return item.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
