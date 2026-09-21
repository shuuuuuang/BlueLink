using BlueLink.Domain;

namespace BlueLink.Session;

// Authenticated record stream IDs identify attempts. Task/message IDs remain stable for resume/history.
internal sealed class TransferStreamFence
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Binding> _current = [];
    private readonly HashSet<int> _used = [];
    private readonly AsyncLocal<Binding?> _context = new();
    private int _next = 16;
    internal sealed class Binding(Guid task, int stream, bool outgoing)
    {
        public Guid Task { get; } = task;
        public int Stream { get; } = stream;
        public bool Outgoing { get; } = outgoing;
        public bool Terminal;
        public Guid Attempt { get; } = Guid.NewGuid();
        public long Sequence { get; } = TransferAttemptRegistry.NextSequence();
    }
    public IDisposable StartOutgoing(Guid task, bool enabled, bool listener)
    {
        lock (_gate)
        {
            if (!enabled && _current.ContainsKey(task))
                throw new IOException("对端版本不支持安全重试，请重新连接后重试。");
            if (_current.Count >= 8192 || _used.Count >= 16384) throw new IOException("传输会话已达到容量限制，请重新连接。");
            var stream = enabled ? checked(_next += 2) + (listener ? 0 : 1) : 2;
            var binding = new Binding(task, stream, true);
            _current[task] = binding;
            if (enabled) _used.Add(stream);
            return Enter(binding);
        }
    }
    public IDisposable? Receive(Guid task, int stream, bool starts, bool busy, bool enabled, bool listener)
    {
        lock (_gate)
        {
            if (_current.TryGetValue(task, out var binding) && (!enabled || binding.Stream == stream))
            {
                if (starts && (binding.Outgoing || binding.Terminal)) return null;
                return Enter(binding);
            }
            if (!starts || busy || _current.Count >= 8192 || _used.Count >= 16384) return null;
            if (enabled && (stream < 16 || (stream & 1) == (listener ? 0 : 1) || !_used.Add(stream))) return null;
            binding = new Binding(task, stream, false);
            _current[task] = binding;
            return Enter(binding);
        }
    }
    public IDisposable Capture(Guid task)
    {
        lock (_gate)
        {
            var captured = _context.Value;
            if (captured?.Task == task) return Enter(captured);
            return _current.TryGetValue(task, out var binding) ? Enter(binding) : new Scope(() => { });
        }
    }
    public int StreamFor(Guid task, int fallback, bool enabled)
    {
        if (!enabled) return fallback;
        lock (_gate)
        {
            var binding = _context.Value is { } captured && captured.Task == task ? captured : _current.GetValueOrDefault(task);
            return binding?.Stream ?? throw new IOException("Transfer has no authenticated attempt stream.");
        }
    }
    public TransferItem Stamp(TransferItem item)
    {
        lock (_gate)
        {
            var binding = _context.Value is { } captured && captured.Task == item.Id ? captured : _current.GetValueOrDefault(item.Id);
            if (binding is null) return item;
            if (!item.IsActive) binding.Terminal = true;
            if (binding.Outgoing) return item;
            var copy = item.Snapshot();
            copy.AttemptId = binding.Attempt; copy.AttemptSequence = binding.Sequence;
            return copy;
        }
    }
    private IDisposable Enter(Binding binding)
    {
        var previous = _context.Value; _context.Value = binding;
        return new Scope(() => _context.Value = previous);
    }
    private sealed class Scope(Action restore) : IDisposable { public void Dispose() => restore(); }
}
