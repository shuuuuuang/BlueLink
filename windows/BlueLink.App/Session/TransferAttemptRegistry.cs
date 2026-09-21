using BlueLink.Domain;

namespace BlueLink.Session;

// Shared by a supervisor, including sessions that are still draining after disconnect.
internal sealed class TransferAttemptRegistry
{
    private static long _nextSequence;
    internal static long NextSequence() => Interlocked.Increment(ref _nextSequence);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Lease> _owners = [];
    private bool _closing;
    public Task CloseAndDrain() { lock (_gate) { _closing=true; return Task.WhenAll(_owners.Values.Select(x=>x.Released.Task)); } }
    public Task WhenReleased(Guid taskId) { lock (_gate) return _owners.GetValueOrDefault(taskId)?.Released.Task ?? Task.CompletedTask; }
    public Lease Begin(Guid taskId)
    {
        lock (_gate)
        {
            if (_closing) throw new InvalidOperationException("Transfer service is stopping.");
            if (_owners.ContainsKey(taskId))
                throw new InvalidOperationException(Localization.Strings.Get("上次传输仍在结束，请稍后重试"));
            var lease = new Lease(this, taskId);
            _owners.Add(taskId, lease);
            return lease;
        }
    }
    internal sealed class Lease(TransferAttemptRegistry registry, Guid taskId) : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public long Sequence { get; } = NextSequence();
        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _released, _terminal;
        public void Publish(TransferItem value, Action<TransferItem> notify)
        {
            TransferItem snapshot;
            lock (registry._gate)
            {
                if (_released || _terminal) return;
                if (value.Id != taskId) throw new InvalidOperationException("Attempt/task identity mismatch");
                value.AttemptId = Id;
                value.AttemptSequence = Sequence;
                snapshot = value.Snapshot();
                _terminal = !snapshot.IsActive;
            }
            // UI dispatch must never run under the ownership lock. The session ledger
            // also checks attempt IDs, so a delayed dispatch cannot replace a new attempt.
            notify(snapshot);
        }
        public void Dispose()
        {
            lock (registry._gate)
            {
                if (_released) return;
                _released = true;
                if (registry._owners.GetValueOrDefault(taskId) == this) registry._owners.Remove(taskId);
                Released.TrySetResult();
            }
        }
    }
}
