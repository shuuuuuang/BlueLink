using System.Globalization;
using System.IO;
using BlueLink.Domain;

internal static class SearchExcerptVerification
{
    internal static void Run(Action<bool, string> check, string directory)
    {
        var original = string.Concat(Enumerable.Repeat("开头", 100)) + "Needle-needle" + string.Concat(Enumerable.Repeat("结尾", 100));
        var excerpt = SearchExcerpt.Create(original, " NEEDLE ", 40);
        check(excerpt.Text.StartsWith('…') && excerpt.Text.EndsWith('…'), "late match has context omission markers");
        check(excerpt.Highlights.Select(s => excerpt.Text.Substring(s.Start, s.Length)).SequenceEqual(new[] { "Needle", "needle" }), "both case-insensitive matches highlighted");
        check(original.Length == 413, "original copy source unchanged");
        var filename = string.Concat(Enumerable.Repeat("报告", 80)) + "Needle" + new string('后', 40) + ".pdf";
        excerpt = SearchExcerpt.Create(filename, "needle", 28, true);
        check(excerpt.Text.Contains("Needle") && excerpt.Text.EndsWith(".pdf"), "filename context retains extension");
        var unicode = string.Concat(Enumerable.Repeat("👩‍💻e\u0301🇨🇳", 40));
        for (var budget = 1; budget <= 24; budget++)
        {
            var shown = SearchExcerpt.Create(unicode, "", budget).Text.TrimEnd('…');
            check(unicode.StartsWith(shown, StringComparison.Ordinal) && StringInfo.ParseCombiningCharacters(shown).Length == budget,
                "grapheme boundary preserved at budget " + budget);
        }
        excerpt = SearchExcerpt.Create(new string('x', 100), new string('x', 100), 12);
        check(excerpt.Text == new string('x', 12) + "…" && excerpt.Highlights.Single() == (0, 12), "oversized query highlights visible portion");
        check(SearchExcerpt.Create("", "needle").Text == "", "empty excerpt safe");
        check(SearchExcerpt.Create("report.pdf", " ", fileName: true).Highlights.Count == 0, "cleared search plain");
        excerpt = SearchExcerpt.Create("a\r\nb", "a\r\nb");
        check(excerpt.Text == "a  b" && excerpt.Highlights.Single() == (0, 4), "newlines folded only for display");
        var path = Path.Combine(directory, "retry-source.txt"); File.WriteAllText(path, "QA");
        foreach (var status in Enum.GetValues<TransferStatus>())
        {
            var item = new TransferItem { Id = Guid.NewGuid(), TotalBytes = 10, Name = "report.pdf", Status = status, Outgoing = true, LocalPath = path };
            foreach (var (filter, expected) in new[] { ("Failed", status == TransferStatus.Failed),
                ("Rejected", status == TransferStatus.Rejected), ("Canceled", status == TransferStatus.Canceled),
                ("Incomplete", status is TransferStatus.Failed or TransferStatus.Rejected or TransferStatus.Canceled) })
                check(HistoryQuery.Matches(item, "", filter, "All", null) == expected, "distinct status filter " + filter + "/" + status);
            check(item.CanRetry == (status is TransferStatus.Failed or TransferStatus.Rejected or TransferStatus.Canceled), "explicit retry gate " + status);
            if (status == TransferStatus.Canceled)
            {
                check(!item.IsFailed && item.IsCanceled, "cancellation is neutral but retryable");
                var received = new TransferItem { Id = Guid.NewGuid(), TotalBytes = 10, Name = "report.pdf", Status = status, Outgoing = false, LocalPath = path };
                check(!received.CanRetry, "receiver cannot initiate retry");
            }
        }
    }
}
