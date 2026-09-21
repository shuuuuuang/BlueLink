package com.bluelink.android.session

import com.bluelink.core.ChatEnvelope
import com.bluelink.core.ChatPayloadKind
import com.bluelink.core.MessagePayloadCodec
import kotlinx.coroutines.*
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean

internal object ChatSubmission {
    fun payload(id: UUID,text: String,structured: Boolean,trim: Boolean): ByteArray {
        val body=if(trim) text.trim() else text
        return if(structured) MessagePayloadCodec.encode(ChatEnvelope(id,ChatPayloadKind.TEXT,System.currentTimeMillis(),body,emptyList()))
            else body.toByteArray(Charsets.UTF_8)
    }
    fun start(scope: CoroutineScope,onSent:(Boolean)->Unit,submit:suspend ()->Unit):Job {
        val reported=AtomicBoolean()
        fun report(value:Boolean) {if(reported.compareAndSet(false,true)) onSent(value)}
        return scope.launch {
            try {submit();report(true)}
            catch(error:CancellationException) {report(false);throw error}
            catch(error:Exception) {report(false)}
        }.also {job->job.invokeOnCompletion {report(false)}}
    }
}
