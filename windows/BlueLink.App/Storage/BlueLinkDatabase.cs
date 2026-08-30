using System.Globalization;
using BlueLink.Security;

namespace BlueLink.Storage;

/// <summary>
/// Durable local state for the Windows client. This deliberately uses the
/// SQLite runtime shipped with Windows 10/11 so the installed application has
/// no native NuGet dependency to restore or deploy.
/// </summary>
public sealed class BlueLinkDatabase
{
    private const int SchemaVersion = 2;

    public string DatabasePath { get; }
    public string DefaultDownloadDirectory { get; }

    public BlueLinkDatabase(string? dataRoot = null, string? installRoot = null)
    {
        dataRoot ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlueLink", "Data");
        Directory.CreateDirectory(dataRoot);
        DatabasePath = Path.Combine(dataRoot, "bluelink.db");
        DefaultDownloadDirectory = Path.Combine(installRoot ?? AppContext.BaseDirectory, "Download");
    }

    public Task InitializeAsync(IdentityStore identityStore, CancellationToken token = default) => Run(() =>
    {
        using var connection = Open();
        connection.Execute(SchemaSql);
        EnsurePeerTransportColumn(connection);
        connection.Execute($"PRAGMA user_version={SchemaVersion};");
        EnsureDefaultSettings(connection);
        ImportLegacyTrust(connection, identityStore.TrustedIdentities);
    }, token);

    public Task<BlueLinkSettings> LoadSettingsAsync(CancellationToken token = default) => Run(() =>
    {
        using var connection = Open();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var statement = connection.Prepare("SELECT key, value FROM app_setting");
        while (statement.Read()) values[statement.GetString(0)] = statement.GetString(1);

        var defaults = BlueLinkSettings.Defaults(DefaultDownloadDirectory);
        return new BlueLinkSettings(
            Bool(values, "auto_connect_trusted", defaults.AutoConnectTrustedDevices),
            Bool(values, "scan_on_startup", defaults.ScanOnStartup),
            Bool(values, "keep_background_sessions", defaults.KeepBackgroundSessions),
            Math.Clamp(Int(values, "max_connections", defaults.MaxConcurrentConnections), 1, 8),
            Bool(values, "auto_download_files", defaults.AutoDownloadFiles),
            Bool(values, "receive_limit_enabled", defaults.ReceiveSizeLimitEnabled),
            Math.Max(0, Long(values, "receive_limit_bytes", defaults.ReceiveSizeLimitBytes)),
            Bool(values, "show_image_thumbnails", defaults.ShowImageThumbnails),
            Bool(values, "save_chat_history", defaults.SaveChatHistory),
            Bool(values, "save_transfer_history", defaults.SaveTransferHistory),
            Bool(values, "diagnostics_enabled", defaults.DiagnosticsEnabled),
            Get(values, "retention_period", defaults.RetentionPeriod),
            Get(values, "download_directory", defaults.DownloadDirectory),
            Bool(values, "transfer_panel_expanded", defaults.TransferPanelExpanded));
    }, token);

    public Task SaveSettingsAsync(BlueLinkSettings settings, CancellationToken token = default) => Run(() =>
    {
        using var connection = Open();
        InTransaction(connection, () =>
        {
            foreach (var pair in SettingsValues(settings)) UpsertSetting(connection, pair.Key, pair.Value);
        });
    }, token);

    public Task UpsertPeerAsync(StoredPeer peer, CancellationToken token = default) => Run(() =>
    {
        using var connection = Open();
        using var statement = connection.Prepare("""
            INSERT INTO peer(peer_id, identity_public_key, display_name, platform, trust_state, created_at, last_seen_at, last_connected_at, transport_address)
            VALUES(?,?,?,?,?,?,?,?,?)
            ON CONFLICT(peer_id) DO UPDATE SET
              identity_public_key=COALESCE(excluded.identity_public_key, peer.identity_public_key),
              display_name=CASE WHEN excluded.display_name='' THEN peer.display_name ELSE excluded.display_name END,
              platform=CASE WHEN excluded.platform='' THEN peer.platform ELSE excluded.platform END,
              trust_state=excluded.trust_state,
              last_seen_at=MAX(peer.last_seen_at, excluded.last_seen_at),
              last_connected_at=COALESCE(excluded.last_connected_at, peer.last_connected_at),
              transport_address=CASE WHEN excluded.transport_address='' THEN peer.transport_address ELSE excluded.transport_address END
            """);
        statement.Bind(1, peer.PeerId).Bind(2, peer.IdentityPublicKey).Bind(3, peer.DisplayName)
            .Bind(4, peer.Platform).Bind(5, peer.TrustState.ToString()).Bind(6, peer.CreatedAt)
            .Bind(7, peer.LastSeenAt).Bind(8, peer.LastConnectedAt).Bind(9, peer.TransportAddress).ExecuteNonQuery();
    }, token);

