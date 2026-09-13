using BlueLink.Domain;
using System.IO;

internal sealed class HistoryQueryVerification
{
    public void Run()
    {
        var day = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var attachment = new ChatAttachment(Guid.NewGuid(), Guid.NewGuid(), "需求文档.PDF", "application/pdf", 512);
        var file = new ChatItem(Guid.NewGuid(), "", false, day, MessageStatus.Received, ChatItemKind.File, [attachment]);
        var text = new ChatItem(Guid.NewGuid(), "季度 REVIEW 已完成", true, day, MessageStatus.Read);
        Check(HistoryQuery.Matches(file, "需求文档.pdf", HistoryKind.Files), "attachment names are searched without case sensitivity");
        Check(!HistoryQuery.Matches(file, "需求", HistoryKind.Text), "text-only filter excludes attachment-only messages");
        Check(!HistoryQuery.Matches(file, "需求", HistoryKind.Images), "image filter excludes PDFs");
        Check(HistoryQuery.Matches(text, " review ", HistoryKind.Text), "search trims query and matches body");
        Check(HistoryQuery.Matches(text, "review", HistoryKind.Date, day.LocalDateTime.Date), "date filter combines with text");
        Check(!HistoryQuery.Matches(text, "review", HistoryKind.Date, day.LocalDateTime.Date.AddDays(1)), "date filter excludes another local day");
        var transfer = new TransferItem { Id = Guid.NewGuid(), Name = "Review.PDF", TotalBytes = 512, Outgoing = false, PeerId = "peer-a", Status = TransferStatus.Completed };
        Check(HistoryQuery.Matches(transfer, "review", "Completed", "Incoming", "PEER-A"), "file filters combine name, status, direction and stable peer ID");
        Check(!HistoryQuery.Matches(transfer, "review", "Completed", "Incoming", "peer-b"), "same-name records on another peer cannot leak into scope");
        Check(!HistoryQuery.Matches(transfer, "review", "Active", "Incoming", "peer-a"), "completed item leaves active filter");
        Check(!HistoryQuery.Matches(transfer, "review", "Completed", "Outgoing", "peer-a"), "direction filters are independent");
        transfer.Status = TransferStatus.Failed;
        Check(HistoryQuery.Matches(transfer, "", "Failed", "All", null) && !transfer.CanOpen, "failed files are filterable without an open action");
        var partial = attachment with { State = "Transferring", LocalPath = Path.GetTempFileName() };
        try { Check(!partial.CanOpen && partial.CanPause && !partial.CanDelete, "partial local files expose transfer controls, not open/delete"); }
        finally { File.Delete(partial.LocalPath!); }
        Check(!HistoryQuery.HasMessageTimeGap(text with { CreatedAt = day.AddSeconds(599) }, text), "messages less than ten minutes apart share a time group");
        Check(HistoryQuery.HasMessageTimeGap(text with { CreatedAt = day.AddMinutes(10) }, text), "ten-minute boundary creates an inline timestamp");
        Check(!HistoryQuery.HasMessageTimeGap(text with { CreatedAt = day.AddDays(1) }, text), "next-day messages use the date header instead of a duplicate timestamp");
        Check(!HistoryQuery.HasMessageTimeGap(text with { CreatedAt = day.AddMinutes(-6) }, text), "reverse timestamps do not create a positive time gap");
        Console.WriteLine("BlueLink history query verification passed: 16 checks");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
