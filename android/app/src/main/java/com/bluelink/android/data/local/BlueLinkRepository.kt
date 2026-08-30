package com.bluelink.android.data.local

import androidx.room.withTransaction
import com.bluelink.android.data.IdentityStore
import com.bluelink.android.domain.ChatAttachment
import com.bluelink.android.domain.ChatItem
import com.bluelink.android.domain.ChatItemKind
import com.bluelink.android.domain.ManagedSessionState
import com.bluelink.android.domain.MessageStatus
import com.bluelink.android.domain.TransferItem
import com.bluelink.android.domain.TransferStatus
import com.bluelink.core.ChatEnvelope
import com.bluelink.core.ChatPayloadKind
import com.bluelink.core.AttachmentRole
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.delay
import java.time.Instant

class BlueLinkRepository(private val database: BlueLinkDatabase) {
    val peers: Flow<List<PeerEntity>> = database.peers().observeAll()
    val conversations: Flow<List<ConversationEntity>> = database.conversations().observeAll()
    val transfers: Flow<List<TransferEntity>> = database.transfers().observeAll()
    val trustedDevices: Flow<List<TrustEntity>> = database.trust().observeAll()
    val settings: Flow<AppSettings> = database.settings().observeAll().map(::decodeSettings)

    suspend fun initialize(identityStore: IdentityStore) {
        database.withTransaction {
            ensureDefaults()
            val now = System.currentTimeMillis()
            identityStore.trustedEntries().forEach { (peerId, publicKey) ->
                val previous = database.peers().find(peerId)
                database.peers().upsert(
                    PeerEntity(
                        peerId = peerId,
                        identityPublicKey = publicKey,
                        displayName = previous?.displayName.orEmpty(),
                        platform = previous?.platform.orEmpty(),
                        trustState = "TRUSTED",
                        createdAt = previous?.createdAt ?: now,
                        lastSeenAt = previous?.lastSeenAt ?: now,
                        lastConnectedAt = previous?.lastConnectedAt,
                    ),
                )
                database.trust().upsert(TrustEntity(peerId, publicKey, now))
            }
        }
    }

    suspend fun saveSettings(value: AppSettings) = database.withTransaction {
        encodeSettings(value).forEach { database.settings().upsert(it) }
    }

    suspend fun removeTrust(peerId: String, identityStore: IdentityStore) = database.withTransaction {
        database.trust().delete(peerId)
        database.peers().find(peerId)?.let { database.peers().upsert(it.copy(trustState = "UNKNOWN")) }
        identityStore.removeTrust(peerId)
    }

    fun messages(conversationId: String): Flow<List<MessageEntity>> =
        database.messages().observeConversation(conversationId)

    suspend fun recordConnectedSession(state: ManagedSessionState, identityStore: IdentityStore) {
        val peerId = state.peerId ?: return
        val now = System.currentTimeMillis()
        val key = identityStore.trustedEntries().entries.firstOrNull { it.key.equals(peerId, true) }?.value
        database.withTransaction {
            val previous = database.peers().find(peerId)
            database.peers().upsert(PeerEntity(peerId, key ?: previous?.identityPublicKey,
                state.peerName, "WINDOWS", "TRUSTED", previous?.createdAt ?: now, now, now,
                state.transportAddress))
            val conversation = database.conversations().findForPeer(peerId)
            database.conversations().upsert(conversation ?: ConversationEntity(
                conversationId(peerId), peerId, now))
            database.sessions().upsert(SessionRecordEntity(state.sessionId.toString(), peerId,
                state.startedAtEpochMs, "CONNECTED", connectedAt = now))
            if (key != null) database.trust().upsert(TrustEntity(peerId, key, now))
        }
    }

