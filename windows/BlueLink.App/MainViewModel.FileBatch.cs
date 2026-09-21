using BlueLink.Domain;

namespace BlueLink;

public sealed partial class MainViewModel
{
    private bool _fileSelectionMode;
    public bool FileSelectionMode { get => _fileSelectionMode; set { _fileSelectionMode=value; Raise(); } }
    private readonly SemaphoreSlim _fileBatchGate = new(1,1);
    internal FileBatchCheck CheckFileBatch(Guid id, FileBatchAction action)
    {
        var item=AllTransfers.FirstOrDefault(item=>item.Id==id);
        return FileBatchPolicy.Check(id,item,action,item is not null && ResolveSessionId(item) is not null);
    }
    internal async Task<IReadOnlyList<FileBatchEntry>> RunFileBatchAsync(IEnumerable<Guid> ids, FileBatchAction action)
    {
        if(action==FileBatchAction.Copy) throw new ArgumentException("Copy requires one clipboard operation.",nameof(action));
        await _fileBatchGate.WaitAsync();
        try
        {
            return await FileBatchRunner.RunAsync(ids, id=>Task.FromResult(CheckFileBatch(id,action)),async id=>
            {
                var current=CheckFileBatch(id,action);
                if(!current.Eligible) throw new InvalidOperationException(Localization.Strings.Get(current.Reason!));
                var item=AllTransfers.First(value=>value.Id==id);
                switch(action)
                {
                    case FileBatchAction.Retry: await RetryTransferAsync(item); break;
                    case FileBatchAction.Cancel: await _sessions.CancelTransferAsync(ResolveSessionId(item) ?? throw new InvalidOperationException("设备当前未连接"),item.Id); break;
                    case FileBatchAction.DeleteRecords: await DeleteTransferAsync(item); break;
                }
            });
        }
        finally { _fileBatchGate.Release(); }
    }
}