    public Task<IReadOnlyList<StoredPeer>> LoadPeersAsync(CancellationToken token = default) => Run<IReadOnlyList<StoredPeer>>(() =>
    {
        var result = new List<StoredPeer>();
        using var connection = Open();
        using var statement = connection.Prepare("SELECT peer_id, display_name, platform, trust_state, identity_public_key, created_at, last_seen_at, last_connected_at, transport_address FROM peer ORDER BY last_seen_at DESC");
        while (statement.Read())
        {
            result.Add(new StoredPeer(
                statement.GetString(0), statement.GetString(1), statement.GetString(2),
                Enum.TryParse<StoredTrustState>(statement.GetString(3), true, out var trust) ? trust : StoredTrustState.Unknown,
                statement.IsNull(4) ? null : statement.GetBlob(4), statement.GetInt64(5), statement.GetInt64(6),
                statement.IsNull(7) ? null : statement.GetInt64(7), statement.GetString(8)));
        }
        return result;
    }, token);

    public Task UpsertConversationAsync(StoredConversation value, CancellationToken token = default) => Run(() =>
    {
        using var connection = Open();
        using var statement = connection.Prepare("""
            INSERT INTO conversation(conversation_id, peer_id, last_activity_at, unread_count, draft)
            VALUES(?,?,?,?,?)
            ON CONFLICT(conversation_id) DO UPDATE SET last_activity_at=excluded.last_activity_at,
              unread_count=excluded.unread_count, draft=excluded.draft
            """);
        statement.Bind(1, value.ConversationId).Bind(2, value.PeerId).Bind(3, value.LastActivityAt)
            .Bind(4, value.UnreadCount).Bind(5, value.Draft).ExecuteNonQuery();
    }, token);

    public Task<IReadOnlyList<StoredConversation>> LoadConversationsAsync(CancellationToken token = default) => Run<IReadOnlyList<StoredConversation>>(() =>
    {
        var result = new List<StoredConversation>();
        using var connection = Open();
        using var statement = connection.Prepare("SELECT conversation_id, peer_id, last_activity_at, unread_count, draft FROM conversation ORDER BY last_activity_at DESC");
        while (statement.Read()) result.Add(new(statement.GetString(0), statement.GetString(1), statement.GetInt64(2), checked((int)statement.GetInt64(3)), statement.GetString(4)));
        return result;
    }, token);

    public Task UpsertMessageAsync(StoredMessage value, CancellationToken token = default) => Run(() =>
    {
        using var connection = Open();
        using var statement = connection.Prepare("""
            INSERT INTO message(message_id, conversation_id, peer_id, direction, type, content, status, created_at, monotonic_order)
            VALUES(?,?,?,?,?,?,?,?,?)
            ON CONFLICT(message_id) DO UPDATE SET status=excluded.status, content=excluded.content
            """);
        statement.Bind(1, value.MessageId).Bind(2, value.ConversationId).Bind(3, value.PeerId)
            .Bind(4, value.Direction.ToString()).Bind(5, value.Type.ToString()).Bind(6, value.Content)
            .Bind(7, value.Status).Bind(8, value.CreatedAt).Bind(9, value.MonotonicOrder).ExecuteNonQuery();
    }, token);

    public Task<IReadOnlyList<StoredMessage>> LoadMessagesAsync(string conversationId, CancellationToken token = default) => Run<IReadOnlyList<StoredMessage>>(() =>
    {
        var result = new List<StoredMessage>();
        using var connection = Open();
        using var statement = connection.Prepare("SELECT message_id, conversation_id, peer_id, direction, type, content, status, created_at, monotonic_order FROM message WHERE conversation_id=? ORDER BY monotonic_order, created_at");
        statement.Bind(1, conversationId);
        while (statement.Read()) result.Add(new(
            statement.GetString(0), statement.GetString(1), statement.GetString(2),
            Enum.TryParse<StoredMessageDirection>(statement.GetString(3), true, out var direction) ? direction : StoredMessageDirection.Incoming,
            Enum.TryParse<StoredMessageType>(statement.GetString(4), true, out var type) ? type : StoredMessageType.System,
            statement.GetString(5), statement.GetString(6), statement.GetInt64(7), statement.GetInt64(8)));
        return result;
    }, token);

