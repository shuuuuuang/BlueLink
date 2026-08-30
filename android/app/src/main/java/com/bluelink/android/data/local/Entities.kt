package com.bluelink.android.data.local

import androidx.room.Entity
import androidx.room.ForeignKey
import androidx.room.Index

@Entity(tableName = "peer")
data class PeerEntity(
    @androidx.room.PrimaryKey val peerId: String,
    val identityPublicKey: ByteArray? = null,
    val displayName: String = "",
    val platform: String = "",
    val trustState: String = "UNKNOWN",
    val createdAt: Long,
    val lastSeenAt: Long,
    val lastConnectedAt: Long? = null,
    val transportAddress: String = "",
)

@Entity(
    tableName = "conversation",
    foreignKeys = [ForeignKey(
        entity = PeerEntity::class,
        parentColumns = ["peerId"],
        childColumns = ["peerId"],
        onDelete = ForeignKey.CASCADE,
    )],
    indices = [Index(value = ["peerId"], unique = true)],
)
data class ConversationEntity(
    @androidx.room.PrimaryKey val conversationId: String,
    val peerId: String,
    val lastActivityAt: Long,
    val unreadCount: Int = 0,
    val draft: String = "",
)

@Entity(
    tableName = "message",
    foreignKeys = [
        ForeignKey(
            entity = ConversationEntity::class,
            parentColumns = ["conversationId"],
            childColumns = ["conversationId"],
            onDelete = ForeignKey.CASCADE,
        ),
        ForeignKey(
            entity = PeerEntity::class,
            parentColumns = ["peerId"],
            childColumns = ["peerId"],
            onDelete = ForeignKey.CASCADE,
        ),
    ],
    indices = [Index("conversationId", "monotonicOrder", "createdAt"), Index("peerId")],
)
data class MessageEntity(
    @androidx.room.PrimaryKey val messageId: String,
    val conversationId: String,
    val peerId: String,
    val direction: String,
    val type: String,
    val content: String = "",
    val status: String,
    val createdAt: Long,
    val monotonicOrder: Long,
)

@Entity(
    tableName = "attachment",
    foreignKeys = [ForeignKey(
        entity = MessageEntity::class,
        parentColumns = ["messageId"],
        childColumns = ["messageId"],
        onDelete = ForeignKey.CASCADE,
    )],
    indices = [Index("messageId"), Index("transferId")],
)
data class AttachmentEntity(
    @androidx.room.PrimaryKey val attachmentId: String,
    val messageId: String,
    val transferId: String? = null,
    val fileName: String,
    val mimeType: String = "application/octet-stream",
    val sizeBytes: Long,
    val sha256: ByteArray? = null,
    val localUri: String? = null,
    val previewUri: String? = null,
    val state: String,
)

@Entity(
    tableName = "transfer",
    foreignKeys = [ForeignKey(
        entity = PeerEntity::class,
        parentColumns = ["peerId"],
        childColumns = ["peerId"],
        onDelete = ForeignKey.CASCADE,
    )],
    indices = [Index("peerId", "updatedAt"), Index("messageId")],
)
data class TransferEntity(
    @androidx.room.PrimaryKey val transferId: String,
    val peerId: String,
    val messageId: String? = null,
    val direction: String,
    val status: String,
    val fileName: String,
    val mimeType: String = "application/octet-stream",
    val totalBytes: Long,
    val completedBytes: Long = 0,
    val localUri: String? = null,
    val snapshotPath: String? = null,
    val sha256: ByteArray? = null,
    val failureCode: String? = null,
    val failureDetail: String? = null,
    val createdAt: Long,
    val updatedAt: Long,
)

@Entity(
    tableName = "transfer_extent",
    primaryKeys = ["transferId", "extentIndex"],
    foreignKeys = [ForeignKey(
        entity = TransferEntity::class,
        parentColumns = ["transferId"],
        childColumns = ["transferId"],
        onDelete = ForeignKey.CASCADE,
    )],
    indices = [Index("transferId")],
)
data class TransferExtentEntity(
    val transferId: String,
    val extentIndex: Int,
    val offsetBytes: Long,
    val sizeBytes: Int,
    val sha256: ByteArray,
    val status: String,
)

@Entity(
    tableName = "session_record",
    foreignKeys = [ForeignKey(
        entity = PeerEntity::class,
        parentColumns = ["peerId"],
        childColumns = ["peerId"],
        onDelete = ForeignKey.CASCADE,
    )],
    indices = [Index("peerId")],
)
data class SessionRecordEntity(
    @androidx.room.PrimaryKey val sessionId: String,
    val peerId: String,
    val epoch: Long,
    val state: String,
    val connectedAt: Long? = null,
    val disconnectedAt: Long? = null,
    val disconnectReason: String? = null,
)

@Entity(
    tableName = "trust",
    foreignKeys = [ForeignKey(
        entity = PeerEntity::class,
        parentColumns = ["peerId"],
        childColumns = ["peerId"],
        onDelete = ForeignKey.CASCADE,
    )],
)
data class TrustEntity(
    @androidx.room.PrimaryKey val peerId: String,
    val identityPublicKey: ByteArray,
    val trustedAt: Long,
)

@Entity(tableName = "app_setting")
data class AppSettingEntity(
    @androidx.room.PrimaryKey val key: String,
    val value: String,
)

data class AppSettings(
    val autoConnectTrustedDevices: Boolean = true,
    val scanOnStartup: Boolean = true,
    val keepBackgroundSessions: Boolean = true,
    val maxConcurrentConnections: Int = 4,
    val autoDownloadFiles: Boolean = true,
    val receiveSizeLimitEnabled: Boolean = true,
    val receiveSizeLimitBytes: Long = 500L * 1024 * 1024,
    val showImageThumbnails: Boolean = true,
    val saveChatHistory: Boolean = true,
    val saveTransferHistory: Boolean = true,
    val diagnosticsEnabled: Boolean = true,
    val retentionPeriod: String = "forever",
    val downloadDirectory: String = DEFAULT_DOWNLOAD_DIRECTORY,
) {
    companion object { const val DEFAULT_DOWNLOAD_DIRECTORY = "downloads://BlueLink" }
}
