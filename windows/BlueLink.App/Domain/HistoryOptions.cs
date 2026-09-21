using System.Globalization;

namespace BlueLink.Domain;

public enum FileKind { All, Images, Files }
public enum HistorySort { Time, Name, Size }
public readonly record struct HistoryDateRange(DateTime? Start = null, DateTime? End = null)
{
    public bool Contains(DateTime value) => (Start is null || value.Date >= Start.Value.Date) &&
        (End is null || value.Date <= End.Value.Date);
    public bool IsActive => Start is not null || End is not null;
}

public sealed record FileQueryOptions(string Query = "", string Status = "All", string Direction = "All",
    string? PeerId = null, FileKind Kind = FileKind.All, HistoryDateRange Dates = default,
    HistorySort Sort = HistorySort.Time, bool Descending = true, string Culture = "zh-CN",
    IReadOnlyList<string>? Statuses = null, IReadOnlyList<string>? PeerIds = null);

// Immutable search fields: workers never read mutable WPF transfer state.
public sealed record FileQueryRecord(TransferItem Item, Guid Id, string Name, long Size, DateTimeOffset Time,
    string? PeerId, bool Outgoing, TransferStatus Status, bool IsImage)
{
    public static FileQueryRecord Capture(TransferItem item) => new(item, item.Id, item.Name, item.TotalBytes,
        item.CreatedAt, item.PeerId, item.Outgoing, item.Status,
        Files.FileTypeCatalog.Classify(item.Name, item.MimeType) == "file-image");
}

public static class HistorySearch
{
    public static ChatItem[] Messages(IEnumerable<ChatItem> items, string query, HistoryKind kind,
        HistoryDateRange dates = default, CancellationToken token = default) => items.Where(item =>
        { token.ThrowIfCancellationRequested(); return dates.Contains(item.CreatedAt.LocalDateTime) && HistoryQuery.Matches(item, query, kind); })
        .OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Id.ToString("N"), StringComparer.Ordinal).ToArray();

    public static FileQueryRecord[] Files(IEnumerable<FileQueryRecord> items, FileQueryOptions options, CancellationToken token = default)
    {
        var term = options.Query.Trim();
        var filtered = items.Where(item =>
        {
            token.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(options.PeerId) && !string.Equals(item.PeerId, options.PeerId, StringComparison.OrdinalIgnoreCase)) return false;
            if (options.PeerIds is { Count: > 0 } peers && !peers.Contains(item.PeerId, StringComparer.OrdinalIgnoreCase)) return false;
            if (options.Direction == "Outgoing" && !item.Outgoing || options.Direction == "Incoming" && item.Outgoing) return false;
            if (options.Kind == FileKind.Images && !item.IsImage || options.Kind == FileKind.Files && item.IsImage) return false;
            if (!options.Dates.Contains(item.Time.LocalDateTime) || !item.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) return false;
            var terminal = item.Status is TransferStatus.Failed or TransferStatus.Rejected or TransferStatus.Canceled;
            bool MatchesStatus(string status) => status switch {
                "All" => true, "Active" => item.Status != TransferStatus.Completed && !terminal,
                "Incomplete" => terminal, _ => item.Status.ToString() == status };
            return options.Statuses is { Count: > 0 } statuses ? statuses.Any(MatchesStatus) : MatchesStatus(options.Status);
        });
        var names = StringComparer.Create(CultureInfo.GetCultureInfo(options.Culture), true);
        IOrderedEnumerable<FileQueryRecord> sorted = options.Sort switch {
            HistorySort.Name => options.Descending ? filtered.OrderByDescending(item => item.Name, names) : filtered.OrderBy(item => item.Name, names),
            HistorySort.Size => options.Descending ? filtered.OrderByDescending(item => item.Size) : filtered.OrderBy(item => item.Size),
            _ => options.Descending ? filtered.OrderByDescending(item => item.Time) : filtered.OrderBy(item => item.Time) };
        return sorted.ThenBy(item => item.Id.ToString("N"), StringComparer.Ordinal).ToArray();
    }
}
