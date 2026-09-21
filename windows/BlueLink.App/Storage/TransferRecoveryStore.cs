using System.Text.Json;
using BlueLink.Domain;
using BlueLink.Protocol;

namespace BlueLink.Storage;

/// <summary>Operational records, independent of history preferences. Never starts a transfer.</summary>
public sealed class TransferRecoveryStore
{
    private readonly string _root;
    private readonly Guid _epoch = Guid.NewGuid();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Record> _records = [];
    private readonly Action<string, byte[]> _write;
    public TransferRecoveryStore(string root, Action<string, byte[]>? write = null)
    {
        _root = root; _write = write ?? AtomicWrite;
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            var row = JsonSerializer.Deserialize<Record>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Invalid recovery record.");
            if (row.Version != 1 || Path.GetFileNameWithoutExtension(path) != row.Task.Id.ToString("N"))
                throw new InvalidDataException("Unsupported recovery record.");
            _records.Add(row.Task.Id, row);
        }
    }

    // The caller serializes publication with its session ledger. Attempt sequence is scoped to this process.
    public bool RecordTransfer(Guid owner, DateTimeOffset ownerStarted, string transport, TransferItem item)
    {
        if (item.Role == AttachmentRole.ImagePreview || string.IsNullOrWhiteSpace(item.PeerId)) return true;
        lock (_gate)
        {
            _records.TryGetValue(item.Id, out var old);
            if (old is not null)
            {
                var incomingOffer = !item.Outgoing && item.AttemptId is null && item.Status == TransferStatus.Offered;
                var newAttempt = Active(item.Status) && (old.Epoch != _epoch || incomingOffer ||
                    item.AttemptId != old.Task.AttemptId && item.AttemptSequence > old.Task.AttemptSequence);
                if (old.Dismissed && !newAttempt || !string.Equals(old.Task.PeerId, item.PeerId, StringComparison.OrdinalIgnoreCase) || old.Task.Outgoing != item.Outgoing) return false;
                if (old.Epoch == _epoch)
                {
                    if (old.Owner != owner && ownerStarted <= old.OwnerStarted) return false;
                    if (old.Task.AttemptId == item.AttemptId && old.Owner == owner)
                    {
                        if (!Active(old.Task.Status) && !incomingOffer) return false;
                    }
                    else if ((item.Outgoing || item.AttemptId is not null || old.Task.AttemptId is not null) && item.AttemptSequence <= old.Task.AttemptSequence) return false;
                }
                else if (!Active(item.Status)) return false; // A new process must explicitly begin again.
            }
            var now = DateTimeOffset.UtcNow;
            var row = new Record(1, _epoch, owner, ownerStarted, transport, TaskData.From(item), false, now);
            // Persist phase boundaries and new attempts immediately; byte counts are display samples, not checkpoints.
            if (old is not null && old.Epoch == _epoch && old.Owner == owner && old.Task.AttemptId == item.AttemptId &&
                old.Task.Status == item.Status && now - old.WrittenAt < TimeSpan.FromSeconds(1)) return true;
            _write(PathFor(item.Id), JsonSerializer.SerializeToUtf8Bytes(row));
            _records[item.Id] = row;
            return true;
        }
    }

    // Called only with the identity journal created by explicit safety-code association.
    // Each replacement is independently replayable if SQL or a later replacement fails.
    public void Associate(IReadOnlyDictionary<string, string> associations)
    {
        lock (_gate)
        {
            var aliases = new Dictionary<string, string>(associations, StringComparer.OrdinalIgnoreCase);
            var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in aliases.Keys)
            {
                var target = source;
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (aliases.TryGetValue(target, out var next))
                {
                    if (!visited.Add(target) || string.IsNullOrWhiteSpace(next))
                        throw new InvalidDataException("Invalid recovery identity association.");
                    target = next;
                }
                resolved[source] = target;
            }
            var affected = _records.Values.Where(x => x.Task.PeerId is { } peer && resolved.ContainsKey(peer)).ToArray();
            if (affected.Any(x => x.Epoch == _epoch && Active(x.Task.Status)))
                throw new InvalidOperationException("设备仍有活动传输，请等待传输结束后关联身份。");
            foreach (var old in affected)
            {
                var row = old with { Task = old.Task with { PeerId = resolved[old.Task.PeerId!] } };
                _write(PathFor(row.Task.Id), JsonSerializer.SerializeToUtf8Bytes(row));
                _records[row.Task.Id] = row;
            }
        }
    }

    public bool ReferencesTemporary(Guid id)
    {
        lock (_gate) return _records.TryGetValue(id, out var row) && !row.Dismissed &&
            (Active(row.Task.Status) || row.Task.Status == TransferStatus.Failed);
    }

    public bool Dismiss(Guid id)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var old)) return true;
            if (old.Epoch == _epoch && Active(old.Task.Status)) return false;
            var row = old with { Dismissed = true };
            _write(PathFor(id), JsonSerializer.SerializeToUtf8Bytes(row));
            _records[id] = row;
            return true;
        }
    }

    public List<TransferItem> MergeHistory(IEnumerable<TransferItem> history, string? peer = null)
    {
        lock (_gate)
        {
            var result = history.ToDictionary(x => x.Id);
            foreach (var row in _records.Values)
            {
                if (peer is not null && !string.Equals(peer, row.Task.PeerId, StringComparison.OrdinalIgnoreCase)) continue;
                if (row.Dismissed) { result.Remove(row.Task.Id); continue; }
                var unfinished = Active(row.Task.Status) || row.Task.Status == TransferStatus.Failed;
                if (!unfinished && !result.ContainsKey(row.Task.Id)) continue;
                var value = row.Task.ToItem();
                if (row.Epoch != _epoch && unfinished)
                {
                    value.Status = TransferStatus.Failed;
                    value.RecoveryPending = true;
                    value.AttemptId = null; value.AttemptSequence = 0;
                    value.FailureDetail = value.Outgoing ? "应用中断后保留的任务，请确认设备连接后手动重试" : "应用中断后保留的接收任务，等待发送方重试";
                }
                result[value.Id] = value;
            }
            return result.Values.OrderByDescending(x => x.CreatedAt).ToList();
        }
    }

    private string PathFor(Guid id) => Path.Combine(_root, id.ToString("N") + ".json");
    private static bool Active(TransferStatus status) => status is TransferStatus.Offered or TransferStatus.Queued or TransferStatus.Transferring
        or TransferStatus.Paused or TransferStatus.RemotePaused or TransferStatus.Resuming or TransferStatus.Verifying or TransferStatus.Committing;
    private static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".new";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { stream.Write(bytes); stream.Flush(true); }
        File.Move(temporary, path, overwrite: true);
    }
    public sealed record Record(int Version, Guid Epoch, Guid Owner, DateTimeOffset OwnerStarted, string Transport,
        TaskData Task, bool Dismissed, DateTimeOffset WrittenAt);
    public sealed record TaskData(Guid Id, string Name, long TotalBytes, long CompletedBytes, bool Outgoing, TransferStatus Status,
        string? PeerId, string PeerName, Guid? MessageId, Guid? AttachmentId, string MimeType, string? LocalPath,
        string? SourceSha256, Guid? AttemptId, long AttemptSequence, AttachmentRole Role, DateTimeOffset CreatedAt, string? FailureDetail)
    {
        public static TaskData From(TransferItem x) => new(x.Id,x.Name,x.TotalBytes,x.CompletedBytes,x.Outgoing,x.Status,x.PeerId,x.PeerName,
            x.MessageId,x.AttachmentId,x.MimeType,x.LocalPath,x.SourceSha256,x.AttemptId,x.AttemptSequence,x.Role,x.CreatedAt,x.FailureDetail);
        public TransferItem ToItem() => new() { Id=Id, Name=Name, TotalBytes=TotalBytes, CompletedBytes=CompletedBytes, Outgoing=Outgoing,
            Status=Status, PeerId=PeerId, PeerName=PeerName, MessageId=MessageId, AttachmentId=AttachmentId, MimeType=MimeType,
            LocalPath=LocalPath, SourceSha256=SourceSha256, AttemptId=AttemptId, AttemptSequence=AttemptSequence, Role=Role,
            CreatedAt=CreatedAt, FailureDetail=FailureDetail };
    }
}
