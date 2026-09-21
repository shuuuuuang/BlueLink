package com.bluelink.android.session
import org.junit.Test
import org.junit.Assert.*
import kotlinx.coroutines.*
import com.bluelink.core.MessagePayloadCodec
import java.util.UUID
class ChatSubmissionTest {
    @Test fun sharedTextPreservesExactWhitespaceAndUnicode() {
        val text="  内容 🌍\nend  "
        val id=UUID.randomUUID()
        assertEquals(text,MessagePayloadCodec.decode(ChatSubmission.payload(id,text,true,false)).body())
        assertEquals(text,String(ChatSubmission.payload(id,text,false,false),Charsets.UTF_8))
        assertEquals(text.trim(),MessagePayloadCodec.decode(ChatSubmission.payload(id,text,true,true)).body())
    }
    @Test fun successAndFailureReportExactlyOnce()=runBlocking {
        for(fail in listOf(false,true)) {
            val seen=mutableListOf<Boolean>();val scope=CoroutineScope(SupervisorJob()+Dispatchers.Unconfined)
            ChatSubmission.start(scope,{seen+=it}) {if(fail)error("write failed")}.join()
            assertEquals(listOf(!fail),seen);scope.cancel()
        }
    }
    @Test fun cancellationBeforeCoroutineStartsStillCompletesCallback()=runBlocking {
        val scope=CoroutineScope(SupervisorJob()+Dispatchers.Unconfined);scope.cancel()
        val seen=mutableListOf<Boolean>();ChatSubmission.start(scope,{seen+=it}) {error("must not write")}.join()
        assertEquals(listOf(false),seen)
    }
    @Test fun encodingFailureReturnsFailureWithoutHanging()=runBlocking {
        val scope=CoroutineScope(SupervisorJob()+Dispatchers.Unconfined);val seen=mutableListOf<Boolean>()
        ChatSubmission.start(scope,{seen+=it}) {ChatSubmission.payload(UUID.randomUUID(),"x".repeat(65537),true,false)}.join()
        assertEquals(listOf(false),seen);scope.cancel()
    }
}