    suspend fun recordDisconnectedSession(state: ManagedSessionState) {
        val peerId = state.peerId ?: return
        val now = System.currentTimeMillis()
        database.sessions().upsert(SessionRecordEntity(state.sessionId.toString(), peerId,
            state.startedAtEpochMs, "DISCONNECTED", connectedAt = state.startedAtEpochMs,
            disconnectedAt = now, disconnectReason = state.detail))
    }

    suspend fun saveChat(state: ManagedSessionState, item: ChatItem, unread: Boolean = false) {
        val peerId = state.peerId ?: return
        val createdAt = item.timestamp.toEpochMilli()
        database.withTransaction {
            ensureConversation(peerId, state.peerName, state.transportAddress, createdAt)
            database.messages().upsert(MessageEntity(item.id.toString(), conversationId(peerId), peerId,
                if (item.outgoing) "OUTGOING" else "INCOMING", item.kind.name, item.text,
                item.status.name, createdAt, createdAt))
            val existing = database.conversations().findForPeer(peerId)
            if (existing != null) database.conversations().upsert(existing.copy(
                lastActivityAt = maxOf(existing.lastActivityAt, createdAt),
                unreadCount = existing.unreadCount + if (unread) 1 else 0))
        }
    }

    suspend fun saveEnvelope(state: ManagedSessionState, envelope: ChatEnvelope, outgoing: Boolean,
                             unread: Boolean = false) {
        val peerId = state.peerId ?: return
        val createdAt = envelope.createdAt()
        database.withTransaction {
            ensureConversation(peerId, state.peerName, state.transportAddress, createdAt)
            val type = when (envelope.kind()) {
                ChatPayloadKind.IMAGE -> "IMAGE"
                ChatPayloadKind.FILE -> "FILE"
                ChatPayloadKind.SYSTEM -> "SYSTEM"
                else -> "TEXT"
            }
            database.messages().upsert(MessageEntity(envelope.messageId().toString(), conversationId(peerId), peerId,
                if (outgoing) "OUTGOING" else "INCOMING", type, envelope.body(),
                if (outgoing) "SENT" else "RECEIVED", createdAt, createdAt))
            val storedAttachments = if (envelope.kind() == ChatPayloadKind.IMAGE)
                envelope.attachments().filter { it.role() == AttachmentRole.IMAGE_ORIGINAL }
            else envelope.attachments().filter { it.role() == AttachmentRole.FILE }
            storedAttachments.forEach { attachment -> database.attachments().upsert(AttachmentEntity(
                attachment.attachmentId().toString(), envelope.messageId().toString(),
                attachment.transferId().toString(), attachment.fileName(), attachment.mimeType(),
                attachment.size(), attachment.sha256(), state = "OFFERED")) }
            val existing = database.conversations().findForPeer(peerId)
            if (existing != null) database.conversations().upsert(existing.copy(
                lastActivityAt = maxOf(existing.lastActivityAt, createdAt),
                unreadCount = existing.unreadCount + if (unread) 1 else 0))
        }
    }

    suspend fun updateMessageStatus(messageId: String, status: MessageStatus) {
        database.messages().find(messageId)?.let { database.messages().upsert(it.copy(status = status.name)) }
    }

    suspend fun applyRetention(retention: String) {
        val days = when (retention) { "30d" -> 30L; "90d" -> 90L; "1y" -> 365L; else -> return }
        val cutoff = System.currentTimeMillis() - days * 24 * 60 * 60 * 1000
        database.withTransaction {
            database.messages().deleteBefore(cutoff)
            database.transfers().deleteBefore(cutoff)
        }
    }

