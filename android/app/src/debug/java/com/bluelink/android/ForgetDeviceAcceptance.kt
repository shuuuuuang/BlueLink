package com.bluelink.android

import android.content.Context
import android.content.ContextWrapper
import android.content.SharedPreferences
import androidx.compose.foundation.layout.*
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.room.Room
import com.bluelink.android.data.IdentityStore
import com.bluelink.android.data.hex
import com.bluelink.android.data.local.*
import com.bluelink.android.domain.*
import com.bluelink.android.ui.devices.*
import com.bluelink.core.DeviceIdentity
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.util.UUID

/** Scoped Room/keystore acceptance: never modifies the user's identity, trust or files. */
@Composable
internal fun ForgetDeviceAcceptance(close: () -> Unit) {
    val context = LocalContext.current
    var result by remember { mutableStateOf("RUNNING") }
    var peers by remember { mutableStateOf(emptyList<ConversationSummary>()) }
    val nearby = remember { NearbyDevice("QA Rediscovered PC", "AA:BB:CC:DD:EE:01", false,
        platform = PeerPlatform.WINDOWS, connectable = true, stableKey = "qa-removed") }
    LaunchedEffect(Unit) {
        try {
            peers = verifyForgetDevice(context)
            result = "PASSED: removal, restart, late callback, retrust, history, files, remove-all"
        } catch (failure: Exception) {
            android.util.Log.e("BlueLinkForgetAcceptance", "Failed", failure)
            result = "FAILED: ${failure.message}"
        }
    }
    Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding()) {
        Row(Modifier.padding(12.dp)) {
            Text(result, Modifier.weight(1f))
            TextButton(onClick = close) { Text("QA Close") }
        }
        DeviceHomeHeader(BluetoothAccessState.READY, 0)
        DevicesScreen(Modifier.weight(1f), listOf(nearby), peers, emptyList(), ConnectionState(),
            DiscoveryState(), BluetoothAccessState.READY, null, null, {}, {}, {}, {}, {}, {})
    }
}

private suspend fun verifyForgetDevice(context: Context): List<ConversationSummary> = withContext(Dispatchers.IO) {
    val suffix = UUID.randomUUID().toString()
    val preferences = "bluelink_qa_forget_$suffix"
    val dbName = "$preferences.db"
    val scopedContext = object : ContextWrapper(context.applicationContext) {
        override fun getSharedPreferences(name: String, mode: Int): SharedPreferences =
            context.getSharedPreferences(preferences, mode)
    }
    fun open() = Room.databaseBuilder(context.applicationContext, BlueLinkDatabase::class.java, dbName).build()
    var database = open()
    var identity = IdentityStore(scopedContext)
    var repository = BlueLinkRepository(database)
    val remote = DeviceIdentity.generate()
    val id = remote.peerId().hex()
    val messageId = UUID.randomUUID().toString()
    val transferId = UUID.randomUUID().toString()
    val now = System.currentTimeMillis()
    val file = File(context.cacheDir, "$preferences.txt").apply { writeText("retained file") }
    fun trust() = identity.completeTrustVerification(identity.beginTrustVerification(remote.peerId(), remote.publicKey(), identity.captureIdentity().trustEpoch))
    val session = ManagedSessionState(UUID.randomUUID(), id, "QA Rediscovered PC", "AA:BB:CC:DD:EE:01", ConnectionPhase.CONNECTED, "", now)
    try {
        trust(); repository.initialize(identity)
        repository.recordConnectedSession(session, identity)
        database.messages().upsert(MessageEntity(messageId, "peer:$id", id, "INCOMING", "TEXT", "retained history", "READ", now, now))
        database.transfers().upsert(TransferEntity(transferId, id, messageId, "INCOMING", "COMPLETED", file.name, "text/plain", 13, 13,
            localUri = file.toURI().toString(), createdAt = now, updatedAt = now))
        identity.removeTrust(id)
        repository.synchronizeTrust(id, identity, removeFromDeviceList = true)
        check(database.peers().find(id)?.trustState == "REMOVED")
        check(database.trust().loadAll().isEmpty())
        database.close(); database = open()
        identity = IdentityStore(scopedContext); repository = BlueLinkRepository(database)
        repository.initialize(identity)
        check(database.peers().find(id)?.trustState == "REMOVED")
        repository.recordConnectedSession(session, identity)
        repository.recordDisconnectedSession(session.copy(phase = ConnectionPhase.DISCONNECTED))
        check(database.peers().find(id)?.trustState == "REMOVED")
        check(repository.loadHistory(id).single().text == "retained history")
        check(database.transfers().find(transferId)?.localUri == file.toURI().toString())
        check(file.readText() == "retained file")
        trust(); repository.recordConnectedSession(session, identity)
        check(database.peers().find(id)?.trustState == "TRUSTED")
        check(repository.loadHistory(id).single().text == "retained history")
        identity.removeAllTrust(); repository.synchronizeAllTrust(identity, removeFromDeviceList = true)
        check(database.peers().find(id)?.trustState == "REMOVED")
        val removed = ConversationSummary(id, "QA Removed PC", PeerPlatform.WINDOWS, DeviceAvailability.OFFLINE,
            transportAddress = session.transportAddress, isRemoved = database.peers().find(id)?.trustState == "REMOVED")
        listOf(removed, ConversationSummary("qa-remaining", "QA Remaining Phone", PeerPlatform.ANDROID,
            DeviceAvailability.OFFLINE, lastConnectedAt = now))
    } finally {
        database.close(); context.deleteDatabase(dbName); context.deleteSharedPreferences(preferences); file.delete()
    }
}
