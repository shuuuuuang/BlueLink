package com.bluelink.android

import android.content.Context
import android.content.ContextWrapper
import android.content.SharedPreferences
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.background
import com.bluelink.android.ui.devices.DeviceColors
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.room.Room
import androidx.room.withTransaction
import com.bluelink.android.data.IdentityStore
import com.bluelink.android.data.hex
import com.bluelink.android.data.local.*
import com.bluelink.android.domain.*
import com.bluelink.android.session.SecureConnectionHandshake
import com.bluelink.android.session.TrustHandshakeException
import com.bluelink.android.ui.components.SecurityPrompt
import com.bluelink.core.*
import kotlinx.coroutines.*
import java.io.PipedInputStream
import java.io.PipedOutputStream
import java.util.UUID

/** Real Android crypto, preferences and Room, scoped to disposable QA identities and an in-memory database. */
@Composable
internal fun IdentityAssociationAcceptance(close: () -> Unit) {
    val context = LocalContext.current
    var request by remember { mutableStateOf<SecurityRequest?>(null) }
    var result by remember { mutableStateOf("RUNNING") }
    LaunchedEffect(Unit) {
        result = try { verifyIdentityAssociation(context) { request = it } }
        catch (failure: Exception) {
            android.util.Log.e("BlueLinkIdentityAcceptance", "Isolated identity acceptance failed", failure)
            "FAILED: ${failure.javaClass.simpleName}: ${failure.message}"
        }
    }
    Column(Modifier.fillMaxSize().background(DeviceColors.Canvas).statusBarsPadding().navigationBarsPadding().padding(24.dp)) {
        Text("BlueLink · 身份关联验收", style = MaterialTheme.typography.titleLarge)
        Text(result)
        TextButton(onClick = close) { Text("QA Close") }
    }
    request?.let { current -> SecurityPrompt(current, { current.confirm() }, { current.cancel(); request = null }, {}, {}) }
}

