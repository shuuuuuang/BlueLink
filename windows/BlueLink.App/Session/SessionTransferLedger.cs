using BlueLink.Domain;

namespace BlueLink.Session;

// One ledger per physical session. A reconnect creates a new owner; late reports
// from the old owner cannot revive its paused tasks or overwrite a retry.
internal sealed class SessionTransferLedger
{
    private readonly Dictionary<Guid, TransferItem> _active = [];
    private bool _closed;
    private readonly Dictionary<Guid, TransferItem> _attempts = [];
    private readonly HashSet<Guid> _retiredAttempts = [];
    public TransferItem? Record(TransferItem value)
    {
        if (_closed) return null;
        if (value.AttemptId is { } attemptId)
        {
            if (_retiredAttempts.Contains(attemptId)) return null;
            if (_attempts.TryGetValue(value.Id, out var previous))
            {
                if (previous.AttemptId == attemptId && !previous.IsActive) return null;
                if (previous.AttemptId != attemptId)
                {
                    if (value.AttemptSequence <= previous.AttemptSequence) return null;
                    if (previous.Status == TransferStatus.Completed || value.Status is not (TransferStatus.Queued or TransferStatus.Offered)) return null;
                    if (previous.AttemptId is { } retired) _retiredAttempts.Add(retired);
                }
            }
            _attempts[value.Id] = Copy(value);
        }
        var snapshot = Copy(value);
        if (value.IsActive) _active[value.Id] = snapshot; else _active.Remove(value.Id);
        return snapshot;
    }
    public TransferItem[] Close()
    {
        if (_closed) return [];
        _closed = true;
        var values = _active.Values.ToArray();
        _active.Clear();
        foreach (var value in values)
        {
            value.Status = TransferStatus.Failed;
            value.FailureDetail = "设备通道已断开，请由发送方重试传输";
        }
        return values;
    }
    public static TransferItem Copy(TransferItem value) => value.Snapshot();
}
