namespace BlueLink.Session;

// Serializes the last possible route change with opening the transfer stream.
internal sealed class QueuedRouteGate
{
    private readonly object _gate = new();
    private int _state; // waiting, switching, started, stopped
    private TaskCompletionSource<bool>? _decision;
    public Task<bool>? Request()
    {
        lock (_gate)
        {
            if (_state != 0) return null;
            _state = 1;
            return (_decision = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }
    public void Resolve(bool accepted)
    {
        lock (_gate)
        {
            if (_state != 1) return;
            _state = accepted ? 3 : 0;
            _decision!.TrySetResult(accepted);
        }
    }
    public async Task StartAsync(CancellationToken token)
    {
        while (true)
        {
            Task pending;
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (_state == 0) { _state = 2; return; }
                if (_state != 1) throw new OperationCanceledException("USB 队列已结束", token);
                pending = _decision!.Task;
            }
            await pending.WaitAsync(token);
        }
    }
}