private suspend fun verifyIdentityAssociation(context: Context, present: (SecurityRequest) -> Unit): String = withContext(Dispatchers.IO) {
    CryptoRuntime.preferProvider(org.conscrypt.Conscrypt.newProvider())
    val suffix = UUID.randomUUID().toString().replace("-", "")
    fun isolated(label: String) = object : ContextWrapper(context.applicationContext) {
        override fun getSharedPreferences(name: String, mode: Int): SharedPreferences =
            context.getSharedPreferences("bluelink_identity_qa_${suffix}_$label", mode)
    }
    val leftContext = isolated("left")
    val left = IdentityStore(leftContext)
    val right = IdentityStore(isolated("right"))
    val old = DeviceIdentity.generate()
    val oldId = old.peerId().hex(); val newId = right.identity.peerId().hex()
    left.completeTrustVerification(left.beginTrustVerification(old.peerId(), old.publicKey(), left.captureIdentity().trustEpoch))
    val database = Room.inMemoryDatabaseBuilder(context.applicationContext, BlueLinkDatabase::class.java).build()
    val repository = BlueLinkRepository(database)
    val aInput = PipedInputStream(65536); val bOutput = PipedOutputStream(aInput)
    val bInput = PipedInputStream(65536); val aOutput = PipedOutputStream(bInput)
    val closePipes = { runCatching { aInput.close() }; runCatching { aOutput.close() }; runCatching { bInput.close() }; runCatching { bOutput.close() }; Unit }
    try {
        repository.initialize(left)
        val now = System.currentTimeMillis()
        val peer = PeerEntity(oldId, old.publicKey(), "原电脑备注", "WINDOWS", "TRUSTED", now - 1000, now,
            transportAddress = "AA:BB:CC:DD:EE:FF")
        database.peers().upsert(peer)
        val hint = PeerIdentityHint.bluetooth(peer.transportAddress)!!
        database.peerHints().upsert(PeerHintEntity(oldId, hint))
        val conversation = "peer:$oldId"
        database.conversations().upsert(ConversationEntity(conversation, oldId, now, 3, "保留的草稿"))
        val message = UUID.randomUUID().toString(); val queued = UUID.randomUUID().toString()
        val transfer = UUID.randomUUID().toString(); val attachment = UUID.randomUUID().toString()
        database.messages().upsert(MessageEntity(message, conversation, oldId, "INCOMING", "FILE", "原消息", "READ", now, now))
        database.messages().upsert(MessageEntity(queued, conversation, oldId, "OUTGOING", "TEXT", "未发送", "LOCAL_QUEUED", now, now))
        database.attachments().upsert(AttachmentEntity(attachment, message, transfer, "原文件.png", "image/png", 99,
            byteArrayOf(1,2), "qa:kept-file", "qa:kept-preview", "COMPLETED"))
        database.transfers().upsert(TransferEntity(transfer, oldId, message, "INCOMING", "COMPLETED", "原文件.png", "image/png", 99, 99,
            localUri = "qa:kept-file", createdAt = now, updatedAt = now))
        val unfinished = UUID.randomUUID().toString()
        database.transfers().upsert(TransferEntity(unfinished, oldId, null, "OUTGOING", "PAUSED", "未完成.bin", "application/octet-stream", 100, 25,
            snapshotPath = "qa:snapshot", createdAt = now, updatedAt = now))
        check(repository.findIdentityCandidate(newId, hint)?.peerId == oldId)
        check(repository.findIdentityCandidate(newId, null) == null)
        val duplicateId = "qa-duplicate"
        database.peers().upsert(peer.copy(peerId = duplicateId))
        check(repository.findIdentityCandidate(newId, hint) == null)
        database.peers().upsert(peer.copy(peerId = duplicateId, trustState = "RETIRED"))
        // A failed SQLite transaction must preserve every original relation.
        runCatching { database.withTransaction { repository.movePeerHistory(oldId, newId, right.identity.publicKey()); error("rollback probe") } }
        check(database.peers().loadAll().single().peerId == oldId)
        check(repository.loadHistory(oldId).size == 2)
        val localPrompt = CompletableDeferred<SecurityRequest>()
        val results = supervisorScope {
            fun engine(local: IdentityStore, input: PipedInputStream, output: PipedOutputStream, listener: Boolean) = SecureConnectionHandshake(
                input, output, closePipes, listener, local::captureIdentity, local::trustedKey,
                { if (listener) emptyList() else listOf(old.publicKey()) }, local::beginTrustVerification, local::completeTrustVerification,
                if (listener) "验收手机" else "原电脑备注", UUID.randomUUID(), "AA:BB:CC:DD:EE:FF", PeerPlatform.WINDOWS,
                { prompt ->
                    if (!listener) { localPrompt.complete(prompt); present(prompt) }
                    else launch { check(prompt.safetyCode == localPrompt.await().safetyCode); prompt.confirm() }
                }, {}, {}, timeoutMs = 90_000,
                findCandidate = { if (listener) null else repository.findIdentityCandidate(it, hint) },
                commitAssociation = { verification, candidate -> local.associateIdentity(verification, candidate) },
                applyAssociation = {
                    // Reopen durable preferences before migrating, as after a process interruption.
                    val restarted = IdentityStore(leftContext)
                    check(restarted.identityAssociations()[oldId] == newId)
                    check(restarted.isRetired(oldId) && restarted.trustedKey(old.peerId()) == null)
                    repository.applyIdentityAssociations(restarted)
                    repository.applyIdentityAssociations(restarted)
                })
            val a = async { runCatching { engine(left, aInput, aOutput, false).run() } }
            val b = async { runCatching { engine(right, bInput, bOutput, true).run() } }
            a.await() to b.await()
        }
        if (results.first.exceptionOrNull() is TrustHandshakeException &&
            (results.first.exceptionOrNull() as TrustHandshakeException).stage == TrustStage.CANCELED) {
            repository.recordDisconnectedSession(ManagedSessionState(UUID.randomUUID(), newId, "已取消的新身份", "AA:BB:CC:DD:EE:FF",
                ConnectionPhase.DISCONNECTED, "已取消", now))
            check(left.identityAssociations().isEmpty() && left.trustedKey(old.peerId()) != null)
            check(database.peers().loadAll().single().peerId == oldId && repository.loadHistory(oldId).size == 2)
            "PASSED: cancel preserves original trust and history"
        } else {
            results.first.getOrThrow(); results.second.getOrThrow()
            repository.recordConnectedSession(ManagedSessionState(UUID.randomUUID(), newId, "新连接广播名", "AA:BB:CC:DD:EE:FF",
                ConnectionPhase.CONNECTED, "", now, identityHint = hint), left)
            check(database.peers().loadAll().single().let { it.peerId == newId && it.displayName == "原电脑备注" && it.createdAt == peer.createdAt })
            val saved = database.conversations().loadAll().single()
            check(saved.peerId == newId && saved.unreadCount == 3 && saved.draft == "保留的草稿")
            check(repository.loadHistory(newId).size == 2)
            check(database.messages().find(queued)?.status == "FAILED")
            check(database.attachments().find(attachment)?.let { it.localUri == "qa:kept-file" && it.previewUri == "qa:kept-preview" && it.sha256!!.contentEquals(byteArrayOf(1,2)) } == true)
            check(database.transfers().find(transfer)?.let { it.peerId == newId && it.status == "COMPLETED" } == true)
            check(database.transfers().find(unfinished)?.let { it.peerId == newId && it.status == "FAILED" && it.completedBytes == 25L } == true)
            // Late callbacks from the retired identity cannot recreate its conversation or peer card.
            repository.saveChat(ManagedSessionState(UUID.randomUUID(), oldId, "旧身份", "AA:BB:CC:DD:EE:FF", ConnectionPhase.DISCONNECTED, "", now),
                ChatItem(text = "late callback", outgoing = true, status = MessageStatus.LOCAL_QUEUED))
            check(database.conversations().loadAll().size == 1 && database.peers().loadAll().size == 1)
            check(runCatching { left.beginTrustVerification(old.peerId(), old.publicKey(), left.captureIdentity().trustEpoch) }.isFailure)
            "PASSED: fresh encrypted handshake, single device, history/files/draft/unread preserved, pending work stopped, restart recovery and late-callback guard"
        }
    } finally {
        closePipes(); database.close()
        context.deleteSharedPreferences("bluelink_identity_qa_${suffix}_left")
        context.deleteSharedPreferences("bluelink_identity_qa_${suffix}_right")
    }
}
