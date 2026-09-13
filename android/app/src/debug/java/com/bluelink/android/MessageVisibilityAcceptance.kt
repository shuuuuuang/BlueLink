package com.bluelink.android

import android.content.Intent
import android.os.Bundle
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.room.Room
import com.bluelink.android.data.local.*
import com.bluelink.android.domain.*
import com.bluelink.android.ui.conversation.ConversationScreen
import com.bluelink.android.ui.devices.DeviceColors
import kotlinx.coroutines.*
import java.util.UUID

/** Real UI visibility and lifecycle callbacks, with a separate Room database and no network events. */
@Composable
internal fun MessageVisibilityAcceptance(close: () -> Unit) {
    val context = LocalContext.current
    val database = remember { Room.inMemoryDatabaseBuilder(context.applicationContext, BlueLinkDatabase::class.java).build() }
    val repository = remember { BlueLinkRepository(database) }
    val scope = remember { CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate) }
    val queue = remember { MessagePersistenceQueue(scope) }
    val tracker = remember { ConversationReadTracker { peer -> queue.enqueue { repository.markConversationRead(peer) } } }
    val state = remember { ManagedSessionState(UUID.randomUUID(), "qa-visibility", "BlueLink QA PC", "qa:memory",
        ConnectionPhase.CONNECTED, "QA", System.currentTimeMillis()) }
    val summaries by repository.conversations.collectAsState(emptyList())
    val unread = summaries.firstOrNull { it.peerId == state.peerId }?.unreadCount ?: 0
    var messages by remember { mutableStateOf<List<ChatItem>>(emptyList()) }
    var visible by remember { mutableStateOf<String?>(null) }
    var showConversation by remember { mutableStateOf(true) }
    var requestedTab by remember { mutableStateOf<Int?>(1) }
    var backgroundUnread by remember { mutableIntStateOf(-1) }
    var delayed by remember { mutableStateOf(false) }
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    DisposableEffect(lifecycle) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) tracker.setResumed(true)
            if (event == Lifecycle.Event.ON_PAUSE) tracker.setResumed(false)
        }
        lifecycle.addObserver(observer)
        tracker.setResumed(lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED))
        onDispose { lifecycle.removeObserver(observer); tracker.setResumed(false); scope.cancel(); database.close() }
    }
    LaunchedEffect(Unit) {
        queue.run {
            repeat(3) { repository.saveChat(state, ChatItem(text = "BlueLink QA unread ${it + 1}", outgoing = false, status = MessageStatus.RECEIVED), unread = true) }
            messages = repository.loadHistory(state.peerId!!)
        }
    }
    val receive: (Boolean) -> Unit = { outgoing ->
        val message = ChatItem(text = "BlueLink QA ${if (outgoing) "outgoing" else "incoming"}", outgoing = outgoing,
            status = if (outgoing) MessageStatus.SENT else MessageStatus.RECEIVED)
        tracker.recordMessage(state.peerId, outgoing) { pending ->
            queue.enqueue {
                repository.saveChat(state, message, unread = pending)
                messages = repository.loadHistory(state.peerId!!)
            }
        }
    }
    Column(Modifier.fillMaxSize().background(DeviceColors.Canvas)) {
        Text("QA unread=$unread visible=${visible != null} background=$backgroundUnread", Modifier.padding(8.dp))
        Row {
            TextButton(onClick = { receive(false) }) { Text("QA receive") }
            TextButton(onClick = { receive(true) }) { Text("QA send") }
            TextButton(onClick = { showConversation = !showConversation }) { Text("QA page") }
        }
        Row {
            TextButton(enabled = !delayed, onClick = {
                delayed = true
                scope.launch {
                    delay(12000)
                    receive(false)
                    queue.run { backgroundUnread = database.conversations().findForPeer(state.peerId!!)?.unreadCount ?: -1 }
                    delayed = false
                }
            }) { Text("QA delayed") }
            TextButton(onClick = { context.startActivity(Intent(context, AcceptanceCoverActivity::class.java)) }) { Text("QA background") }
            TextButton(onClick = close) { Text("QA close") }
        }
        if (showConversation) ConversationScreen(Modifier.weight(1f), state.peerId, messages,
            ConnectionState(ConnectionPhase.CONNECTED), listOf(ConversationSummary(state.peerId!!, state.peerName,
                PeerPlatform.WINDOWS, DeviceAvailability.CONNECTED, unreadCount = unread)), emptyList(), true,
            "downloads://BlueLink", { showConversation = false }, {}, {}, {}, {}, {}, {}, {}, {}, {},
            requestedTab = requestedTab, tabRequestHandled = { requestedTab = null },
            messageListVisibilityChanged = { visible = it; tracker.setVisiblePeer(it) })
        else Text("BlueLink QA other page", Modifier.padding(16.dp))
    }
}

/** Keeps testing inside BlueLink while the original activity receives real pause/resume events. */
class AcceptanceCoverActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContent {
            com.bluelink.android.ui.theme.BlueLinkTheme {
                Column(Modifier.fillMaxSize().background(DeviceColors.Canvas).padding(24.dp)) {
                    Text("BlueLink QA lifecycle cover")
                    TextButton(onClick = { finish() }) { Text("QA return") }
                }
            }
        }
    }
}
