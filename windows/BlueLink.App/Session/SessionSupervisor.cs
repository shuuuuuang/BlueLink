using BlueLink.Bluetooth;
using BlueLink.Domain;
using BlueLink.Protocol;
using BlueLink.Security;

namespace BlueLink.Session;

public sealed record SessionSnapshot(Guid SessionId, string? PeerId, string PeerName,
    string TransportAddress, ConnectionPhase Phase, string Detail, DateTimeOffset StartedAt)
{
    public string RoutingKey => PeerId ?? SessionId.ToString("N");
}

/// <summary>
/// Owns all live peer sessions. UI selection never owns a transport, so
/// switching conversations cannot accidentally disconnect a background peer.
/// </summary>
public sealed class SessionSupervisor : IAsyncDisposable
{
    private readonly IdentityStore _identity;
    private readonly Func<string, string, string, Task<bool>> _confirmTrust;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Dictionary<string, Guid> _peerIndex = new(StringComparer.OrdinalIgnoreCase);

    public int MaxConcurrentSessions { get; set; } = 4;
    private string _receiveDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueLink", "Received");
    private long _maxReceiveBytes = 500L * 1024 * 1024;
    private bool _autoAcceptFiles = true;
    public string ReceiveDirectory
    {
        get => _receiveDirectory;
        set { _receiveDirectory = value; ForEachSession(session => session.ReceiveDirectory = value); }
    }
    public long MaxReceiveBytes
    {
        get => _maxReceiveBytes;
        set { _maxReceiveBytes = value; ForEachSession(session => session.MaxReceiveBytes = value); }
    }
    public bool AutoAcceptFiles
    {
        get => _autoAcceptFiles;
        set { _autoAcceptFiles = value; ForEachSession(session => session.AutoAcceptFiles = value); }
    }
    public int ActiveCount { get { lock (_gate) return _entries.Count; } }
    public bool CanAccept => ActiveCount < Math.Clamp(MaxConcurrentSessions, 1, 8);

    public event Action<SessionSnapshot>? SessionChanged;
    public event Action<SessionSnapshot, ChatItem>? MessageReceived;
    public event Action<SessionSnapshot, ChatEnvelope, bool>? EnvelopeReceived;
    public event Action<SessionSnapshot, ChatReceipt>? ReceiptReceived;
    public event Action<SessionSnapshot, TransferItem>? TransferChanged;

    public SessionSupervisor(IdentityStore identity, Func<string, string, string, Task<bool>> confirmTrust)
    {
        _identity = identity;
        _confirmTrust = confirmTrust;
    }

    public IReadOnlyList<SessionSnapshot> Snapshot()
    {
        lock (_gate) return _entries.Values.Select(value => value.Snapshot()).ToArray();
    }

    public Task<Guid?> AddAsync(RfcommConnection connection, bool listenerRole, string? transportAddress = null)
    {
        Entry? entry = null;
        lock (_gate)
        {
            if (_entries.Count >= Math.Clamp(MaxConcurrentSessions, 1, 8))
            {
                _ = connection.DisposeAsync();
                return Task.FromResult<Guid?>(null);
            }
            var id = Guid.NewGuid();
            PeerSession? session = null;
            session = new PeerSession(connection, listenerRole, _identity,
                async (name, code, remoteFingerprint) =>
                {
                    Update(entry!, ConnectionPhase.TrustRequired, "等待用户核对安全代码");
                    return await _confirmTrust(name, code, remoteFingerprint);
                },
                message => MessageReceived?.Invoke(entry!.Snapshot(), message),
                transfer => TransferChanged?.Invoke(entry!.Snapshot(), transfer),
                peerId => Ready(entry!, peerId),
                reason => CloseEntry(entry!, reason),
                (envelope, outgoing) => EnvelopeReceived?.Invoke(entry!.Snapshot(), envelope, outgoing),
                receipt => ReceiptReceived?.Invoke(entry!.Snapshot(), receipt), ReceiveDirectory, MaxReceiveBytes,
                AutoAcceptFiles);
            entry = new Entry(id, connection.PeerName,
                string.IsNullOrWhiteSpace(transportAddress) ? connection.TransportAddress : transportAddress, session);
            _entries.Add(id, entry);
        }
        Update(entry, ConnectionPhase.SecureHandshake, "正在验证设备身份");
        entry.RunTask = RunAsync(entry);
        return Task.FromResult<Guid?>(entry.Id);
    }

    public Task SendChatAsync(Guid sessionId, string text, Guid? messageId = null) => Find(sessionId)?.Session.SendChatAsync(text, messageId)
        ?? Task.FromException(new InvalidOperationException("会话当前未连接"));

