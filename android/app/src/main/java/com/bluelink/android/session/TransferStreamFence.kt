package com.bluelink.android.session

import com.bluelink.android.domain.HistoryQuery
import com.bluelink.android.domain.TransferItem
import kotlinx.coroutines.asContextElement
import java.io.IOException
import java.util.UUID

/** Uses authenticated record stream IDs to distinguish attempts without changing task IDs. */
internal class TransferStreamFence {
    data class Binding(val task: UUID, val stream: Int, val outgoing: Boolean,
        val attempt: UUID = UUID.randomUUID(), val sequence: Long = TransferAttemptRegistry.nextSequence(), var terminal: Boolean = false)
    private val current = mutableMapOf<UUID, Binding>()
    private val used = mutableSetOf<Int>()
    private val local = ThreadLocal<Binding?>()
    private var next = 16
    fun context(binding: Binding? = local.get()) = local.asContextElement(binding)
    @Synchronized fun contextFor(task: UUID) = context(local.get()?.takeIf { it.task == task } ?: current[task])
    @Synchronized fun startOutgoing(task: UUID, enabled: Boolean, listener: Boolean): Binding {
        if (!enabled && task in current) throw IOException("对端版本不支持安全重试，请重新连接后重试。")
        if (current.size >= 8192 || used.size >= 16384) throw IOException("传输会话已达到容量限制，请重新连接。")
        val stream = if (enabled) { next += 2; next + if (listener) 0 else 1 } else 2
        return Binding(task,stream,true).also { current[task]=it; if(enabled) used.add(stream) }
    }
    @Synchronized fun receive(task: UUID, stream: Int, starts: Boolean, busy: Boolean, enabled: Boolean, listener: Boolean): Binding? {
        val existing=current[task]
        if(existing != null && (!enabled || existing.stream == stream)) {
            if(starts && (existing.outgoing || existing.terminal)) return null
            return existing
        }
        if(!starts || busy || current.size >= 8192 || used.size >= 16384) return null
        if(enabled && (stream < 16 || (stream and 1) == (if(listener) 0 else 1) || !used.add(stream))) return null
        return Binding(task,stream,false).also { current[task]=it }
    }
    @Synchronized fun streamFor(task: UUID, fallback: Int, enabled: Boolean): Int {
        if(!enabled) return fallback
        return (local.get()?.takeIf { it.task == task } ?: current[task])?.stream
            ?: throw IOException("Transfer has no authenticated attempt stream")
    }
    @Synchronized fun stamp(item: TransferItem): TransferItem {
        val binding=local.get()?.takeIf { it.task == item.id } ?: current[item.id] ?: return item
        if(item.status !in HistoryQuery.activeStatuses) binding.terminal=true
        return if(binding.outgoing) item else item.copy(attemptId=binding.attempt,attemptSequence=binding.sequence)
    }
}
