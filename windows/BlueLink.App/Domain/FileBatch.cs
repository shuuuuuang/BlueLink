namespace BlueLink.Domain;

public enum FileBatchAction { Retry, Cancel, DeleteRecords, Copy }
public enum FileBatchOutcome { Submitted, Skipped, Failed }
public sealed record FileBatchEntry(Guid Id, string Name, FileBatchOutcome Outcome, string? Reason = null);
public sealed record FileBatchCheck(Guid Id, string Name, bool Eligible, string? Reason = null);

public static class FileBatchPolicy
{
    public const int MaximumSelection = 100;
    public static FileBatchCheck Check(Guid id, TransferItem? item, FileBatchAction action, bool online)
    {
        string? reason = item is null ? "记录已不存在" : action switch {
            FileBatchAction.Retry when !item.Outgoing || !item.IsRetryableTerminal => "当前状态不支持重试",
            FileBatchAction.Retry when !online => "设备当前未连接",
            FileBatchAction.Retry when !item.CanRetry => "原文件不可读取",
            FileBatchAction.Cancel when !item.IsActive => "任务已结束",
            FileBatchAction.Cancel when !online => "设备当前未连接",
            FileBatchAction.DeleteRecords when !item.CanDelete => "请先取消正在进行的任务",
            FileBatchAction.Copy when !item.IsCompleted || !item.CanOpen => "文件尚未完成或不可读取",
            _ => null };
        return new(id,item?.Name ?? id.ToString(),reason is null,reason);
    }
}

public static class FileBatchRunner
{
    public static async Task<IReadOnlyList<FileBatchEntry>> RunAsync(IEnumerable<Guid> selection,
        Func<Guid,Task<FileBatchCheck>> check, Func<Guid,Task> execute, CancellationToken token = default)
    {
        var ids = selection.Distinct().ToArray();
        if(ids.Length > FileBatchPolicy.MaximumSelection) throw new ArgumentOutOfRangeException(nameof(selection));
        var results = new List<FileBatchEntry>();
        foreach(var id in ids)
        {
            token.ThrowIfCancellationRequested();
            var name = id.ToString();
            try
            {
                var current = await check(id); name=current.Name;
                if(!current.Eligible) { results.Add(new(id,name,FileBatchOutcome.Skipped,current.Reason)); continue; }
                await execute(id);
                results.Add(new(id,name,FileBatchOutcome.Submitted));
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested) { throw; }
            catch(Exception error) { results.Add(new(id,name,FileBatchOutcome.Failed,error.Message)); }
        }
        return results;
    }
}