    public Task SendFileAsync(Guid sessionId, string path) => Find(sessionId)?.Session.SendFileAsync(path)
        ?? Task.FromException(new InvalidOperationException("会话当前未连接"));

    public Task RetryFileAsync(Guid sessionId, string path, TransferItem transfer) =>
        Find(sessionId)?.Session.RetryFileAsync(path, transfer)
        ?? Task.FromException(new InvalidOperationException("会话当前未连接"));

    public Task CancelTransferAsync(Guid sessionId, Guid transferId, string reason = "用户取消") =>
        Find(sessionId)?.Session.CancelTransferAsync(transferId, reason)
        ?? Task.FromException(new InvalidOperationException("会话当前未连接"));

    public Task PauseTransferAsync(Guid sessionId, Guid transferId) =>
        Find(sessionId)?.Session.PauseTransferAsync(transferId)
        ?? Task.FromException(new InvalidOperationException("会话当前未连接"));

    public Task ResumeTransferAsync(Guid sessionId, Guid transferId) =>
        Find(sessionId)?.Session.ResumeTransferAsync(transferId)
        ?? Task.FromException(new InvalidOperationException("会话当前未连接"));

    public async Task DisconnectAsync(Guid sessionId)
    {
        var entry = Find(sessionId);
        if (entry is null) return;
        await entry.Session.DisposeAsync();
        CloseEntry(entry, "已断开");
    }

    private async Task RunAsync(Entry entry)
    {
        try { await entry.Session.RunAsync(); }
        finally { CloseEntry(entry, "连接已关闭"); }
    }

    private void Ready(Entry entry, string peerId)
    {
        Entry? existing = null;
        lock (_gate)
        {
            if (entry.IsClosed) return;
            if (_peerIndex.TryGetValue(peerId, out var existingId) && existingId != entry.Id &&
                _entries.TryGetValue(existingId, out existing))
            {
                // Deterministic duplicate resolution: the older established session wins.
            }
            else
            {
                entry.PeerId = peerId;
                entry.Phase = ConnectionPhase.Connected;
                entry.Detail = "端到端加密 · BTX/1.1";
                _peerIndex[peerId] = entry.Id;
            }
        }
        if (existing is not null)
        {
            _ = entry.Session.DisposeAsync();
            CloseEntry(entry, "同一设备已有活动会话");
            return;
        }
        SessionChanged?.Invoke(entry.Snapshot());
    }

    private void Update(Entry entry, ConnectionPhase phase, string detail)
    {
        lock (_gate)
        {
            if (entry.IsClosed) return;
            entry.Phase = phase;
            entry.Detail = detail;
        }
        SessionChanged?.Invoke(entry.Snapshot());
    }

    private void CloseEntry(Entry entry, string reason)
    {
        lock (_gate)
        {
            if (entry.IsClosed) return;
            entry.IsClosed = true;
            _entries.Remove(entry.Id);
            if (entry.PeerId is not null && _peerIndex.TryGetValue(entry.PeerId, out var indexed) && indexed == entry.Id)
                _peerIndex.Remove(entry.PeerId);
            entry.Phase = ConnectionPhase.Disconnected;
            entry.Detail = reason;
        }
        SessionChanged?.Invoke(entry.Snapshot());
    }

    private Entry? Find(Guid id) { lock (_gate) return _entries.GetValueOrDefault(id); }
    private void ForEachSession(Action<PeerSession> action)
    {
        PeerSession[] sessions;
        lock (_gate) sessions = _entries.Values.Select(value => value.Session).ToArray();
        foreach (var session in sessions) action(session);
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] sessions;
        lock (_gate) sessions = _entries.Values.ToArray();
        foreach (var entry in sessions)
        {
            try { await entry.Session.DisposeAsync(); } catch { }
            CloseEntry(entry, "应用已关闭");
        }
    }

    private sealed class Entry(Guid id, string peerName, string transportAddress, PeerSession session)
    {
        public Guid Id { get; } = id;
        public string? PeerId { get; set; }
        public string PeerName { get; } = peerName;
        public string TransportAddress { get; } = transportAddress;
        public PeerSession Session { get; } = session;
        public ConnectionPhase Phase { get; set; } = ConnectionPhase.Connecting;
        public string Detail { get; set; } = "正在建立会话";
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public Task? RunTask { get; set; }
        public bool IsClosed { get; set; }
        public SessionSnapshot Snapshot() => new(Id, PeerId, PeerName, TransportAddress, Phase, Detail, StartedAt);
    }
}
