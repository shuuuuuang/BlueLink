package com.bluelink.android.domain

import java.util.UUID
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive

enum class FileBatchAction { RETRY, CANCEL, DELETE_RECORDS, SHARE }
enum class FileBatchOutcome { SUBMITTED, SKIPPED, FAILED }
enum class FileBatchReason { MISSING, STATE_CHANGED, OFFLINE, SOURCE_UNREADABLE, ACTIVE, FAILED }
data class FileBatchCheck(val id: UUID, val name: String, val eligible: Boolean, val reason: FileBatchReason? = null)
data class FileBatchEntry(val id: UUID, val name: String, val outcome: FileBatchOutcome, val reason: FileBatchReason? = null)

interface FileBatchOperations {
    suspend fun check(ids: List<UUID>, action: FileBatchAction): List<FileBatchCheck>
    suspend fun run(ids: List<UUID>, action: FileBatchAction): List<FileBatchEntry>
}

object FileBatchPolicy {
    const val MAXIMUM_SELECTION = 100
    fun check(id: UUID, item: TransferItem?, action: FileBatchAction, online: Boolean, readable: Boolean): FileBatchCheck {
        val reason = when {
            item == null -> FileBatchReason.MISSING
            action == FileBatchAction.RETRY && (!item.outgoing || item.status !in setOf(TransferStatus.FAILED,TransferStatus.REJECTED,TransferStatus.CANCELED)) -> FileBatchReason.STATE_CHANGED
            (action == FileBatchAction.RETRY || action == FileBatchAction.CANCEL) && !online -> FileBatchReason.OFFLINE
            action == FileBatchAction.RETRY && !readable -> FileBatchReason.SOURCE_UNREADABLE
            action == FileBatchAction.CANCEL && item.status !in HistoryQuery.activeStatuses -> FileBatchReason.STATE_CHANGED
            action == FileBatchAction.DELETE_RECORDS && item.status in HistoryQuery.activeStatuses -> FileBatchReason.ACTIVE
            action == FileBatchAction.SHARE && (item.status != TransferStatus.COMPLETED || !readable) -> FileBatchReason.SOURCE_UNREADABLE
            else -> null
        }
        return FileBatchCheck(id,item?.name ?: id.toString(),reason == null,reason)
    }
}

object FileBatchRunner {
    suspend fun run(selection: List<UUID>, check: suspend (UUID)->FileBatchCheck, execute: suspend (UUID)->Unit): List<FileBatchEntry> {
        val ids=selection.distinct()
        require(ids.size <= FileBatchPolicy.MAXIMUM_SELECTION)
        return ids.map { id ->
            currentCoroutineContext().ensureActive()
            var name=id.toString()
            try {
                val current=check(id); name=current.name
                if(!current.eligible) FileBatchEntry(id,name,FileBatchOutcome.SKIPPED,current.reason)
                else { execute(id); FileBatchEntry(id,name,FileBatchOutcome.SUBMITTED) }
            } catch(cancelled: CancellationException) { currentCoroutineContext().ensureActive(); FileBatchEntry(id,name,FileBatchOutcome.FAILED,FileBatchReason.FAILED) }
            catch(error: Exception) { FileBatchEntry(id,name,FileBatchOutcome.FAILED,FileBatchReason.FAILED) }
        }
    }
}