    suspend fun saveTransfer(state: ManagedSessionState, value: TransferItem) {
        val peerId = state.peerId ?: return
        val now = System.currentTimeMillis()
        if (value.role == AttachmentRole.IMAGE_PREVIEW) {
            val messageId = value.messageId ?: return
            repeat(5) {
                val attachment = database.attachments().loadForMessage(messageId.toString())
                    .firstOrNull { it.mimeType.startsWith("image/", true) }
                if (attachment != null) {
                    database.attachments().upsert(attachment.copy(
                        previewUri = value.localUri ?: attachment.previewUri))
                    return
                }
                delay(50)
            }
            return
        }
        database.withTransaction {
            ensureConversation(peerId, state.peerName, state.transportAddress, now)
            val previous = database.transfers().find(value.id.toString())
            database.transfers().upsert(TransferEntity(value.id.toString(), peerId,
                value.messageId?.toString() ?: previous?.messageId,
                if (value.outgoing) "OUTGOING" else "INCOMING", value.status.name, value.name,
                value.mimeType.ifBlank { previous?.mimeType ?: "application/octet-stream" }, value.totalBytes,
                value.completedBytes, value.localUri ?: previous?.localUri, previous?.snapshotPath,
                previous?.sha256, if (value.status.name == "FAILED") "TRANSFER_FAILED" else null,
                value.failureDetail, previous?.createdAt ?: now, now))
            value.attachmentId?.toString()?.let { id -> database.attachments().find(id)?.let { attachment ->
                database.attachments().upsert(attachment.copy(transferId = value.id.toString(),
                    localUri = value.localUri ?: attachment.localUri, state = value.status.name))
            } }
        }
    }

    suspend fun loadHistory(peerId: String): List<ChatItem> {
        val conversation = database.conversations().findForPeer(peerId) ?: return emptyList()
        return database.messages().loadConversation(conversation.conversationId).map { message ->
            val attachments = database.attachments().loadForMessage(message.messageId).map { attachment ->
                ChatAttachment(java.util.UUID.fromString(attachment.attachmentId),
                    attachment.transferId?.let(java.util.UUID::fromString) ?: java.util.UUID(0, 0),
                    attachment.fileName, attachment.mimeType, attachment.sizeBytes, attachment.localUri,
                    attachment.state, attachment.previewUri)
            }
            ChatItem(java.util.UUID.fromString(message.messageId), message.content,
                message.direction == "OUTGOING", Instant.ofEpochMilli(message.createdAt),
                runCatching { MessageStatus.valueOf(message.status) }.getOrDefault(MessageStatus.RECEIVED),
                runCatching { ChatItemKind.valueOf(message.type) }.getOrDefault(ChatItemKind.TEXT), attachments)
        }
    }

    suspend fun loadTransfers(peerId: String): List<TransferItem> = database.transfers().loadForPeer(peerId).map { value ->
        TransferItem(java.util.UUID.fromString(value.transferId), value.fileName, value.totalBytes,
            value.completedBytes, value.direction == "OUTGOING",
            runCatching { TransferStatus.valueOf(value.status) }.getOrDefault(TransferStatus.FAILED),
            value.messageId?.let(java.util.UUID::fromString), mimeType = value.mimeType,
            localUri = value.localUri, failureDetail = value.failureDetail, peerId = value.peerId)
    }

    suspend fun trustedPeers(): List<PeerEntity> = database.peers().loadAll().filter { it.trustState == "TRUSTED" }

    suspend fun peerForTransport(address: String): PeerEntity? = database.peers().findByTransportAddress(address)

    suspend fun markConversationRead(peerId: String) = database.conversations().markRead(peerId)

    suspend fun clearChatHistory() = database.messages().deleteAll()

    suspend fun deleteMessage(messageId: java.util.UUID) = database.messages().delete(messageId.toString())

    suspend fun clearConversation(peerId: String) = database.messages().deleteConversation(conversationId(peerId))

    suspend fun clearTransferHistory() = database.transfers().deleteAll()

    suspend fun deleteTransfer(transferId: java.util.UUID) {
        database.transferExtents().deleteForTransfer(transferId.toString())
        database.transfers().delete(transferId.toString())
    }

