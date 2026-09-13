namespace BlueLink.Session;

// Preserve submission order while snapshots/hashes complete at different speeds.
internal sealed class FilePreparationLease(SemaphoreSlim gate) : IDisposable
{
    private int _released;
    public static async Task<FilePreparationLease> EnterAsync(SemaphoreSlim gate, CancellationToken token)
    {
        await gate.WaitAsync(token);
        return new(gate);
    }
    public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release(); }
}
