namespace BlueLink.Domain;

internal sealed record DraftSnapshot(string PeerId, string Text, long Revision);

/// <summary>Send completion and disk writes only acknowledge the exact captured edit.</summary>
internal sealed class DraftLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DraftSnapshot> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private long _revision;
    internal void Load(IEnumerable<KeyValuePair<string, string>> stored)
    {
        lock (_gate)
        {
            _values.Clear(); _dirty.Clear();
            foreach (var (peer, text) in stored)
                _values[peer] = new(peer.ToLowerInvariant(), text, ++_revision);
        }
    }
    internal DraftSnapshot Get(string peer)
    {
        lock (_gate) return _values.GetValueOrDefault(peer) ?? new(peer.ToLowerInvariant(), "", 0);
    }
    internal DraftSnapshot Edit(string peer, string text)
    {
        lock (_gate)
        {
            var existing = Get(peer);
            if (existing.Text == text) return existing;
            var value = new DraftSnapshot(peer.ToLowerInvariant(), text, ++_revision);
            _values[peer] = value; _dirty.Add(peer); return value;
        }
    }
    internal bool ClearAfterSend(DraftSnapshot snapshot)
    {
        lock (_gate)
        {
            if (Get(snapshot.PeerId) != snapshot) return false;
            Edit(snapshot.PeerId, ""); return true;
        }
    }
    internal void Acknowledge(DraftSnapshot snapshot)
    {
        lock (_gate) if (Get(snapshot.PeerId) == snapshot) _dirty.Remove(snapshot.PeerId);
    }
    internal IReadOnlyList<DraftSnapshot> Pending()
    {
        lock (_gate) return _dirty.Select(peer => _values[peer]).ToArray();
    }
    internal void Clear(string? peer = null)
    {
        lock (_gate)
        {
            if (peer is not null) Edit(peer, "");
            else foreach (var id in _values.Keys.ToArray()) Edit(id, "");
        }
    }
}
