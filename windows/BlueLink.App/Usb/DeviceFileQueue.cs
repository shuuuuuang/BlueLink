using System.Collections.Concurrent;

namespace BlueLink.Usb;

/// <summary>FIFO across both directions; a canceled waiter never overtakes or blocks the next file.</summary>
public sealed class DeviceFileQueue
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;
    private int _count;
    public int Count { get { lock (_gate) return _count; } }
    public async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken token)
    {
        Task previous;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) { previous = _tail; _tail = finished.Task; _count++; }
        try
        {
            // Keep the chain intact even when this wait is canceled before its predecessor finishes.
            try { await previous.WaitAsync(token); }
            catch (OperationCanceledException) { _ = CompleteAfterPreviousAsync(); throw; }
            token.ThrowIfCancellationRequested();
            await action(token);
        }
        finally
        {
            if (previous.IsCompleted) finished.TrySetResult();
            lock (_gate) _count--;
        }
        async Task CompleteAfterPreviousAsync()
        {
            await previous;
            finished.TrySetResult();
        }
    }
}
