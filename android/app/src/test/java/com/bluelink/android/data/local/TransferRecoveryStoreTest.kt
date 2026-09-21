package com.bluelink.android.data.local

import com.bluelink.android.domain.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.Rule
import org.junit.rules.TemporaryFolder
import java.io.File
import java.io.IOException
import java.util.UUID

class TransferRecoveryStoreTest {
    @get:Rule val temporary=TemporaryFolder()
    private val owner=UUID.randomUUID()
    private fun item(outgoing:Boolean=true)=TransferItem(UUID.randomUUID(),"报告.bin",200,outgoing=outgoing,status=TransferStatus.QUEUED,
        peerId="peer-a",localUri="file:///owned/source.bin",sourceSha256="A".repeat(64),attemptId=if(outgoing) UUID.randomUUID() else null,
        attemptSequence=if(outgoing) 50 else 0,messageId=UUID.randomUUID(),attachmentId=UUID.randomUUID())
    @Test fun restartKeepsRecoveryWithoutHistoryAndNeverClaimsCompletion() {
        val root=temporary.newFolder();val store=TransferRecoveryStore(root);val value=item().copy(status=TransferStatus.COMMITTING,completedBytes=200)
        assertTrue(store.record(owner,1,"BLUETOOTH",value));assertFalse(store.dismiss(value.id))
        val restarted=TransferRecoveryStore(root);val restored=restarted.mergeHistory(emptyList()).single()
        assertTrue(restored.recoveryPending);assertEquals(TransferStatus.FAILED,restored.status)
        assertEquals(value.sourceSha256,restored.sourceSha256);assertEquals(value.messageId,restored.messageId);assertEquals(value.attachmentId,restored.attachmentId)
        assertNull(restored.attemptId);assertEquals(0L,restored.attemptSequence)
        assertEquals(1,restarted.mergeHistory(listOf(value)).size)
        assertTrue(restarted.record(UUID.randomUUID(),2,"BLUETOOTH",value.copy(status=TransferStatus.QUEUED,attemptId=UUID.randomUUID(),attemptSequence=1)))
    }
    @Test fun staleAttemptsAndWrongIdentityCannotOverrideCompletion() {
        val root=temporary.newFolder();val store=TransferRecoveryStore(root);val first=item()
        store.record(owner,1,"USB",first)
        val nextOwner=UUID.randomUUID();val next=first.copy(attemptId=UUID.randomUUID(),attemptSequence=51)
        assertTrue(store.record(nextOwner,2,"BLUETOOTH",next))
        assertFalse(store.record(owner,1,"USB",first.copy(status=TransferStatus.COMPLETED)))
        assertFalse(store.record(nextOwner,2,"BLUETOOTH",next.copy(peerId="peer-b")))
        assertTrue(store.record(nextOwner,2,"BLUETOOTH",next.copy(status=TransferStatus.COMPLETED)))
        assertFalse(store.record(nextOwner,2,"BLUETOOTH",next.copy(status=TransferStatus.TRANSFERRING)))
        val restart=TransferRecoveryStore(root)
        assertTrue(restart.mergeHistory(emptyList()).isEmpty())
        assertEquals(TransferStatus.COMPLETED,restart.mergeHistory(listOf(first)).single().status)
    }
    @Test fun receivingAndDismissalRetainExplicitOwnership() {
        val root=temporary.newFolder();val value=item(false);TransferRecoveryStore(root).record(owner,1,"USB",value)
        val restarted=TransferRecoveryStore(root);val incoming=restarted.mergeHistory(emptyList()).single()
        assertFalse(incoming.outgoing);assertTrue(incoming.recoveryPending)
        assertTrue(restarted.dismiss(value.id))
        assertTrue(TransferRecoveryStore(root).mergeHistory(listOf(value)).isEmpty())
    }
    @Test fun freshIncomingOffersSurvivePriorFailureAndExplicitRecordDeletion() {
        val root=temporary.newFolder(); val store=TransferRecoveryStore(root); val value=item(false).copy(status=TransferStatus.OFFERED)
        assertTrue(store.record(owner,1,"USB",value))
        assertTrue(store.record(owner,1,"USB",value.copy(status=TransferStatus.FAILED)))
        assertTrue(store.dismiss(value.id))
        assertTrue(store.record(owner,1,"USB",value))
        assertEquals(TransferStatus.OFFERED,store.mergeHistory(emptyList()).single().status)
        val deleted=TransferHistoryIndex().receive(value).forget(setOf(value.id))
        assertTrue(deleted.receive(value.copy(status=TransferStatus.COMPLETED)).items.isEmpty())
        assertEquals(value,deleted.admit(value).items[value.id])
    }
    @Test fun failedWritesDoNotAdvanceMemoryOrDestroyPreviousRecord() {
        val root=temporary.newFolder();val value=item()
        val broken=TransferRecoveryStore(root) { _,_->throw IOException("Injected full disk") }
        try { broken.record(owner,1,"BLUETOOTH",value);fail() } catch (_:IOException) { }
        assertTrue(broken.mergeHistory(emptyList()).isEmpty())
        TransferRecoveryStore(root).record(owner,1,"BLUETOOTH",value)
        val brokenDelete=TransferRecoveryStore(root) { _,_->throw IOException("Injected full disk") }
        try { brokenDelete.dismiss(value.id);fail() } catch (_:IOException) { }
        File(root,"ignored.json.new").writeText("incomplete replacement")
        assertTrue(TransferRecoveryStore(root).mergeHistory(emptyList()).single().recoveryPending)
    }
    @Test fun messageAttachmentProjectionTracksRecoveryAndLiveRetry() {
        val task=item(); val attachment=ChatAttachment(task.attachmentId!!, task.id, task.name, task.mimeType, task.totalBytes)
        val pending=attachment.withTransfer(task.copy(status=TransferStatus.FAILED,recoveryPending=true))
        assertTrue(pending.recoveryPending); assertFalse(pending.isTransferActive)
        assertTrue(pending.recoveryOutgoing)
        assertFalse(pending.withTransfer(task).recoveryPending)
        assertSame(pending,pending.withTransfer(item()))
        assertFalse(pending.withTransfer(task.copy(outgoing=false,recoveryPending=true)).recoveryOutgoing)
    }
    @Test fun confirmedIdentityAssociationIsIdempotentAndNeverStartsRecovery() {
        val root=temporary.newFolder(); val value=item(); val store=TransferRecoveryStore(root)
        store.record(owner,1,"USB",value)
        val aliases=mapOf("peer-a" to "peer-b", "peer-b" to "peer-c")
        try { store.associate(aliases); fail("active association") } catch (_:IllegalStateException) { }
        assertEquals("peer-a",store.mergeHistory(emptyList()).single().peerId)
        val restart=TransferRecoveryStore(root)
        restart.associate(aliases); restart.associate(aliases)
        val migrated=TransferRecoveryStore(root).mergeHistory(emptyList()).single()
        assertEquals("peer-c",migrated.peerId); assertTrue(migrated.recoveryPending)
        assertEquals(value.id,migrated.id); assertEquals(value.sourceSha256,migrated.sourceSha256); assertEquals(value.localUri,migrated.localUri)
        assertFalse(restart.record(owner,1,"USB",value))
        try { restart.associate(mapOf("peer-c" to "peer-d", "peer-d" to "peer-c")); fail("cycle") } catch (_:IllegalStateException) { }
        assertEquals("peer-c",TransferRecoveryStore(root).mergeHistory(emptyList()).single().peerId)
        val broken=TransferRecoveryStore(root) { _,_->throw IOException("Injected migration failure") }
        try { broken.associate(mapOf("peer-c" to "peer-d")); fail("write failure") } catch (_:IOException) { }
        assertEquals("peer-c",broken.mergeHistory(emptyList()).single().peerId)
    }
    @Test fun oldReceiverAttemptCannotOverwriteNewReceiverAfterRestart() {
        val store = TransferRecoveryStore(temporary.newFolder())
        val old = item(false).copy(attemptId = UUID.randomUUID(), attemptSequence = 1)
        assertTrue(store.record(owner, 1, "USB", old))
        assertTrue(store.record(owner, 1, "USB", old.copy(status = TransferStatus.CANCELED)))
        val fresh = old.copy(attemptId = UUID.randomUUID(), attemptSequence = 2)
        assertTrue(store.record(owner, 1, "USB", fresh))
        assertFalse(store.record(owner, 1, "USB", old.copy(status = TransferStatus.COMPLETED)))
        assertEquals(fresh.attemptId, store.mergeHistory(emptyList()).single().attemptId)
    }
    @Test fun retentionDoesNotDiscardPendingRecoveryOrLiveTasks() {
        val live=item().copy(updatedAtEpochMs=1)
        val recovery=item().copy(status=TransferStatus.FAILED,recoveryPending=true,updatedAtEpochMs=1)
        val done=item().copy(status=TransferStatus.COMPLETED,updatedAtEpochMs=1)
        val index=TransferHistoryIndex().restore(listOf(live,recovery,done)).retainSince(100)
        assertEquals(setOf(live.id,recovery.id),index.items.keys)
    }
}
