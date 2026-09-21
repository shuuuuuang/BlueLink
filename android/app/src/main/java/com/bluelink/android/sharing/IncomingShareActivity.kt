package com.bluelink.android.sharing

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.compose.BackHandler
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.platform.LocalContext
import com.bluelink.android.BlueLinkApplication
import com.bluelink.android.R
import com.bluelink.android.ui.components.BlueLinkConfirmation
import com.bluelink.android.ui.components.BlueLinkPrompt
import com.bluelink.android.ui.devices.DeviceScreenTheme
import com.bluelink.android.ui.theme.BlueLinkTheme
import kotlinx.coroutines.*
import java.util.UUID

/** A share is reviewed here; receiving an Intent alone never submits a transfer. */
class IncomingShareActivity : ComponentActivity() {
    private val app get()=application as BlueLinkApplication
    private var captureJob: Job?=null
    private var captureGeneration=0L
    private var activeId by mutableStateOf<UUID?>(null)
    private var preparing by mutableStateOf(false)
    private var progressName by mutableStateOf("")
    private var progressBytes by mutableLongStateOf(0L)
    private var failure by mutableStateOf<ShareInputException.Code?>(null)
    private var sendFailed by mutableStateOf(false)
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        activeId=savedInstanceState?.getString("request")?.let { runCatching {UUID.fromString(it)}.getOrNull() }
        if(activeId==null) receive(intent)
        setContent {
            val settings by app.runtime.settings.collectAsState()
            BlueLinkTheme(theme=settings.theme,language=settings.language) { DeviceScreenTheme { Review() } }
        }
    }
    override fun onStart() { super.onStart();app.clientStarted(this) }
    override fun onStop() { app.clientStopped(this);super.onStop() }
    override fun onNewIntent(intent: Intent) { super.onNewIntent(intent);setIntent(intent);receive(intent) }
    override fun onSaveInstanceState(outState: Bundle) { activeId?.let {outState.putString("request",it.toString())};super.onSaveInstanceState(outState) }
    override fun onDestroy() { captureJob?.cancel();super.onDestroy() }
    private fun receive(intent: Intent) {
        if(intent.action !in setOf(Intent.ACTION_SEND,Intent.ACTION_SEND_MULTIPLE)) return
        val generation=++captureGeneration
        captureJob?.cancel()
        preparing=true;failure=null
        captureJob=app.shareScope.launch {
            try {
                val id=app.shareInbox.capture(Intent(intent)) { name,bytes -> if(generation==captureGeneration) { progressName=name;progressBytes=bytes } }
                if(generation==captureGeneration) activeId=id
            } catch(cancelled: CancellationException) { throw cancelled }
            catch(error: Exception) { if(generation==captureGeneration) failure=(error as? ShareInputException)?.code ?: ShareInputException.Code.UNREADABLE }
            finally { if(generation==captureGeneration) preparing=false }
        }
    }
    @OptIn(ExperimentalMaterial3Api::class)
    @Composable private fun Review() {
        val context=LocalContext.current
        val requests by app.shareInbox.requests.collectAsState()
        val sending by app.shareInbox.sending.collectAsState()
        val peers by app.runtime.conversations.collectAsState()
        val request=if(preparing || failure!=null) null else requests.firstOrNull {it.id==activeId} ?: requests.firstOrNull { !it.complete }
        var selectedPeer by remember(request?.id,request?.peerId) { mutableStateOf(request?.peerId) }
        var discard by remember {mutableStateOf(false)}
        val busy=preparing || sending!=null
        // Observe the durable whole-request outcome, including completion during recreation.
        // Partial handoff stays here so failed items can be retried without resending successes.
        LaunchedEffect(request?.id, request?.completedPeerId, busy) {
            val peer = request?.completedPeerId
            if (!busy && peer != null) {
                startActivity(Intent(this@IncomingShareActivity, com.bluelink.android.MainActivity::class.java)
                    .addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
                    .putExtra(com.bluelink.android.service.BlueLinkNotifications.OPEN_PEER, peer))
                finish()
            }
        }
        BackHandler { if(!busy) finish() }
        IncomingShareReview(request, requests, peers.filter { it.isTrusted && !it.isRemoved }, selectedPeer,
            busy, preparing, sending == request?.id && sending != null, progressName, progressBytes,
            selectRequest = { activeId = it }, selectPeer = { selectedPeer = it },
            close = { if (!busy) finish() }, cancelPreparation = { captureJob?.cancel(); finish() },
            discard = { discard = true },
            keep = {
                request?.let { current -> app.shareScope.launch {
                    try { selectedPeer?.let { app.shareInbox.setTarget(current.id,it) }; withContext(Dispatchers.Main) { finish() } }
                    catch (error: Exception) { if (error is CancellationException) throw error; sendFailed = true }
                } }
            },
            send = {
                val current = request
                val target = selectedPeer
                if (current != null && target != null) app.shareScope.launch {
                    try { if (!app.shareInbox.send(current.id,target,app.runtime)) sendFailed = true }
                    catch (error: Exception) { if (error is CancellationException) throw error; sendFailed = true }
                }
            })
        if(discard && request!=null) BlueLinkConfirmation(context.getString(R.string.share_discard),context.getString(R.string.share_discard_note),null,
            context.getString(R.string.share_discard),{discard=false}) {
            discard=false
            app.shareScope.launch { try {app.shareInbox.discard(request.id)} catch(error:Exception) {sendFailed=true} }
        }
        failure?.let { code->BlueLinkPrompt(context.getString(R.string.share_receive_failed),{finish()}) {
            Text(context.getString(when(code) {
                ShareInputException.Code.UNSUPPORTED->R.string.share_unsupported
                ShareInputException.Code.TOO_MANY->R.string.batch_selection_limit
                ShareInputException.Code.TOO_LARGE->R.string.share_too_large
                ShareInputException.Code.NO_SPACE->R.string.share_no_space
                else->R.string.share_unreadable
            }))
        } }
        if(sendFailed) BlueLinkPrompt(context.getString(R.string.batch_failed),{sendFailed=false}) {Text(context.getString(R.string.share_send_failed))}
    }
}