    public Task UpsertAttachmentAsync(StoredAttachment value, CancellationToken token = default) => Run(() =>
    {
        using var connection = Open();
        using var statement = connection.Prepare("""
            INSERT INTO attachment(attachment_id, message_id, transfer_id, file_name, mime_type, size_bytes, sha256, local_path, preview_path, state)
            VALUES(?,?,?,?,?,?,?,?,?,?)
            ON CONFLICT(attachment_id) DO UPDATE SET transfer_id=excluded.transfer_id, sha256=excluded.sha256,
              local_path=excluded.local_path, preview_path=excluded.preview_path, state=excluded.state
            """);
        statement.Bind(1, value.AttachmentId).Bind(2, value.MessageId).Bind(3, value.TransferId)
            .Bind(4, value.FileName).Bind(5, value.MimeType).Bind(6, value.Size).Bind(7, value.Sha256)
            .Bind(8, value.LocalPath).Bind(9, value.PreviewPath).Bind(10, value.State).ExecuteNonQuery();
    }, token);

    public Task<IReadOnlyList<StoredAttachment>> LoadAttachmentsAsync(string messageId, CancellationToken token = default) => Run<IReadOnlyList<StoredAttachment>>(() =>
    {
        var result = new List<StoredAttachment>();
        using var connection = Open();
        using var statement = connection.Prepare("SELECT attachment_id, message_id, transfer_id, file_name, mime_type, size_bytes, sha256, local_path, preview_path, state FROM attachment WHERE message_id=? ORDER BY rowid");
        statement.Bind(1, messageId);
        while (statement.Read()) result.Add(new(
            statement.GetString(0), statement.GetString(1), NullableString(statement, 2), statement.GetString(3),
            statement.GetString(4), statement.GetInt64(5), statement.IsNull(6) ? null : statement.GetBlob(6),
            NullableString(statement, 7), NullableString(statement, 8), statement.GetString(9)));
        return result;
    }, token);

    public Task UpsertTransferAsync(StoredTransfer value, CancellationToken token = default) => Run(() =>
    {
        using var connection = Open();
        using var statement = connection.Prepare("""
            INSERT INTO transfer(transfer_id, peer_id, message_id, direction, status, file_name, mime_type,
              total_bytes, completed_bytes, local_path, snapshot_path, sha256, failure_code, failure_detail, created_at, updated_at)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
            ON CONFLICT(transfer_id) DO UPDATE SET status=excluded.status, completed_bytes=excluded.completed_bytes,
              local_path=excluded.local_path, snapshot_path=excluded.snapshot_path, failure_code=excluded.failure_code,
              failure_detail=excluded.failure_detail, updated_at=excluded.updated_at
            """);
        statement.Bind(1, value.TransferId).Bind(2, value.PeerId).Bind(3, value.MessageId)
            .Bind(4, value.Direction).Bind(5, value.Status).Bind(6, value.FileName).Bind(7, value.MimeType)
            .Bind(8, value.TotalBytes).Bind(9, value.CompletedBytes).Bind(10, value.LocalPath)
            .Bind(11, value.SnapshotPath).Bind(12, value.Sha256).Bind(13, value.FailureCode)
            .Bind(14, value.FailureDetail).Bind(15, value.CreatedAt).Bind(16, value.UpdatedAt).ExecuteNonQuery();
    }, token);

    public Task<IReadOnlyList<StoredTransfer>> LoadTransfersAsync(string? peerId = null, CancellationToken token = default) => Run<IReadOnlyList<StoredTransfer>>(() =>
    {
        var result = new List<StoredTransfer>();
        using var connection = Open();
        using var statement = connection.Prepare(peerId is null
            ? "SELECT transfer_id, peer_id, message_id, direction, status, file_name, mime_type, total_bytes, completed_bytes, local_path, snapshot_path, sha256, failure_code, failure_detail, created_at, updated_at FROM transfer ORDER BY updated_at DESC"
            : "SELECT transfer_id, peer_id, message_id, direction, status, file_name, mime_type, total_bytes, completed_bytes, local_path, snapshot_path, sha256, failure_code, failure_detail, created_at, updated_at FROM transfer WHERE peer_id=? ORDER BY updated_at DESC");
        if (peerId is not null) statement.Bind(1, peerId);
        while (statement.Read()) result.Add(new(
            statement.GetString(0), statement.GetString(1), NullableString(statement, 2), statement.GetString(3), statement.GetString(4),
            statement.GetString(5), statement.GetString(6), statement.GetInt64(7), statement.GetInt64(8), NullableString(statement, 9),
            NullableString(statement, 10), statement.IsNull(11) ? null : statement.GetBlob(11), NullableString(statement, 12),
            NullableString(statement, 13), statement.GetInt64(14), statement.GetInt64(15)));
        return result;
    }, token);

