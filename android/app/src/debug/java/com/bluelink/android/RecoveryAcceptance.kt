package com.bluelink.android

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.bluelink.android.data.local.TransferRecoveryStore
import com.bluelink.android.domain.*
import com.bluelink.android.ui.files.*
import java.io.File
import java.util.UUID

/** Reopens real private-file metadata; isolated fixtures never connect or change user history. */
@Composable internal fun RecoveryAcceptance(close: () -> Unit) {
    val context=LocalContext.current
    val root=remember { File(context.cacheDir,"recovery-qa-"+UUID.randomUUID()) }
    val journal=remember {
        val writer=TransferRecoveryStore(root)
        for (outgoing in listOf(true,false)) writer.record(UUID.randomUUID(),1,"BLUETOOTH",TransferItem(UUID.randomUUID(),
            if(outgoing) "QA-待恢复的发送文件.bin" else "QA-等待发送方恢复.pdf",1024,1024,outgoing,TransferStatus.COMMITTING,
            peerId="qa-recovery",localUri=if(outgoing) "file:///qa-source.bin" else null,sourceSha256="A".repeat(64),
            attemptId=if(outgoing) UUID.randomUUID() else null,attemptSequence=if(outgoing) 1 else 0))
        TransferRecoveryStore(root)
    }
    var items by remember { mutableStateOf(journal.mergeHistory(emptyList()).also { values ->
        check(values.size==2 && values.all { it.recoveryPending && it.status==TransferStatus.FAILED })
        File(context.cacheDir,"p5-recovery-qa-result.txt").writeText("PASS: real Android atomic storage reopened; 2 pending; no network started")
    }) }
    val queued = remember { TransferItem(UUID.randomUUID(), "QA-USB-queued.bin", 2048, outgoing = true,
        status = TransferStatus.QUEUED, peerId = "qa-recovery", localUri = "file:///qa-source.bin", queuedForUsb = true) }
    var shown by remember { mutableStateOf<TransferItem?>(null) }
    var detail by remember { mutableStateOf<TransferItem?>(null) }
    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().padding(8.dp)) { Text("QA · P5 Recovery",Modifier.weight(1f));TextButton(onClick=close) { Text("Close") } }
        FilesScreen(Modifier.weight(1f),items + queued,listOf(ConversationSummary("qa-recovery","QA Windows",PeerPlatform.WINDOWS,DeviceAvailability.OFFLINE)),
            receiveDirectory="QA",openSettings={},open={shown=it},more={shown=it})
    }
    shown?.let { item -> TransferActionSheet(item,online=item.queuedForUsb,dismiss={shown=null}) { action ->
        shown=null
        when(action) {
            TransferAction.FAILURE,TransferAction.DETAILS -> detail=item
            TransferAction.DELETE -> { check(journal.dismiss(item.id));items=TransferRecoveryStore(root).mergeHistory(emptyList()) }
            else -> Unit
        }
    } }
    detail?.let { TransferFailurePrompt(it) { detail=null } }
}
