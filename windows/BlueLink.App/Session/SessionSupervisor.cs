using BlueLink.Bluetooth;
using BlueLink.Domain;
using BlueLink.Protocol;
using BlueLink.Security;
using BlueLink.Transport;

namespace BlueLink.Session;

public sealed record SessionSnapshot(Guid SessionId, string? PeerId, string PeerName,
    string TransportAddress, ConnectionPhase Phase, string Detail, DateTimeOffset StartedAt,
    TransportKind Transport = TransportKind.Bluetooth, PeerPlatform Platform = PeerPlatform.Unknown, string? IdentityHint = null)
{
    public bool HasPeerProvidedName { get; init; }
    public bool UsbFileReady { get; init; }
    public string RoutingKey => PeerId ?? SessionId.ToString("N");
}

/// <summary>
/// Owns all live peer sessions. UI selection never owns a transport, so
/// switching conversations cannot accidentally disconnect a background peer.
/// </summary>
public sealed class SessionSupervisor : IAsyncDisposable
{
    private readonly IdentityStore _identity;
    private readonly Action<TrustRequest> _presentTrust;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Dictionary<string, Guid> _peerIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, Entry> _transferOwners = [];
    private bool _suspended;
    private bool _disposed;

    public Func<IPeerConnection, IdentityAssociationHandler>? IdentityAssociations { get; set; }
    public IReadOnlyList<string> IdentityHints(string peerId)
    {
        lock (_gate) return _entries.Values.Where(value => value.PeerId?.Equals(peerId, StringComparison.OrdinalIgnoreCase) == true &&
            value.Phase == ConnectionPhase.Connected && value.IdentityHint is not null).Select(value => value.IdentityHint!).Distinct().ToArray();
    }
    public string LocalDeviceName { get; set; } = "";
    public string OutgoingDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueLink", "Cache", "Outgoing");
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
    public Func<BlueLink.Transfer.IncomingFileDecision, CancellationToken, Task<string?>>? ReceiveDecision { get; set; }
    private string _duplicateFilePolicy = "rename";
    public string DuplicateFilePolicy
    {
        get => _duplicateFilePolicy;
        set { _duplicateFilePolicy = BlueLink.Transfer.DuplicateFilePolicy.Normalize(value); ForEachSession(session => session.DuplicateFilePolicy = _duplicateFilePolicy); }
    }
    public bool AutoAcceptFiles
    {
        get => _autoAcceptFiles;
        set { _autoAcceptFiles = value; ForEachSession(session => session.AutoAcceptFiles = value); }
    }
    public int ActiveCount { get { lock (_gate) return PublicSnapshots().Length; } }
    public bool CanAccept { get { lock (_gate) return !_suspended && !_disposed; } }

    public event Action<SessionSnapshot>? SessionChanged;
    public event Action<SessionSnapshot, ChatItem>? MessageReceived;
    public event Action<SessionSnapshot, ChatEnvelope, bool>? EnvelopeReceived;
    public event Action<SessionSnapshot, ChatReceipt>? ReceiptReceived;
    public event Action<SessionSnapshot, TransferItem>? TransferChanged;

    public SessionSupervisor(IdentityStore identity, Action<TrustRequest> presentTrust)
    {
        _identity = identity;
        _presentTrust = presentTrust;
    }

    public IReadOnlyList<SessionSnapshot> Snapshot()
    {
        lock (_gate) return PublicSnapshots();
    }

    private SessionSnapshot[] PublicSnapshots() => _entries.Values.Where(value => value.PeerId is null ||
        _peerIndex.GetValueOrDefault(value.PeerId) == value.Id).Select(value => value.Snapshot()).ToArray();

    public bool HasTransport(string peerId, TransportKind transport)
    {
        lock (_gate) return _entries.Values.Any(value => value.PeerId?.Equals(peerId, StringComparison.OrdinalIgnoreCase) == true &&
            value.Transport == transport && value.Phase == ConnectionPhase.Connected && !value.IsClosed);
    }