    public Task UpsertExtentAsync(StoredTransferExtent value, CancellationToken token = default) => Run(() =>
    {
        using var connection = Open();
        using var statement = connection.Prepare("""
            INSERT INTO transfer_extent(transfer_id, extent_index, offset_bytes, size_bytes, sha256, status)
            VALUES(?,?,?,?,?,?)
            ON CONFLICT(transfer_id, extent_index) DO UPDATE SET sha256=excluded.sha256, status=excluded.status
            """);
        statement.Bind(1, value.TransferId).Bind(2, value.ExtentIndex).Bind(3, value.Offset)
            .Bind(4, value.Size).Bind(5, value.Sha256).Bind(6, value.Status).ExecuteNonQuery();
    }, token);

    public Task UpsertSessionRecordAsync(StoredSessionRecord value, CancellationToken token = default) => Run(() =>
    {
        using var connection = Open();
        using var statement = connection.Prepare("""
            INSERT INTO session_record(session_id, peer_id, epoch, state, connected_at, disconnected_at, disconnect_reason)
            VALUES(?,?,?,?,?,?,?)
            ON CONFLICT(session_id) DO UPDATE SET state=excluded.state,
              connected_at=COALESCE(excluded.connected_at, session_record.connected_at),
              disconnected_at=excluded.disconnected_at, disconnect_reason=excluded.disconnect_reason
            """);
        statement.Bind(1, value.SessionId).Bind(2, value.PeerId).Bind(3, value.Epoch).Bind(4, value.State)
            .Bind(5, value.ConnectedAt).Bind(6, value.DisconnectedAt).Bind(7, value.DisconnectReason).ExecuteNonQuery();
    }, token);

    public Task ClearMessagesAsync(CancellationToken token = default) => Run(() =>
    {
        using var connection = Open(); connection.Execute("DELETE FROM message;");
    }, token);

    public Task DeleteMessageAsync(string messageId, CancellationToken token = default) => Run(() =>
    {
        using var connection = Open();
        using var statement = connection.Prepare("DELETE FROM message WHERE message_id = ?");
        statement.Bind(1, messageId).ExecuteNonQuery();
    }, token);

    public Task ClearConversationMessagesAsync(string conversationId, CancellationToken token = default) => Run(() =>
    {
        using var connection = Open();
        using var statement = connection.Prepare("DELETE FROM message WHERE conversation_id = ?");
        statement.Bind(1, conversationId).ExecuteNonQuery();
    }, token);

    public Task ClearTransfersAsync(CancellationToken token = default) => Run(() =>
    {
        using var connection = Open(); connection.Execute("DELETE FROM transfer;");
    }, token);

    public Task DeleteTransferAsync(string transferId, CancellationToken token = default) => Run(() =>
    {
        using var connection = Open();
        using var statement = connection.Prepare("DELETE FROM transfer WHERE transfer_id = ?");
        statement.Bind(1, transferId).ExecuteNonQuery();
    }, token);

    public Task ClearCompletedTransfersAsync(CancellationToken token = default) => Run(() =>
    {
        using var connection = Open();
        using var statement = connection.Prepare("DELETE FROM transfer WHERE status = ?");
        statement.Bind(1, "Completed").ExecuteNonQuery();
    }, token);

    public Task ResetTrustAndSettingsAsync(CancellationToken token = default) => Run(() =>
    {
        using var connection = Open();
        InTransaction(connection, () =>
        {
            connection.Execute("DELETE FROM trust;");
            connection.Execute("UPDATE peer SET trust_state='Unknown', identity_public_key=NULL;");
            connection.Execute("DELETE FROM app_setting;");
            EnsureDefaultSettings(connection);
        });
    }, token);

    public Task DeleteHistoryBeforeAsync(long cutoffEpochMs, CancellationToken token = default) => Run(() =>
    {
        using var connection = Open();
        InTransaction(connection, () =>
        {
            using (var messages = connection.Prepare("DELETE FROM message WHERE created_at < ?"))
                messages.Bind(1, cutoffEpochMs).ExecuteNonQuery();
            using var transfers = connection.Prepare("DELETE FROM transfer WHERE updated_at < ?");
            transfers.Bind(1, cutoffEpochMs).ExecuteNonQuery();
        });
    }, token);

