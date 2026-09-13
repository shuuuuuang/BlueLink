package com.bluelink.android

import android.content.Context
import androidx.room.Room
import com.bluelink.android.data.local.BlueLinkDatabase
import com.bluelink.android.data.local.BlueLinkRepository
import com.bluelink.android.data.local.MessagePersistenceQueue
import com.bluelink.android.domain.*
import com.bluelink.core.*
import kotlinx.coroutines.*
import java.util.UUID

/** Runs only on an explicitly selected debug scene, with a separate in-memory database. */
internal suspend fun verifyMessagePersistence(context: Context): List<ChatItem> = withContext(Dispatchers.IO) {
    val database = Room.inMemoryDatabaseBuilder(context.applicationContext, BlueLinkDatabase::class.java).build()
    try {
        val repository = BlueLinkRepository(database)
        val queue = MessagePersistenceQueue(this)
        val state = ManagedSessionState(UUID.randomUUID(), "qa-receipts", "BlueLink QA PC", "qa:memory",
            ConnectionPhase.CONNECTED, "QA", System.currentTimeMillis())
        val message = ChatItem(text = "QA: out-of-order receipts", outgoing = true, status = MessageStatus.SENDING)
        val inserted = CompletableDeferred<Unit>()
        val insert = queue.enqueue { inserted.await(); repository.saveChat(state, message) }
        val read = queue.enqueue { repository.updateMessageStatus(message.id.toString(), state.peerId!!, MessageStatus.READ) }
        val sent = queue.enqueue { repository.updateMessageStatus(message.id.toString(), state.peerId!!, MessageStatus.SENT) }
        inserted.complete(Unit)
        joinAll(insert, read, sent)
        check(repository.loadHistory(state.peerId!!).single().status == MessageStatus.READ)
        repository.saveChat(state, message)
        check(repository.loadHistory(state.peerId).single().status == MessageStatus.READ)

        val guarded = message.copy(id = UUID.randomUUID(), text = "QA: wrong peer ignored")
        repository.saveChat(state, guarded)
        repository.updateMessageStatus(guarded.id.toString(), "qa-other-peer", MessageStatus.READ)
        check(repository.loadHistory(state.peerId).first { it.id == guarded.id }.status == MessageStatus.SENDING)
        repository.updateMessageStatus(guarded.id.toString(), state.peerId, MessageStatus.DELIVERED)
        repository.updateMessageStatus(guarded.id.toString(), state.peerId, MessageStatus.FAILED)
        check(repository.loadHistory(state.peerId).first { it.id == guarded.id }.status == MessageStatus.DELIVERED)

        val received = message.copy(id = UUID.randomUUID(), text = "QA: database checks passed", outgoing = false,
            status = MessageStatus.RECEIVED)
        repository.saveChat(state, received, unread = true)
        repository.saveChat(state, received, unread = true)
        repository.updateMessageStatus(received.id.toString(), state.peerId, MessageStatus.READ)
        check(database.conversations().findForPeer(state.peerId)?.unreadCount == 1)
        check(repository.loadHistory(state.peerId).first { it.id == received.id }.status == MessageStatus.RECEIVED)

        val attachmentId = UUID.randomUUID()
        val transferId = UUID.randomUUID()
        val envelope = ChatEnvelope(UUID.randomUUID(), ChatPayloadKind.FILE, System.currentTimeMillis(), "",
            listOf(AttachmentDescriptor(attachmentId, transferId, AttachmentRole.FILE, "BlueLink-QA.pdf",
                "application/pdf", 10, ByteArray(32))))
        repository.saveEnvelope(state, envelope, outgoing = true)
        val original = database.attachments().find(attachmentId.toString())!!
        database.attachments().upsert(original.copy(state = "COMPLETED", localUri = "qa:memory-only"))
        repository.updateMessageStatus(envelope.messageId().toString(), state.peerId, MessageStatus.READ)
        repository.saveEnvelope(state, envelope, outgoing = true)
        val file = repository.loadHistory(state.peerId).first { it.id == envelope.messageId() }
        check(file.status == MessageStatus.READ && file.attachments.single().state == "COMPLETED")
        check(file.attachments.single().localUri == "qa:memory-only")

        // History must recover the persisted progress and pause origin instead of showing 0%/OFFERED.
        val eventTime = System.currentTimeMillis()
        val paused = TransferItem(transferId, "BlueLink-QA.pdf", 10, 7, true, TransferStatus.REMOTE_PAUSED,
            messageId = envelope.messageId(), attachmentId = attachmentId, updatedAtEpochMs = eventTime)
        repository.saveTransfer(state, paused)
        val reloaded = repository.loadHistory(state.peerId).first { it.id == envelope.messageId() }.attachments.single()
        check(reloaded.state == "REMOTE_PAUSED" && reloaded.completedBytes == 7L && reloaded.progress == .7f)
        repository.saveTransfer(state, paused.copy(status = TransferStatus.FAILED, updatedAtEpochMs = eventTime + 2))
        repository.saveTransfer(state, paused.copy(status = TransferStatus.TRANSFERRING, completedBytes = 2,
            updatedAtEpochMs = eventTime + 1))
        val failed = repository.loadHistory(state.peerId).first { it.id == envelope.messageId() }.attachments.single()
        check(failed.state == "FAILED" && failed.completedBytes == 7L)

        val delete = queue.enqueue { repository.deleteMessage(message.id) }
        val lateReceipt = queue.enqueue { repository.updateMessageStatus(message.id.toString(), state.peerId, MessageStatus.READ) }
        joinAll(delete, lateReceipt)
        check(repository.loadHistory(state.peerId).none { it.id == message.id })
        val visible = listOf(repository.loadHistory(state.peerId).first { it.id == received.id },
            message.copy(status = MessageStatus.READ), guarded.copy(status = MessageStatus.DELIVERED))
        repository.clearChatHistory()
        repository.updateMessageStatus(envelope.messageId().toString(), state.peerId, MessageStatus.READ)
        check(repository.loadHistory(state.peerId).isEmpty())
        visible
    } finally { database.close() }
}