    public Task<Guid?> AddAsync(IPeerConnection connection, bool listenerRole, string? transportAddress = null, string? expectedTrustedPeerId = null)
    {
        Entry? entry = null;
        lock (_gate)
        {
            if (_suspended || _disposed)
            {
                _ = connection.DisposeAsync();
                return Task.FromResult<Guid?>(null);
            }
            var id = Guid.NewGuid();
            PeerSession? session = null;
            session = new PeerSession(connection, listenerRole, _identity,
                request =>
                {
                    Update(entry!, ConnectionPhase.TrustRequired, "等待用户核对安全代码");
                    request.TransportAddress = entry!.TransportAddress;
                    _presentTrust(request);
                },
                message => MessageReceived?.Invoke(entry!.Snapshot(), message),
                transfer => PublishTransfer(entry!, transfer),
                peerId => Ready(entry!, peerId),
                reason => CloseEntry(entry!, reason),
                (envelope, outgoing) => EnvelopeReceived?.Invoke(entry!.Snapshot(), envelope, outgoing),
                receipt => ReceiptReceived?.Invoke(entry!.Snapshot(), receipt), ReceiveDirectory, MaxReceiveBytes,
                AutoAcceptFiles, expectedTrustedPeerId, IdentityAssociations?.Invoke(connection));
            session.LocalDeviceName = LocalDeviceName;
            session.OutgoingDirectory = OutgoingDirectory;
            session.ReceiveDecision = ReceiveDecision;
            session.DuplicateFilePolicy = DuplicateFilePolicy;
            session.MtpEnabled = UsbEnabled;
            session.MtpAvailabilityChanged = _ => { if (entry is not null) SessionChanged?.Invoke(entry.Snapshot()); };
            entry = new Entry(id, connection.PeerName,
                string.IsNullOrWhiteSpace(transportAddress) ? connection.TransportAddress : transportAddress, session, connection.Transport, connection.Platform, connection.IdentityHint);
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
        FindTransfer(sessionId, transferId)?.Session.CancelTransferAsync(transferId, reason)
        ?? Task.FromException(new InvalidOperationException("会话当前未连接"));

    public Task PauseTransferAsync(Guid sessionId, Guid transferId) =>
        FindTransfer(sessionId, transferId)?.Session.PauseTransferAsync(transferId)
        ?? Task.FromException(new InvalidOperationException("会话当前未连接"));

    public Task ResumeTransferAsync(Guid sessionId, Guid transferId) =>
        FindTransfer(sessionId, transferId)?.Session.ResumeTransferAsync(transferId)
        ?? Task.FromException(new InvalidOperationException("会话当前未连接"));

    public async Task DisconnectAsync(Guid sessionId)
    {
        Entry[] entries;
        lock (_gate) entries = _entries.Values.Where(value => value.RoutingId == sessionId).ToArray();
        foreach (var entry in entries) { CloseEntry(entry, "已断开"); await entry.Session.CloseAsync(); }
        await AwaitStoppedAsync(entries);
    }

    public async Task DisconnectTransportAsync(TransportKind transport)
    {
        Entry[] entries;
        lock (_gate) entries = _entries.Values.Where(value => value.Transport == transport).ToArray();
        foreach (var entry in entries) { CloseEntry(entry, "通道已断开"); await entry.Session.CloseAsync(); }
        await AwaitStoppedAsync(entries);
    }

    private async Task RunAsync(Entry entry)
    {
        try { await entry.Session.RunAsync(); }
        finally { CloseEntry(entry, "连接已关闭"); }
    }

    private void Ready(Entry entry, string peerId)
    {
        SessionSnapshot? merged = null;
        SessionSnapshot? primary = null;
        string? rejection = null;
        lock (_gate)
        {
            if (entry.IsClosed) return;
            var existing = _peerIndex.TryGetValue(peerId, out var currentId) ? _entries.GetValueOrDefault(currentId) : null;
            if (_suspended || _disposed || _identity.FindTrustedKey(peerId) is null)
                rejection = "设备身份或信任关系已变化";
            else if (_entries.Values.Any(value => value != entry && value.PeerId == peerId && value.Transport == entry.Transport && !value.IsClosed))
                rejection = "同一设备已有活动会话";
            else
            {
                if (existing is not null)
                {
                    merged = entry.Snapshot() with { Phase = ConnectionPhase.Disconnected, Detail = "同一设备通道已合并" };
                    entry.RoutingId = existing.RoutingId;
                }
                entry.PeerName = entry.Session.PeerName;
                entry.PeerId = peerId;
                entry.Phase = ConnectionPhase.Connected;
                entry.Detail = entry.Transport == TransportKind.Usb ? "USB · 端到端加密 · BTX/1.1" : "Bluetooth · 端到端加密 · BTX/1.1";
                // Keep the authenticated Bluetooth session warm. Existing transfers stay with their owner.
                if (existing is null || entry.Transport == TransportKind.Usb) _peerIndex[peerId] = entry.Id;
                primary = _entries[_peerIndex[peerId]].Snapshot();
            }
        }
        if (rejection is not null) { CloseEntry(entry, rejection); _ = entry.Session.DisposeAsync(); return; }
        if (merged is not null) SessionChanged?.Invoke(merged);
        if (primary is not null) SessionChanged?.Invoke(primary);
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
        SessionSnapshot? changed = null;
        TransferItem[] interrupted;
        lock (_gate)
        {
            if (entry.IsClosed) return;
            entry.IsClosed = true;
            interrupted = entry.Transfers.Close();
            _entries.Remove(entry.Id);
            foreach (var transfer in _transferOwners.Where(value => value.Value == entry).Select(value => value.Key).ToArray())
                _transferOwners.Remove(transfer);
            entry.Phase = ConnectionPhase.Disconnected;
            entry.Detail = reason;
            if (entry.PeerId is null) changed = entry.Snapshot();
            else if (_peerIndex.GetValueOrDefault(entry.PeerId) == entry.Id)
            {
                var alternate = !_suspended && !_disposed && _identity.FindTrustedKey(entry.PeerId) is not null
                    ? _entries.Values.Where(value => value.PeerId == entry.PeerId && value.Phase == ConnectionPhase.Connected && !value.IsClosed)
                        .OrderByDescending(value => value.Transport == TransportKind.Usb).FirstOrDefault() : null;
                if (alternate is not null)
                {
                    _peerIndex[entry.PeerId] = alternate.Id;
                    changed = alternate.Snapshot();
                }
                else { _peerIndex.Remove(entry.PeerId); changed = entry.Snapshot(); }
            }
        }
        foreach (var transfer in interrupted) TransferChanged?.Invoke(entry.Snapshot(), transfer);
        if (changed is not null) SessionChanged?.Invoke(changed);
    }

    // Gate new sends immediately when settings change, before asynchronous USB disposal finishes.
    private bool _usbEnabled;
    public bool UsbEnabled { get => _usbEnabled; set { _usbEnabled = value; ForEachSession(session => session.MtpEnabled = value); } }

    public void RequestMtpProbe() { if (UsbEnabled) ForEachSession(session => session.RequestMtpProbe()); }

    private Entry? Find(Guid id)
    {
        lock (_gate) return _entries.Values.FirstOrDefault(value => value.RoutingId == id && !value.IsClosed &&
            (UsbEnabled ? value.PeerId is null || _peerIndex.GetValueOrDefault(value.PeerId) == value.Id :
                value.Transport == TransportKind.Bluetooth && value.Phase == ConnectionPhase.Connected));
    }
    private Entry? FindTransfer(Guid id, Guid transferId)
    {
        lock (_gate) return _transferOwners.TryGetValue(transferId, out var owner) && owner.RoutingId == id && !owner.IsClosed ? owner : Find(id);
    }
    private void PublishTransfer(Entry entry, TransferItem transfer)
    {
        TransferItem? snapshot;
        lock (_gate)
        {
            snapshot = entry.Transfers.Record(transfer);
            if (snapshot is null) return;
            if (snapshot.IsActive) _transferOwners[transfer.Id] = entry;
            else _transferOwners.Remove(transfer.Id);
        }
        TransferChanged?.Invoke(entry.Snapshot(), snapshot);
    }

    private void ForEachSession(Action<PeerSession> action)
    {
        PeerSession[] sessions;
        lock (_gate) sessions = _entries.Values.Select(value => value.Session).ToArray();
        foreach (var session in sessions) action(session);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) _disposed = true;
        await SuspendAsync();
    }

    public async Task SuspendAsync()
    {
        Entry[] sessions;
        lock (_gate) { _suspended = true; sessions = _entries.Values.ToArray(); }
        foreach (var entry in sessions)
        {
            CloseEntry(entry, "应用已关闭");
            try { await entry.Session.CloseAsync(); } catch { }
        }
        await AwaitStoppedAsync(sessions);
    }

    private static async Task AwaitStoppedAsync(IEnumerable<Entry> entries)
    {
        foreach (var entry in entries)
            if (entry.RunTask is { } task)
                try { await task.ConfigureAwait(false); } catch { /* PeerSession reports the terminal failure. */ }
    }

    public void Resume() { lock (_gate) { if (!_disposed) _suspended = false; } }

    private sealed class Entry(Guid id, string peerName, string transportAddress, PeerSession session, TransportKind transport, PeerPlatform platform, string? identityHint)
    {
        public Guid Id { get; } = id;
        public Guid RoutingId { get; set; } = id;
        public TransportKind Transport { get; } = transport;
        public PeerPlatform Platform { get; } = platform;
        public string? PeerId { get; set; }
        public string PeerName { get; set; } = peerName;
        public string TransportAddress { get; } = transportAddress;
        public PeerSession Session { get; } = session;
        public ConnectionPhase Phase { get; set; } = ConnectionPhase.Connecting;
        public string Detail { get; set; } = "正在建立会话";
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public Task? RunTask { get; set; }
        public bool IsClosed { get; set; }
        public SessionTransferLedger Transfers { get; } = new();
        public string? IdentityHint { get; } = identityHint;
        public SessionSnapshot Snapshot() => new(RoutingId, PeerId, PeerName, TransportAddress, Phase, Detail, StartedAt, Transport, Platform, IdentityHint) { HasPeerProvidedName = Session.HasPeerProvidedName, UsbFileReady = Session.MtpReady };
    }
}