    private NativeSqliteConnection Open()
    {
        var connection = new NativeSqliteConnection(DatabasePath);
        connection.Execute("PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        return connection;
    }

    private void EnsureDefaultSettings(NativeSqliteConnection connection)
    {
        foreach (var pair in SettingsValues(BlueLinkSettings.Defaults(DefaultDownloadDirectory)))
        {
            using var statement = connection.Prepare("INSERT OR IGNORE INTO app_setting(key,value) VALUES(?,?)");
            statement.Bind(1, pair.Key).Bind(2, pair.Value).ExecuteNonQuery();
        }
    }

    private static void EnsurePeerTransportColumn(NativeSqliteConnection connection)
    {
        using var statement = connection.Prepare("PRAGMA table_info(peer)");
        while (statement.Read()) if (statement.GetString(1).Equals("transport_address", StringComparison.OrdinalIgnoreCase)) return;
        connection.Execute("ALTER TABLE peer ADD COLUMN transport_address TEXT NOT NULL DEFAULT '';");
    }

    private static void ImportLegacyTrust(NativeSqliteConnection connection, IReadOnlyList<TrustedIdentity> trusted)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        InTransaction(connection, () =>
        {
            foreach (var value in trusted)
            {
                using (var peer = connection.Prepare("""
                    INSERT INTO peer(peer_id, identity_public_key, display_name, platform, trust_state, created_at, last_seen_at)
                    VALUES(?,?,'','','Trusted',?,?)
                    ON CONFLICT(peer_id) DO UPDATE SET identity_public_key=excluded.identity_public_key, trust_state='Trusted'
                    """))
                {
                    peer.Bind(1, value.PeerIdHex).Bind(2, value.PublicKey).Bind(3, now).Bind(4, now).ExecuteNonQuery();
                }
                using var trust = connection.Prepare("""
                    INSERT INTO trust(peer_id, identity_public_key, trusted_at) VALUES(?,?,?)
                    ON CONFLICT(peer_id) DO UPDATE SET identity_public_key=excluded.identity_public_key
                    """);
                trust.Bind(1, value.PeerIdHex).Bind(2, value.PublicKey).Bind(3, now).ExecuteNonQuery();
            }
        });
    }

    private static void UpsertSetting(NativeSqliteConnection connection, string key, string value)
    {
        using var statement = connection.Prepare("INSERT INTO app_setting(key,value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value");
        statement.Bind(1, key).Bind(2, value).ExecuteNonQuery();
    }

    private static void InTransaction(NativeSqliteConnection connection, Action action)
    {
        connection.Execute("BEGIN IMMEDIATE;");
        try { action(); connection.Execute("COMMIT;"); }
        catch { connection.Execute("ROLLBACK;"); throw; }
    }

    private Dictionary<string, string> SettingsValues(BlueLinkSettings value) => new()
    {
        ["auto_connect_trusted"] = Value(value.AutoConnectTrustedDevices),
        ["scan_on_startup"] = Value(value.ScanOnStartup),
        ["keep_background_sessions"] = Value(value.KeepBackgroundSessions),
        ["max_connections"] = value.MaxConcurrentConnections.ToString(CultureInfo.InvariantCulture),
        ["auto_download_files"] = Value(value.AutoDownloadFiles),
        ["receive_limit_enabled"] = Value(value.ReceiveSizeLimitEnabled),
        ["receive_limit_bytes"] = value.ReceiveSizeLimitBytes.ToString(CultureInfo.InvariantCulture),
        ["show_image_thumbnails"] = Value(value.ShowImageThumbnails),
        ["save_chat_history"] = Value(value.SaveChatHistory),
        ["save_transfer_history"] = Value(value.SaveTransferHistory),
        ["diagnostics_enabled"] = Value(value.DiagnosticsEnabled),
        ["retention_period"] = value.RetentionPeriod,
        ["download_directory"] = value.DownloadDirectory,
        ["transfer_panel_expanded"] = Value(value.TransferPanelExpanded),
    };