    private suspend fun ensureConversation(peerId: String, peerName: String, transportAddress: String, now: Long) {
        val previous = database.peers().find(peerId)
        database.peers().upsert(PeerEntity(peerId, previous?.identityPublicKey, peerName,
            previous?.platform ?: "WINDOWS", previous?.trustState ?: "TRUSTED",
            previous?.createdAt ?: now, now, previous?.lastConnectedAt, transportAddress))
        if (database.conversations().findForPeer(peerId) == null)
            database.conversations().upsert(ConversationEntity(conversationId(peerId), peerId, now))
    }

    private fun conversationId(peerId: String) = "peer:${peerId.lowercase()}"

    private suspend fun ensureDefaults() {
        val existing = database.settings().loadAll().associateBy { it.key }
        encodeSettings(AppSettings()).filterNot { existing.containsKey(it.key) }
            .forEach { database.settings().upsert(it) }
    }

    private fun decodeSettings(values: List<AppSettingEntity>): AppSettings {
        val map = values.associate { it.key to it.value }
        val defaults = AppSettings()
        return AppSettings(
            autoConnectTrustedDevices = map.bool("auto_connect_trusted", defaults.autoConnectTrustedDevices),
            scanOnStartup = map.bool("scan_on_startup", defaults.scanOnStartup),
            keepBackgroundSessions = map.bool("keep_background_sessions", defaults.keepBackgroundSessions),
            maxConcurrentConnections = map.int("max_connections", defaults.maxConcurrentConnections).coerceIn(1, 8),
            autoDownloadFiles = map.bool("auto_download_files", defaults.autoDownloadFiles),
            receiveSizeLimitEnabled = map.bool("receive_limit_enabled", defaults.receiveSizeLimitEnabled),
            receiveSizeLimitBytes = map.long("receive_limit_bytes", defaults.receiveSizeLimitBytes).coerceAtLeast(0),
            showImageThumbnails = map.bool("show_image_thumbnails", defaults.showImageThumbnails),
            saveChatHistory = map.bool("save_chat_history", defaults.saveChatHistory),
            saveTransferHistory = map.bool("save_transfer_history", defaults.saveTransferHistory),
            diagnosticsEnabled = map.bool("diagnostics_enabled", defaults.diagnosticsEnabled),
            retentionPeriod = map["retention_period"] ?: defaults.retentionPeriod,
            downloadDirectory = map["download_directory"] ?: defaults.downloadDirectory,
        )
    }

    private fun encodeSettings(value: AppSettings) = listOf(
        AppSettingEntity("auto_connect_trusted", value.autoConnectTrustedDevices.value()),
        AppSettingEntity("scan_on_startup", value.scanOnStartup.value()),
        AppSettingEntity("keep_background_sessions", value.keepBackgroundSessions.value()),
        AppSettingEntity("max_connections", value.maxConcurrentConnections.toString()),
        AppSettingEntity("auto_download_files", value.autoDownloadFiles.value()),
        AppSettingEntity("receive_limit_enabled", value.receiveSizeLimitEnabled.value()),
        AppSettingEntity("receive_limit_bytes", value.receiveSizeLimitBytes.toString()),
        AppSettingEntity("show_image_thumbnails", value.showImageThumbnails.value()),
        AppSettingEntity("save_chat_history", value.saveChatHistory.value()),
        AppSettingEntity("save_transfer_history", value.saveTransferHistory.value()),
        AppSettingEntity("diagnostics_enabled", value.diagnosticsEnabled.value()),
        AppSettingEntity("retention_period", value.retentionPeriod),
        AppSettingEntity("download_directory", value.downloadDirectory),
    )

    private fun Boolean.value() = if (this) "1" else "0"
    private fun Map<String, String>.bool(key: String, fallback: Boolean) = this[key]?.let { it == "1" || it.equals("true", true) } ?: fallback
    private fun Map<String, String>.int(key: String, fallback: Int) = this[key]?.toIntOrNull() ?: fallback
    private fun Map<String, String>.long(key: String, fallback: Long) = this[key]?.toLongOrNull() ?: fallback
}
