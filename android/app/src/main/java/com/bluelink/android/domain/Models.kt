package com.bluelink.android.domain

import com.bluelink.core.AttachmentRole

import java.time.Instant
import java.util.UUID

data class NearbyDevice(
    val name: String,
    val address: String,
    val bonded: Boolean,
    val discoveryId: String = "",
    val rssi: Short? = null,
    val platform: PeerPlatform = PeerPlatform.UNKNOWN,
    val rendezvousAvailable: Boolean = false,
    val connectable: Boolean = false,
    val lastSeenEpochMs: Long = System.currentTimeMillis(),
    val rendezvousLastSeenEpochMs: Long = 0L,
    val connectableLastSeenEpochMs: Long = 0L,
    val stableKey: String = "",
)

enum class PeerPlatform { ANDROID, WINDOWS, UNKNOWN }

/** Keeps device precedence rules outside Compose. */
object DeviceProjectionPolicy {
    fun availability(connected: Boolean, nearby: Boolean): DeviceAvailability = when {
        connected -> DeviceAvailability.CONNECTED
        nearby -> DeviceAvailability.CONNECTABLE
        else -> DeviceAvailability.OFFLINE
    }

    fun nearbyCandidates(
        devices: List<NearbyDevice>,
        activePeerIds: Set<String>,
        trustedPeerIds: Set<String>,
        activeAddresses: Set<String>,
        trustedAddresses: Set<String>,
    ): List<NearbyDevice> {
        val blockedIdentities = (activePeerIds + trustedPeerIds).mapNotNull(::normalizeIdentity).toSet()
        val blockedAddresses = (activeAddresses + trustedAddresses).mapNotNull(::normalizeAddress).toSet()
        return devices.filterNot { device ->
            normalizeIdentity(device.discoveryId)?.let(blockedIdentities::contains) == true ||
                normalizeAddress(device.address)?.let(blockedAddresses::contains) == true
        }
    }

    fun normalizeIdentity(value: String): String? {
        val compact = value.filter(Char::isLetterOrDigit).uppercase()
        if (compact.length < 12 || compact.any { it !in '0'..'9' && it !in 'A'..'F' }) return null
        return compact.take(12)
    }

    fun normalizeAddress(value: String): String? {
        val compact = value.filter(Char::isLetterOrDigit).uppercase()
        if (compact.length != 12 || compact.any { it !in '0'..'9' && it !in 'A'..'F' }) return null
        return compact
    }
}

data class DiscoveryState(
    val scanning: Boolean = false,
    val detail: String = "下拉刷新查找附近运行蓝联的设备",
)

enum class DiagnosticLevel { INFO, WARNING, ERROR }

data class DiagnosticEntry(
    val sequence: Long,
    val timestamp: Instant = Instant.now(),
    val level: DiagnosticLevel,
    val component: String,
    val message: String,
)

enum class ConnectionPhase { OFFLINE, NEARBY, CONNECTING, SECURE_HANDSHAKE, TRUST_REQUIRED, CONNECTED, DISCONNECTED }

data class ConnectionState(
    val phase: ConnectionPhase = ConnectionPhase.OFFLINE,
    val peerName: String? = null,
    val detail: String? = null,
    val transportAddress: String? = null,
    val transport: SessionTransport = SessionTransport.BLUETOOTH,
)

data class ManagedSessionState(
    val sessionId: UUID,
    val peerId: String? = null,
    val peerName: String,
    val transportAddress: String,
    val phase: ConnectionPhase,
    val detail: String,
    val startedAtEpochMs: Long,
    val transport: SessionTransport = SessionTransport.BLUETOOTH,
    val platform: PeerPlatform = PeerPlatform.UNKNOWN,
    val identityHint: String? = null,
    val hasPeerProvidedName: Boolean = false,
    val usbFileReady: Boolean = false,
)

enum class DeviceAvailability { CONNECTED, OFFLINE, CONNECTABLE }

data class ConversationSummary(
    val peerId: String,
    val peerName: String,
    val platform: PeerPlatform,
    val availability: DeviceAvailability,
    val sessionId: UUID? = null,
    val transportAddress: String = "",
    val unreadCount: Int = 0,
    val lastActivityAt: Long = 0,
    val lastConnectedAt: Long? = null,
    val isTrusted: Boolean = false,
    val usbReady: Boolean = false,
    val transport: SessionTransport = SessionTransport.BLUETOOTH,
    val isRemoved: Boolean = false,
    val localNote: String = "",
    val isPinned: Boolean = false,
) {
    val displayName: String get() = localNote.ifBlank { peerName }
}