    private static Task Run(Action action, CancellationToken token) => Task.Run(() => { token.ThrowIfCancellationRequested(); action(); }, token);
    private static Task<T> Run<T>(Func<T> action, CancellationToken token) => Task.Run(() => { token.ThrowIfCancellationRequested(); return action(); }, token);
    private static string? NullableString(NativeSqliteStatement statement, int column) => statement.IsNull(column) ? null : statement.GetString(column);
    private static string Value(bool value) => value ? "1" : "0";
    private static string Get(IReadOnlyDictionary<string, string> values, string key, string fallback) => values.TryGetValue(key, out var value) ? value : fallback;
    private static bool Bool(IReadOnlyDictionary<string, string> values, string key, bool fallback) => values.TryGetValue(key, out var value) ? value is "1" or "true" or "True" : fallback;
    private static int Int(IReadOnlyDictionary<string, string> values, string key, int fallback) => values.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    private static long Long(IReadOnlyDictionary<string, string> values, string key, long fallback) => values.TryGetValue(key, out var value) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private const string SchemaSql = """
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS peer(
          peer_id TEXT PRIMARY KEY, identity_public_key BLOB, display_name TEXT NOT NULL DEFAULT '',
          platform TEXT NOT NULL DEFAULT '', trust_state TEXT NOT NULL DEFAULT 'Unknown',
          created_at INTEGER NOT NULL, last_seen_at INTEGER NOT NULL, last_connected_at INTEGER,
          transport_address TEXT NOT NULL DEFAULT '');
        CREATE TABLE IF NOT EXISTS conversation(
          conversation_id TEXT PRIMARY KEY, peer_id TEXT NOT NULL UNIQUE REFERENCES peer(peer_id) ON DELETE CASCADE,
          last_activity_at INTEGER NOT NULL, unread_count INTEGER NOT NULL DEFAULT 0, draft TEXT NOT NULL DEFAULT '');
        CREATE TABLE IF NOT EXISTS message(
          message_id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL REFERENCES conversation(conversation_id) ON DELETE CASCADE,
          peer_id TEXT NOT NULL REFERENCES peer(peer_id) ON DELETE CASCADE, direction TEXT NOT NULL, type TEXT NOT NULL,
          content TEXT NOT NULL DEFAULT '', status TEXT NOT NULL, created_at INTEGER NOT NULL, monotonic_order INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_message_conversation_order ON message(conversation_id, monotonic_order, created_at);
        CREATE TABLE IF NOT EXISTS attachment(
          attachment_id TEXT PRIMARY KEY, message_id TEXT NOT NULL REFERENCES message(message_id) ON DELETE CASCADE,
          transfer_id TEXT, file_name TEXT NOT NULL, mime_type TEXT NOT NULL DEFAULT 'application/octet-stream',
          size_bytes INTEGER NOT NULL, sha256 BLOB, local_path TEXT, preview_path TEXT, state TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_attachment_message ON attachment(message_id);
        CREATE TABLE IF NOT EXISTS transfer(
          transfer_id TEXT PRIMARY KEY, peer_id TEXT NOT NULL REFERENCES peer(peer_id) ON DELETE CASCADE, message_id TEXT,
          direction TEXT NOT NULL, status TEXT NOT NULL, file_name TEXT NOT NULL,
          mime_type TEXT NOT NULL DEFAULT 'application/octet-stream', total_bytes INTEGER NOT NULL,
          completed_bytes INTEGER NOT NULL DEFAULT 0, local_path TEXT, snapshot_path TEXT, sha256 BLOB,
          failure_code TEXT, failure_detail TEXT, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_transfer_peer_updated ON transfer(peer_id, updated_at DESC);
        CREATE TABLE IF NOT EXISTS transfer_extent(
          transfer_id TEXT NOT NULL REFERENCES transfer(transfer_id) ON DELETE CASCADE, extent_index INTEGER NOT NULL,
          offset_bytes INTEGER NOT NULL, size_bytes INTEGER NOT NULL, sha256 BLOB NOT NULL, status TEXT NOT NULL,
          PRIMARY KEY(transfer_id, extent_index));
        CREATE TABLE IF NOT EXISTS session_record(
          session_id TEXT PRIMARY KEY, peer_id TEXT NOT NULL REFERENCES peer(peer_id) ON DELETE CASCADE,
          epoch INTEGER NOT NULL, state TEXT NOT NULL, connected_at INTEGER, disconnected_at INTEGER, disconnect_reason TEXT);
        CREATE TABLE IF NOT EXISTS trust(
          peer_id TEXT PRIMARY KEY REFERENCES peer(peer_id) ON DELETE CASCADE,
          identity_public_key BLOB NOT NULL, trusted_at INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS app_setting(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """;
}
