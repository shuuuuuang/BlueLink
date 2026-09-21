using System.Diagnostics;
using System.Text.Json;
using System.IO;
using BlueLink.Domain;

internal static class HistorySearchVerification
{
    public static async Task RunAsync(string output)
    {
        var checks = 0;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks++; }
        var day = new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.FromHours(8));
        TransferItem File(int id, string name = "计划.png", long size = 10) => new() { Id = Guid.Parse($"00000000-0000-0000-0000-{id:x12}"),
            Name = name, TotalBytes = size, Outgoing = false, PeerId = "PEER", CreatedAt = day, Status = TransferStatus.Completed };
        var items = new[] { File(3), File(1), File(2) };
        foreach(var sort in Enum.GetValues<HistorySort>()) foreach(var descending in new[] { true, false })
        {
            var options = new FileQueryOptions(Sort:sort, Descending:descending);
            var result = HistorySearch.Files(items.Select(FileQueryRecord.Capture), options);
            Check(result.Select(item => item.Id).SequenceEqual(items.OrderBy(item => item.Id).Select(item => item.Id)), "ID breaks equal sort values");
            Check(HistorySearch.Files(items.Reverse().Select(FileQueryRecord.Capture), options).Select(item => item.Id).SequenceEqual(result.Select(item => item.Id)), "input order does not change pages");
        }
        var date = day.LocalDateTime.Date;
        var options2 = new FileQueryOptions("计划", "Completed", "Incoming", "peer", FileKind.Images, new(date,date));
        Check(HistorySearch.Files(items.Select(FileQueryRecord.Capture), options2).Length == 3, "date type direction peer status keyword combine");
        Check(HistorySearch.Files(items.Select(FileQueryRecord.Capture), options2 with { Dates = new(date.AddDays(1),date) }).Length == 0, "invalid date range has no results");
        Check(HistorySearch.Files(items.Select(FileQueryRecord.Capture), options2 with { Kind = FileKind.Files }).Length == 0, "image extension classification");
        var snapshot = FileQueryRecord.Capture(items[0]); items[0].Status = TransferStatus.Failed;
        Check(HistorySearch.Files([snapshot], options2).Length == 1, "worker fields are immutable");
        Check(HistorySearch.Files([FileQueryRecord.Capture(items[0])], options2).Length == 0, "new snapshot observes state change");
        var text = new ChatItem(Guid.NewGuid(), "中文 review", false, day, MessageStatus.Received);
        Check(HistorySearch.Messages([text], "REVIEW", HistoryKind.Text, new(date,date)).Length == 1, "text type supports independent dates");
        Check(HistorySearch.Messages([text], "REVIEW", HistoryKind.Images, new(date,date)).Length == 0, "dates do not override type");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { HistorySearch.Files([snapshot], options2, canceled.Token); throw new Exception("canceled query ran"); } catch(OperationCanceledException) { checks++; }
        var pages = HistorySearch.Files(Enumerable.Range(1,250).Reverse().Select(id => FileQueryRecord.Capture(File(id))), new());
        Check(pages.Chunk(100).SelectMany(page => page).Select(item => item.Id).Distinct().Count() == 250, "page snapshot has no repeats or omissions");
        var changed = HistorySearch.Files(pages.Where(item => item.Id != File(100).Id).Append(FileQueryRecord.Capture(File(0))), new());
        Check(changed.Length == 250 && changed.Select(item => item.Id).Distinct().Count() == 250 && changed[0].Id == File(0).Id, "new snapshot resets pages after insertion deletion");
        var benchmark = new List<object>();
        foreach(var count in new[] {1000,10000,100000})
        {
            var data = Enumerable.Range(1,count).Select(id => File(id, $"资料-{id % 100}报告.PDF", id)).ToArray();
            var fields = data.Select(FileQueryRecord.Capture).ToArray();
            var options = new FileQueryOptions(Sort:HistorySort.Name);
            for(var i=0;i<3;i++) HistorySearch.Files(fields, options);
            var times = new List<double>(); var allocated = new List<long>();
            for(var i=0;i<20;i++)
            {
                var bytes = GC.GetTotalAllocatedBytes(true); var clock = Stopwatch.StartNew();
                await Task.Delay(125);
                // Include the UI-side immutable snapshot cost as well as worker computation.
                var result = await Task.Run(() => HistorySearch.Files(data.Select(FileQueryRecord.Capture), options));
                times.Add(clock.Elapsed.TotalMilliseconds); allocated.Add(GC.GetTotalAllocatedBytes(true)-bytes);
                Check(result.Length == count, "benchmark retains every match");
            }
            times.Sort(); allocated.Sort();
            benchmark.Add(new { count, p95Ms = times[18], allocatedBytesMedian = allocated[10], debounceMs = 125, includesWpfRender = false });
            Console.WriteLine($"HistorySearch Windows count={count} p95={times[18]:F1} ms (includes debounce, excludes render)");
        }
        Directory.CreateDirectory(output);
        System.IO.File.WriteAllText(Path.Combine(output,"query-benchmark.json"), JsonSerializer.Serialize(new { machine = Environment.MachineName, runtime = Environment.Version.ToString(), processorCount=Environment.ProcessorCount, checks, benchmark }, new JsonSerializerOptions { WriteIndented=true }));
        Console.WriteLine($"HistorySearch verification passed: {checks} checks");
    }
}
