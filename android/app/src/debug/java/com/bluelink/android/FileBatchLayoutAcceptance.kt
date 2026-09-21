package com.bluelink.android

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import com.bluelink.android.domain.*
import com.bluelink.android.ui.files.*
import java.util.UUID

/** In-memory fixture: no user history changes, external sharing, or live transfers. */
@Composable
internal fun FileBatchLayoutAcceptance(close: () -> Unit) {
    var files by remember { mutableStateOf((0..119).map { index ->
        TransferItem(UUID(0, index.toLong() + 1), "QA-file-${index.toString().padStart(3, '0')}.txt", 128,
            completedBytes = 128, outgoing = true, status = TransferStatus.COMPLETED,
            localUri = "file:///qa-only.txt", peerId = "qa", startedAtEpochMs = 1_789_700_000_000L - index * 60_000L)
    }) }
    var shown by remember { mutableStateOf<TransferItem?>(null) }
    var selectFromMenu by remember { mutableStateOf<(() -> Unit)?>(null) }
    val operations = remember { object : FileBatchOperations {
        override suspend fun check(ids: List<UUID>, action: FileBatchAction) = ids.map {
            FileBatchPolicy.check(it, files.find { row -> row.id == it }, action, online = true, readable = true)
        }
        override suspend fun run(ids: List<UUID>, action: FileBatchAction) = FileBatchRunner.run(ids, { check(listOf(it), action).single() }) { id ->
            files = if (action == FileBatchAction.DELETE_RECORDS) files.filterNot { it.id == id }
            else files.map { if (it.id != id) it else it.copy(status = when (action) {
                FileBatchAction.RETRY -> TransferStatus.TRANSFERRING
                FileBatchAction.CANCEL -> TransferStatus.CANCELED
                else -> it.status
            }) }
        }
    } }
    Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding()) {
        Row {
            TextButton(onClick = close) { Text("QA Close") }
            listOf("Done" to TransferStatus.COMPLETED, "Fail" to TransferStatus.FAILED, "Active" to TransferStatus.TRANSFERRING).forEach { (label, status) ->
                TextButton(onClick = { files = files.map { if (it.id == UUID(0, 1)) it.copy(status = status) else it } }) { Text("QA $label") }
            }
        }
        FilesScreen(Modifier.weight(1f), files, emptyList(), receiveDirectory = "QA", openSettings = {}, open = {},
            more = { shown = it }, batch = operations, moreWithSelection = { item, select -> shown = item; selectFromMenu = select })
    }
    shown?.let { TransferActionSheet(it, true, { shown = null; selectFromMenu = null }, selectMultiple = selectFromMenu) {} }
}
