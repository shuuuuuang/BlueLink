using BlueLink.Domain;

namespace BlueLink.Session;

/// <summary>Local and remote pause requests remain independent while late block acknowledgements arrive.</summary>
internal sealed class TransferPauseController(TransferItem item, Action<TransferItem> notify)
{
    private readonly object _gate = new();
    private bool _localPaused, _remotePaused;
    private Exception? _failure;
    private TaskCompletionSource _resumed = Ready();
    public TransferItem Item { get; } = item;
    public bool SetPaused(bool local, bool paused)
    {
        lock (_gate)
        {
            if (_failure is not null || !Pausable(Item.Status)) return false;
            if ((local ? _localPaused : _remotePaused) == paused) return false;
            if (local) _localPaused = paused; else _remotePaused = paused;
            if (_localPaused || _remotePaused)
            {
                if (_resumed.Task.IsCompleted) _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else _resumed.TrySetResult();
            Publish(TransferStatus.Resuming);
            return true;
        }
    }
    public void Report(TransferStatus status, long? completedBytes = null)
    {
        lock (_gate)
        {
            if (Item.Status is TransferStatus.Completed or TransferStatus.Canceled or TransferStatus.Failed or TransferStatus.Rejected) return;
            if (!Pausable(Item.Status) && Pausable(status)) return;
            if (completedBytes is { } bytes) Item.CompletedBytes = bytes;
            Publish(status);
        }
    }
    private void Publish(TransferStatus status)
    {
        Item.Status = !Pausable(status) ? status : _localPaused ? TransferStatus.Paused :
            _remotePaused ? TransferStatus.RemotePaused : status;
        notify(Item);
    }
    public async Task WaitAsync(CancellationToken token)
    {
        while (true)
        {
            Task signal;
            lock (_gate)
            {
                if (_failure is { } failure) throw failure;
                if (!_localPaused && !_remotePaused) return;
                signal = _resumed.Task;
            }
            await signal.WaitAsync(token).ConfigureAwait(false);
        }
    }
    public void Fail(Exception failure)
    {
        lock (_gate) { _failure = failure; _resumed.TrySetException(failure); }
    }
    private static bool Pausable(TransferStatus status) => status is TransferStatus.Offered or TransferStatus.Queued or
        TransferStatus.Transferring or TransferStatus.Paused or TransferStatus.RemotePaused or TransferStatus.Resuming;
    private static TaskCompletionSource Ready()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult(); return signal;
    }
}
