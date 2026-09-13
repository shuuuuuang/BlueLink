using System.Threading.Channels;

namespace BlueLink.Usb;

/// <summary>Single consumer, coalesced events, bounded fast retry after a real change.</summary>
internal sealed class MtpProbeSignal(Func<long>? clock = null)
{
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private long _fastUntil;
    private int _retryDelay = 250;
    public void Request(bool fastRetry = true)
    {
        if (fastRetry)
        {
            Interlocked.Exchange(ref _fastUntil, _clock() + 10_000);
            Volatile.Write(ref _retryDelay, 250);
        }
        _wake.Writer.TryWrite(true);
    }
    public void BackOffBusyInterface() => Volatile.Write(ref _retryDelay, 3000);
    internal int DelayMilliseconds(bool enabled, bool ready) => !enabled ? 30_000 :
        !ready && _clock() < Interlocked.Read(ref _fastUntil) ? Volatile.Read(ref _retryDelay) : 3000;
    public async Task WaitAsync(bool enabled, bool ready, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(DelayMilliseconds(enabled, ready));
        try { await _wake.Reader.ReadAsync(timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
    }
}
