namespace BlueLink.Session;

/// <summary>Only authenticated inbound records renew a USB peer's monotonic lease.</summary>
internal sealed class UsbSessionLiveness(Func<long>? clock = null)
{
    internal const int PingIntervalMs = 3000;
    internal const int TimeoutMs = 12000;
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private long _received = (clock ?? (() => Environment.TickCount64))();
    public void Received() => Interlocked.Exchange(ref _received, _clock());
    public bool Expired => _clock() - Interlocked.Read(ref _received) >= TimeoutMs;

    public async Task RunAsync(Func<Task> ping, Action<string> lost, CancellationToken token)
    {
        Task? pending = null;
        try
        {
            while (true)
            {
                await Task.Delay(PingIntervalMs, token).ConfigureAwait(false);
                if (Expired) { lost("USB 对端已无响应，连接已断开。"); return; }
                // A stalled write must not stall the independent receive deadline or grow the queue.
                if (pending is { IsCompleted: false }) continue;
                if (pending is not null) await pending.ConfigureAwait(false);
                pending = ping();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (!token.IsCancellationRequested) lost("USB 心跳发送失败，连接已断开。"); }
        finally { if (pending is not null) _ = pending.ContinueWith(t => _ = t.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); }
    }
}
