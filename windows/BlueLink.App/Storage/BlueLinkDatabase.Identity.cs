using BlueLink.Security;

namespace BlueLink.Storage;

public sealed partial class BlueLinkDatabase
{
    public Func<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>>? AssociateComposerDrafts { get; set; }
    private static void InitializeIdentityHints(NativeSqliteConnection connection)
    {
        connection.Execute("CREATE TABLE IF NOT EXISTS peer_hint(peer_id TEXT NOT NULL REFERENCES peer(peer_id) ON DELETE CASCADE, hint TEXT NOT NULL, PRIMARY KEY(peer_id,hint));");
        using var peers = connection.Prepare("SELECT peer_id, transport_address FROM peer WHERE trust_state != 'Retired'");
        while (peers.Read())
        {
            if (PeerIdentityHint.Bluetooth(peers.GetString(1)) is not { } hint) continue;
            using var insert = connection.Prepare("INSERT OR IGNORE INTO peer_hint(peer_id,hint) VALUES(?,?)");
            insert.Bind(1, peers.GetString(0)).Bind(2, hint).ExecuteNonQuery();
        }
    }

    public Task RecordIdentityHintAsync(string peerId, string? hint, CancellationToken token = default) => Run(() =>
    {
        if (string.IsNullOrEmpty(hint)) return;
        using var connection = Open();
        using var insert = connection.Prepare("INSERT OR IGNORE INTO peer_hint(peer_id,hint) SELECT peer_id,? FROM peer WHERE peer_id=? AND trust_state='Trusted'");
        insert.Bind(1, hint).Bind(2, peerId).ExecuteNonQuery();
    }, token);

