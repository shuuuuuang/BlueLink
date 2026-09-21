namespace BlueLink.Storage;

public enum StoredTrustState { Unknown, Trusted, Blocked, Retired, Removed }
public enum StoredMessageDirection { Incoming, Outgoing }
public enum StoredMessageType { Text, Image, File, System }

public sealed record StoredPeer(
    string PeerId,
    string DisplayName,
    string Platform,
    StoredTrustState TrustState,
    byte[]? IdentityPublicKey,
    long CreatedAt,
    long LastSeenAt,
    long? LastConnectedAt = null,
    string TransportAddress = "",
    string UsbTransportAddress = "");

public sealed record StoredConversation(
    string ConversationId,
    string PeerId,
    long LastActivityAt,
    int UnreadCount,
    string Draft = "");

public sealed record StoredMessage(
    string MessageId,
    string ConversationId,
    string PeerId,
    StoredMessageDirection Direction,
    StoredMessageType Type,
    string Content,
    string Status,
    long CreatedAt,
    long MonotonicOrder);

public sealed record StoredAttachment(
    string AttachmentId,
    string MessageId,
    string? TransferId,
    string FileName,
    string MimeType,
    long Size,
    byte[]? Sha256,
    string? LocalPath,
    string? PreviewPath,
    string State);

public sealed record StoredTransfer(
    string TransferId,
    string PeerId,
    string? MessageId,
    string Direction,
    string Status,
    string FileName,
    string MimeType,
    long TotalBytes,
    long CompletedBytes,
    string? LocalPath,
    string? SnapshotPath,
    byte[]? Sha256,
    string? FailureCode,
    string? FailureDetail,
    long CreatedAt,
    long UpdatedAt);

public sealed record StoredTransferExtent(
    string TransferId,
    int ExtentIndex,
    long Offset,
    int Size,
    byte[] Sha256,
    string Status);

public sealed record StoredSessionRecord(
    string SessionId,
    string PeerId,
    long Epoch,
    string State,
    long? ConnectedAt,
    long? DisconnectedAt,
    string? DisconnectReason);

public sealed record BlueLinkSettings(
    bool AutoConnectTrustedDevices = true,
    bool ScanOnStartup = true,
    bool KeepBackgroundSessions = true,
    bool AutoDownloadFiles = true,
    bool ReceiveSizeLimitEnabled = true,
    long ReceiveSizeLimitBytes = 500L * 1024 * 1024,
    bool ShowImageThumbnails = true,
    bool SaveChatHistory = true,
    bool SaveTransferHistory = true,
    bool DiagnosticsEnabled = true,
    string RetentionPeriod = "forever",
    string DownloadDirectory = "",
    string LocalDeviceName = "",
    string Theme = "system",
    string Language = "zh-CN",
    bool UsbEnabled = false,
    bool AllowDiscovery = true,
    bool ReconnectAfterDisconnect = true,
    string DuplicateFilePolicy = "rename",
    bool MessageNotifications = true,
    bool ConnectionNotifications = true,
    bool TransferNotifications = true,
    string SendShortcut = "enter")
{
    public static BlueLinkSettings Defaults(string downloadDirectory) => new(DownloadDirectory: downloadDirectory);
}