data class TrustPrompt(
    val requestId: UUID = UUID.randomUUID(),
    val peerId: String,
    val peerName: String,
    val safetyCode: String,
    val connectionConfirmation: Boolean = false,
)

enum class MessageStatus { LOCAL_QUEUED, SENDING, SENT, DELIVERED, READ, RECEIVED, FAILED }
enum class ChatItemKind { TEXT, IMAGE, FILE, SYSTEM }

data class ChatAttachment(
    val attachmentId: UUID,
    val transferId: UUID,
    val fileName: String,
    val mimeType: String,
    val sizeBytes: Long,
    val localUri: String? = null,
    val state: String = "OFFERED",
    val previewUri: String? = null,
    val completedBytes: Long = 0,
    val bytesPerSecond: Double = 0.0,
    val recoveryPending: Boolean = false,
    val recoveryOutgoing: Boolean = true,
    val queuedForUsb: Boolean = false,
) {
    fun withTransfer(transfer: TransferItem?): ChatAttachment =
        if (transfer == null || transfer.id != transferId || transfer.role == AttachmentRole.IMAGE_PREVIEW) this
        else copy(localUri = transfer.localUri ?: localUri, state = transfer.status.name,
            completedBytes = transfer.completedBytes, bytesPerSecond = transfer.bytesPerSecond,
            recoveryPending = transfer.recoveryPending, recoveryOutgoing = transfer.outgoing, queuedForUsb = transfer.queuedForUsb)
    val isImage: Boolean get() = mimeType.startsWith("image/", ignoreCase = true)
    val isAvailable: Boolean get() = !localUri.isNullOrBlank()
    val canOpen: Boolean get() = state == "COMPLETED" && isAvailable
    // A verified preview can be displayed before the original is ready to open.
    val thumbnailUri: String? get() = previewUri?.takeIf { it.isNotBlank() }
        ?: localUri?.takeIf { canOpen && it.isNotBlank() }
    fun showsThumbnail(enabled: Boolean): Boolean = enabled && isImage && thumbnailUri != null
    val progress: Float get() = if (sizeBytes == 0L) 1f else
        (completedBytes.toFloat() / sizeBytes).coerceIn(0f, 1f)
    val isProgressIndeterminate: Boolean get() = state in setOf("OFFERED", "QUEUED", "VERIFYING", "COMMITTING")
    val isTransferActive: Boolean get() = state in setOf("OFFERED", "QUEUED", "TRANSFERRING",
        "PAUSED", "REMOTE_PAUSED", "RESUMING", "VERIFYING", "COMMITTING")
}

data class ChatItem(
    val id: UUID = UUID.randomUUID(),
    val text: String,
    val outgoing: Boolean,
    val timestamp: Instant = Instant.now(),
    val status: MessageStatus,
    val kind: ChatItemKind = ChatItemKind.TEXT,
    val attachments: List<ChatAttachment> = emptyList(),
)

enum class TransferStatus {
    OFFERED, QUEUED, TRANSFERRING, PAUSED, REMOTE_PAUSED, RESUMING, VERIFYING, COMMITTING,
    COMPLETED, REJECTED, FAILED, CANCELED
}

data class TransferItem(
    val id: UUID,
    val name: String,
    val totalBytes: Long,
    val completedBytes: Long = 0,
    val outgoing: Boolean,
    val status: TransferStatus,
    val messageId: UUID? = null,
    val attachmentId: UUID? = null,
    val mimeType: String = "application/octet-stream",
    val localUri: String? = null,
    val failureDetail: String? = null,
    val peerId: String? = null,
    val role: AttachmentRole = AttachmentRole.FILE,
    val startedAtEpochMs: Long = System.currentTimeMillis(),
    val updatedAtEpochMs: Long = System.currentTimeMillis(),
    val bytesPerSecond: Double = 0.0,
    val sourceSha256: String? = null,
    val attemptId: UUID? = null,
    val attemptSequence: Long = 0,
    val recoveryPending: Boolean = false,
    val queuedForUsb: Boolean = false,
) {
    val canSwitchToBluetooth: Boolean get() = outgoing && status == TransferStatus.QUEUED && queuedForUsb
    val isProgressIndeterminate: Boolean get() = status in setOf(TransferStatus.OFFERED, TransferStatus.QUEUED,
        TransferStatus.VERIFYING, TransferStatus.COMMITTING)
    val progress: Float get() = if (totalBytes == 0L) 1f else completedBytes.toFloat() / totalBytes
    val remainingSeconds: Long? get() = bytesPerSecond.takeIf { it > 0.0 && totalBytes > completedBytes }
        ?.let { ((totalBytes - completedBytes) / it).toLong().coerceAtLeast(0) }
}