    public async Task<IdentityCandidate?> FindIdentityCandidateAsync(string peerId, string? hint, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(hint)) return null;
        var peers = await LoadPeersAsync(token);
        if (peers.Any(peer => peer.PeerId.Equals(peerId, StringComparison.OrdinalIgnoreCase))) return null;
        var ids = await Run(() =>
        {
            using var connection = Open();
            using var query = connection.Prepare("SELECT peer_id FROM peer_hint WHERE hint=?");
            query.Bind(1, hint);
            var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (query.Read()) matches.Add(query.GetString(0));
            return matches;
        }, token);
        var candidates = peers.Where(peer => peer.TrustState != StoredTrustState.Blocked &&
            (ids.Contains(peer.PeerId) || PeerIdentityHint.Bluetooth(peer.TransportAddress) == hint)).ToArray();
        return candidates.Length == 1 ? new(candidates[0].PeerId, candidates[0].DisplayName,
            candidates[0].IdentityPublicKey, hint) : null;
    }

    public Task ApplyIdentityAssociationsAsync(IdentityStore identity, CancellationToken token = default) => Run(() =>
    {
        using var connection = Open();
        ApplyIdentityAssociations(connection, identity, AssociateComposerDrafts);
    }, token);

    private static void ApplyIdentityAssociations(NativeSqliteConnection connection, IdentityStore identity,
        Func<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>>? associateDrafts = null)
    {
        var associations = identity.IdentityAssociations;
        InTransaction(connection, () =>
        {
            IReadOnlyDictionary<string, string>? merged = null;
            if (associations.Count > 0 && associateDrafts is not null)
            {
                var legacy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using (var query = connection.Prepare("SELECT peer_id,draft FROM conversation"))
                    while (query.Read()) legacy[query.GetString(0)] = query.GetString(1);
                merged = associateDrafts(legacy);
            }
            foreach (var (oldId, initialTarget) in associations)
            {
                var target = initialTarget;
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { oldId };
                while (associations.TryGetValue(target, out var next))
                {
                    if (!visited.Add(target)) throw new InvalidDataException("设备身份关联存在循环。");
                    target = next;
                }
                MovePeerHistory(connection, oldId, target, identity.FindTrustedKey(target));
            }
            if (merged is not null) foreach (var (peer, text) in merged)
            {
                using var update = connection.Prepare("UPDATE conversation SET draft=? WHERE peer_id=?");
                update.Bind(1, text).Bind(2, peer).ExecuteNonQuery();
            }
        });
    }

    private static void MovePeerHistory(NativeSqliteConnection db, string oldId, string newId, byte[]? newKey)
    {
        using (var old = db.Prepare("SELECT trust_state FROM peer WHERE peer_id=?"))
        {
            old.Bind(1, oldId);
            if (!old.Read() || old.GetString(0) == "Retired") return;
        }
        using (var peer = db.Prepare("""
            INSERT INTO peer(peer_id,identity_public_key,display_name,platform,trust_state,created_at,last_seen_at,last_connected_at,transport_address,usb_transport_address)
            SELECT ?,?,display_name,platform,?,created_at,last_seen_at,last_connected_at,transport_address,usb_transport_address FROM peer WHERE peer_id=?
            ON CONFLICT(peer_id) DO UPDATE SET display_name=excluded.display_name,
              created_at=MIN(peer.created_at,excluded.created_at), identity_public_key=excluded.identity_public_key, trust_state=excluded.trust_state,
              transport_address=CASE WHEN peer.transport_address='' THEN excluded.transport_address ELSE peer.transport_address END,
              usb_transport_address=CASE WHEN peer.usb_transport_address='' THEN excluded.usb_transport_address ELSE peer.usb_transport_address END
            """)) peer.Bind(1, newId).Bind(2, newKey).Bind(3, newKey is null ? "Unknown" : "Trusted").Bind(4, oldId).ExecuteNonQuery();
        var conversationId = "peer:" + newId.ToLowerInvariant();
        using (var conversation = db.Prepare("""
            INSERT INTO conversation(conversation_id,peer_id,last_activity_at,unread_count,draft)
            SELECT ?,?,last_activity_at,unread_count,draft FROM conversation WHERE peer_id=?
            ON CONFLICT(peer_id) DO UPDATE SET last_activity_at=MAX(conversation.last_activity_at,excluded.last_activity_at),
              unread_count=conversation.unread_count+excluded.unread_count,
              draft=CASE WHEN excluded.draft='' THEN conversation.draft ELSE excluded.draft END
            """)) conversation.Bind(1, conversationId).Bind(2, newId).Bind(3, oldId).ExecuteNonQuery();
        using (var files = db.Prepare("""
            UPDATE attachment SET state='Failed' WHERE transfer_id IN
            (SELECT transfer_id FROM transfer WHERE peer_id=? AND UPPER(status) NOT IN ('COMPLETED','FAILED','CANCELED'))
            """)) files.Bind(1, oldId).ExecuteNonQuery();
        using (var messages = db.Prepare("""
            UPDATE message SET peer_id=?,conversation_id=?,
              status=CASE WHEN direction='Outgoing' AND UPPER(status) IN ('LOCALQUEUED','QUEUED','PENDING','SENDING') THEN 'Failed' ELSE status END WHERE peer_id=?
            """)) messages.Bind(1, newId).Bind(2, conversationId).Bind(3, oldId).ExecuteNonQuery();
        using (var transfers = db.Prepare("""
            UPDATE transfer SET peer_id=?,status=CASE WHEN UPPER(status) NOT IN ('COMPLETED','FAILED','CANCELED') THEN 'Failed' ELSE status END,
              failure_detail=CASE WHEN UPPER(status) NOT IN ('COMPLETED','FAILED','CANCELED') THEN '设备身份已变化，请确认后手动重试。' ELSE failure_detail END WHERE peer_id=?
            """)) transfers.Bind(1, newId).Bind(2, oldId).ExecuteNonQuery();
        using (var sessions = db.Prepare("UPDATE session_record SET peer_id=? WHERE peer_id=?"))
            sessions.Bind(1, newId).Bind(2, oldId).ExecuteNonQuery();
        using (var hints = db.Prepare("INSERT OR IGNORE INTO peer_hint(peer_id,hint) SELECT ?,hint FROM peer_hint WHERE peer_id=?"))
            hints.Bind(1, newId).Bind(2, oldId).ExecuteNonQuery();
        foreach (var table in new[] { "peer_hint", "conversation", "trust" })
        {
            using var remove = db.Prepare($"DELETE FROM {table} WHERE peer_id=?");
            remove.Bind(1, oldId).ExecuteNonQuery();
        }
        using (var retire = db.Prepare("UPDATE peer SET trust_state='Retired' WHERE peer_id=?")) retire.Bind(1, oldId).ExecuteNonQuery();
        if (newKey is not null)
        {
            using var trust = db.Prepare("INSERT OR REPLACE INTO trust(peer_id,identity_public_key,trusted_at) VALUES(?,?,?)");
            trust.Bind(1, newId).Bind(2, newKey).Bind(3, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ExecuteNonQuery();
        }
    }
}
