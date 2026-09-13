package com.bluelink.android.data.local

import androidx.room.withTransaction
import com.bluelink.android.data.IdentityStore
import com.bluelink.android.domain.ChatAttachment
import com.bluelink.android.domain.ChatItem
import com.bluelink.android.domain.ChatItemKind
import com.bluelink.android.domain.ManagedSessionState
import com.bluelink.android.domain.MessageDeliveryPolicy
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

class BlueLinkRepository(internal val database: BlueLinkDatabase) {
    val peers: Flow<List<PeerEntity>> = database.peers().observeAll()
    val conversations: Flow<List<ConversationEntity>> = database.conversations().observeAll()
    val transfers: Flow<List<TransferEntity>> = database.transfers().observeAll()
    val transferHistory: Flow<List<TransferItem>> = transfers.map { rows -> rows.map { it.toTransferItem() } }
    val trustedDevices: Flow<List<TrustEntity>> = database.trust().observeAll()
    val settings: Flow<AppSettings> = database.settings().observeAll().map(SettingsCodec::decode)

    suspend fun initialize(identityStore: IdentityStore) {
        database.withTransaction {
            ensureDefaults()
            applyIdentityAssociations(identityStore)
            database.peers().loadAll().forEach { peer ->
                com.bluelink.android.domain.PeerIdentityHint.fromAddress(peer.transportAddress)?.let {
                    database.peerHints().upsert(PeerHintEntity(peer.peerId, it))
                }
            }
            val now = System.currentTimeMillis()
            // Repair stale database projections after a process/disk interruption.
            synchronizeAllTrust(identityStore)
            identityStore.trustedEntries().forEach { (peerId, publicKey) ->
                val previous = database.peers().find(peerId)
                database.peers().upsert(
                    (previous ?: PeerEntity(peerId = peerId, createdAt = now, lastSeenAt = now)).copy(
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

    suspend fun trustedKeysAtAddress(address: String, identityStore: IdentityStore): List<ByteArray> {
        if (address.isBlank()) return emptyList()
        val trusted = identityStore.trustedEntries()
        return database.peers().loadAll().filter { it.transportAddress.equals(address, true) }
            .mapNotNull { peer -> trusted[peer.peerId.lowercase(java.util.Locale.ROOT)] }
    }

    suspend fun saveSettings(value: AppSettings) = database.withTransaction {
        SettingsCodec.encode(value).forEach { database.settings().upsert(it) }
    }

    suspend fun synchronizeTrust(peerId: String, identityStore: IdentityStore, removeFromDeviceList: Boolean = false) = database.withTransaction {
        if (identityStore.isRetired(peerId)) return@withTransaction
        val key = identityStore.trustedEntries().entries.firstOrNull { it.key.equals(peerId, true) }?.value
        val previous = database.peers().find(peerId)
        // A completed handshake can persist trust just before the process dies, before its peer row is written.
        if (previous != null || key != null) {
            val now = System.currentTimeMillis()
            database.peers().upsert((previous ?: PeerEntity(peerId, createdAt = now, lastSeenAt = now))
                .copy(identityPublicKey = key ?: previous?.identityPublicKey, trustState = when {
                    key != null -> "TRUSTED"
                    removeFromDeviceList || previous?.trustState == "REMOVED" -> "REMOVED"
                    else -> "UNKNOWN"
                }))
        }
        if (key == null) database.trust().delete(peerId)
        else database.trust().upsert(TrustEntity(peerId, key, System.currentTimeMillis()))
    }

    suspend fun synchronizeAllTrust(identityStore: IdentityStore, removeFromDeviceList: Boolean = false) = database.withTransaction {
        val peers = database.peers().loadAll().map { it.peerId }
        val storedTrust = database.trust().loadAll().map { it.peerId }
        (peers + storedTrust + identityStore.trustedEntries().keys).distinct().forEach { synchronizeTrust(it, identityStore, removeFromDeviceList) }
    }

    fun messages(conversationId: String): Flow<List<MessageEntity>> =
        database.messages().observeConversation(conversationId)

    suspend fun recordConnectedSession(state: ManagedSessionState, identityStore: IdentityStore) {
        val peerId = state.peerId ?: return
        if (identityStore.isRetired(peerId)) return
        val now = System.currentTimeMillis()
        database.withTransaction {
            val key = identityStore.trustedEntries().entries.firstOrNull { it.key.equals(peerId, true) }?.value
            val previous = database.peers().find(peerId)
            if (key == null && previous?.trustState == "REMOVED") return@withTransaction
            database.peers().upsert(PeerEntity(peerId, key ?: previous?.identityPublicKey,
                previous?.displayName?.takeIf { !state.hasPeerProvidedName && (state.transport == com.bluelink.android.domain.SessionTransport.USB || peerId in identityStore.identityAssociations().values) && it.isNotBlank() } ?: state.peerName,
                state.platform.takeIf { it != com.bluelink.android.domain.PeerPlatform.UNKNOWN }?.name ?: previous?.platform.orEmpty(), if (key == null) "UNKNOWN" else "TRUSTED", previous?.createdAt ?: now, now, now,
                com.bluelink.android.domain.SessionRoute.savedAddress(previous?.transportAddress, state.transportAddress)))
            if (key != null) state.identityHint?.let { database.peerHints().upsert(PeerHintEntity(peerId, it)) }
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
        val peer = database.peers().find(peerId) ?: return
        if (peer.trustState == "RETIRED") return
        val now = System.currentTimeMillis()
        database.sessions().upsert(SessionRecordEntity(state.sessionId.toString(), peerId,
            state.startedAtEpochMs, "DISCONNECTED", connectedAt = state.startedAtEpochMs,
            disconnectedAt = now, disconnectReason = state.detail))
    }

    suspend fun saveChat(state: ManagedSessionState, item: ChatItem, unread: Boolean = false) {
        val peerId = state.peerId ?: return
        val createdAt = item.timestamp.toEpochMilli()
        database.withTransaction {
            if (!ensureConversation(peerId, state.peerName, state.transportAddress, createdAt)) return@withTransaction
            val previous = database.messages().find(item.id.toString())
            if (previous != null && previous.peerId != peerId) return@withTransaction
            val status = mergeMessageStatus(previous, item.status)
            database.messages().upsert(MessageEntity(item.id.toString(), conversationId(peerId), peerId,
                if (item.outgoing) "OUTGOING" else "INCOMING", item.kind.name, item.text,
                status.name, createdAt, createdAt))
            val existing = database.conversations().findForPeer(peerId)
            if (existing != null) database.conversations().upsert(existing.copy(
                lastActivityAt = maxOf(existing.lastActivityAt, createdAt),
                unreadCount = existing.unreadCount + if (unread && previous == null) 1 else 0))
        }
    }

    suspend fun saveEnvelope(state: ManagedSessionState, envelope: ChatEnvelope, outgoing: Boolean,
                             unread: Boolean = false) {
        val peerId = state.peerId ?: return
        val createdAt = envelope.createdAt()
        database.withTransaction {
            if (!ensureConversation(peerId, state.peerName, state.transportAddress, createdAt)) return@withTransaction
            val type = when (envelope.kind()) {
                ChatPayloadKind.IMAGE -> "IMAGE"
                ChatPayloadKind.FILE -> "FILE"
                ChatPayloadKind.SYSTEM -> "SYSTEM"
                else -> "TEXT"
            }
            val previous = database.messages().find(envelope.messageId().toString())
            if (previous != null && previous.peerId != peerId) return@withTransaction
            val status = mergeMessageStatus(previous, if (outgoing) MessageStatus.SENDING else MessageStatus.RECEIVED)
            database.messages().upsert(MessageEntity(envelope.messageId().toString(), conversationId(peerId), peerId,
                if (outgoing) "OUTGOING" else "INCOMING", type, envelope.body(),
                status.name, createdAt, createdAt))
            val storedAttachments = if (envelope.kind() == ChatPayloadKind.IMAGE)
                envelope.attachments().filter { it.role() == AttachmentRole.IMAGE_ORIGINAL }
            else envelope.attachments().filter { it.role() == AttachmentRole.FILE }
            storedAttachments.forEach { attachment ->
                if (database.attachments().find(attachment.attachmentId().toString()) == null) {
                    database.attachments().upsert(AttachmentEntity(
                        attachment.attachmentId().toString(), envelope.messageId().toString(),
                        attachment.transferId().toString(), attachment.fileName(), attachment.mimeType(),
                        attachment.size(), attachment.sha256(), state = "OFFERED"))
                }
            }
            val existing = database.conversations().findForPeer(peerId)
            if (existing != null) database.conversations().upsert(existing.copy(
                lastActivityAt = maxOf(existing.lastActivityAt, createdAt),
                unreadCount = existing.unreadCount + if (unread && previous == null) 1 else 0))
        }
    }

    suspend fun updateMessageStatus(messageId: String, peerId: String, status: MessageStatus) = database.withTransaction {
        val message = database.messages().find(messageId) ?: return@withTransaction
        if (message.peerId != peerId || message.direction != "OUTGOING") return@withTransaction
        database.messages().upsert(message.copy(status = mergeMessageStatus(message, status).name))
    }

    private fun mergeMessageStatus(previous: MessageEntity?, status: MessageStatus): MessageStatus {
        val current = previous?.status?.let { runCatching { MessageStatus.valueOf(it) }.getOrNull() } ?: return status
        return MessageDeliveryPolicy.merge(current, status)
    }

    suspend fun applyRetention(retention: String) {
        val cutoff = com.bluelink.android.domain.RecordRetention.cutoff(retention, System.currentTimeMillis()) ?: return
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
            if (!ensureConversation(peerId, state.peerName, state.transportAddress, now)) return@withTransaction
            val previous = database.transfers().find(value.id.toString())
            // Progress callbacks can reach Room out of order; keep the latest event and its original time.
            if (previous != null && previous.updatedAt > value.updatedAtEpochMs) return@withTransaction
            database.transfers().upsert(TransferEntity(value.id.toString(), peerId,
                value.messageId?.toString() ?: previous?.messageId,
                if (value.outgoing) "OUTGOING" else "INCOMING", value.status.name, value.name,
                value.mimeType.ifBlank { previous?.mimeType ?: "application/octet-stream" }, value.totalBytes,
                value.completedBytes, value.localUri ?: previous?.localUri, previous?.snapshotPath,
                previous?.sha256, if (value.status.name == "FAILED") "TRANSFER_FAILED" else null,
                value.failureDetail, previous?.createdAt ?: value.startedAtEpochMs, value.updatedAtEpochMs))
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
                val transfer = attachment.transferId?.let { database.transfers().find(it) }
                    ?.takeIf { it.peerId == message.peerId }
                val status = transfer?.status ?: attachment.state
                ChatAttachment(java.util.UUID.fromString(attachment.attachmentId),
                    attachment.transferId?.let(java.util.UUID::fromString) ?: java.util.UUID(0, 0),
                    attachment.fileName, attachment.mimeType, attachment.sizeBytes, transfer?.localUri ?: attachment.localUri,
                    status, attachment.previewUri,
                    completedBytes = transfer?.completedBytes ?: if (status == "COMPLETED") attachment.sizeBytes else 0)
            }
            ChatItem(java.util.UUID.fromString(message.messageId), message.content,
                message.direction == "OUTGOING", Instant.ofEpochMilli(message.createdAt),
                runCatching { MessageStatus.valueOf(message.status) }.getOrDefault(MessageStatus.RECEIVED),
                runCatching { ChatItemKind.valueOf(message.type) }.getOrDefault(ChatItemKind.TEXT), attachments)
        }
    }

    suspend fun loadTransfers(peerId: String): List<TransferItem> =
        database.transfers().loadForPeer(peerId).map { it.toTransferItem() }

    suspend fun trustedPeers(): List<PeerEntity> = database.peers().loadAll().filter { it.trustState == "TRUSTED" }

    suspend fun peerForTransport(address: String): PeerEntity? = database.peers().findByTransportAddress(address)

    suspend fun markConversationRead(peerId: String) = database.conversations().markRead(peerId)

    suspend fun clearChatHistory() = database.withTransaction {
        database.messages().deleteAll()
        database.conversations().markAllRead()
    }

    suspend fun deleteMessage(messageId: java.util.UUID) = database.messages().delete(messageId.toString())

    suspend fun clearConversation(peerId: String) = database.messages().deleteConversation(conversationId(peerId))

    suspend fun clearTransferHistory() = database.transfers().deleteAll()

    suspend fun deleteTransfer(transferId: java.util.UUID) {
        database.transferExtents().deleteForTransfer(transferId.toString())
        database.transfers().delete(transferId.toString())
    }

    private suspend fun ensureConversation(peerId: String, peerName: String, transportAddress: String, now: Long): Boolean {
        val previous = database.peers().find(peerId)
        if (previous?.trustState == "RETIRED") return false
        database.peers().upsert(PeerEntity(peerId, previous?.identityPublicKey,
            previous?.displayName?.takeIf { transportAddress.startsWith("usb:") && it.isNotBlank() } ?: peerName,
            previous?.platform ?: "WINDOWS", previous?.trustState ?: "TRUSTED",
            previous?.createdAt ?: now, now, previous?.lastConnectedAt, com.bluelink.android.domain.SessionRoute.savedAddress(previous?.transportAddress, transportAddress)))
        if (database.conversations().findForPeer(peerId) == null)
            database.conversations().upsert(ConversationEntity(conversationId(peerId), peerId, now))
        return true
    }

    private fun conversationId(peerId: String) = "peer:${peerId.lowercase()}"

    private suspend fun ensureDefaults() {
        val existing = database.settings().loadAll().associateBy { it.key }
        SettingsCodec.encode(AppSettings()).filterNot { existing.containsKey(it.key) }
            .forEach { database.settings().upsert(it) }
    }

}
