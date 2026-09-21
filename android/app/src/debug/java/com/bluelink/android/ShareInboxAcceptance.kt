package com.bluelink.android
import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import com.bluelink.android.sharing.*
import kotlinx.coroutines.*
import java.io.File
import java.util.UUID

/** Isolated inbox and QA sources; never invokes a session or sends to any device. */
@Composable
internal fun ShareInboxAcceptance(close:()->Unit) {
    val context=LocalContext.current
    var report by remember {mutableStateOf("QA running inbox regressions")}
    val plain=remember {Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT,"BlueLink QA incoming · explicit confirmation only")}
    LaunchedEffect(Unit) {
        try {
            val root=File(context.filesDir,"qa-inbox-"+UUID.randomUUID());var checks=0
            fun verify(value:Boolean) {check(value);checks++}
            val store=ShareInbox(context,root)
            val first=store.capture(plain);verify(store.capture(plain)==first && store.requests.value.size==1)
            val duplicate=coroutineScope {listOf(async {store.capture(plain)},async {store.capture(plain)}).awaitAll()}
            verify(duplicate.all {it==first})
            store.setTarget(first,"qa-offline")
            val reopened=ShareInbox(context,root);reopened.load()
            verify(reopened.requests.value.single().text==plain.getStringExtra(Intent.EXTRA_TEXT))
            verify(reopened.requests.value.single().peerId=="qa-offline" && !reopened.requests.value.single().textSubmitted)
            fun input(name:String)=Intent(Intent.ACTION_SEND).setType("application/pdf").putExtra(Intent.EXTRA_STREAM,Uri.parse("content://${context.packageName}.qa-share-input/$name"))
            val fileId=store.capture(input("unknown"));val request=store.requests.value.first {it.id==fileId};val item=request.files.single()
            verify(item.size==256*1024L && item.name=="QA-unknown-size.pdf")
            verify(store.source(fileId,item).length()==item.size)
            val original=File(context.cacheDir,"qa-share-input.bin").readBytes()
            verify(store.capture(input("unknown"))==fileId)
            val qaSource=File(context.cacheDir,"qa-share-input.bin")
            val changedId=try {
                qaSource.writeBytes(original.copyOf().also {it[0]=(it[0]+1).toByte()})
                store.capture(input("unknown"))
            } finally {qaSource.writeBytes(original)}
            verify(changedId!=fileId)
            verify(store.requests.value.first {it.id==changedId}.files.single().sha256!=item.sha256)
            store.discard(changedId)

            val batchRoot=File(root,"partial-handoff")
            val batch=ShareInbox(context,batchRoot)
            val multiple=Intent(Intent.ACTION_SEND_MULTIPLE).setType("application/pdf").putParcelableArrayListExtra(
                Intent.EXTRA_STREAM,arrayListOf(Uri.parse("content://${context.packageName}.qa-share-input/one"),
                    Uri.parse("content://${context.packageName}.qa-share-input/two")))
            val batchId=batch.capture(multiple)
            val batchFiles=batch.requests.value.single().files
            val calls=mutableListOf<UUID>()
            var rejectSecond=true
            val sender=object:ShareInbox.Sender {
                override suspend fun text(peerId:String,id:UUID,text:String)=error("No text expected")
                override suspend fun file(peerId:String,id:UUID,source:Uri,name:String,size:Long):Boolean {
                    verify(peerId=="qa-target" && File(requireNotNull(source.path)).length()==size)
                    calls+=id;return !(rejectSecond && id==batchFiles[1].id)
                }
            }
            verify(!batch.send(batchId,"qa-target",sender))
            verify(batch.requests.value.single().files.map {it.submitted}==listOf(true,false))
            val resumed=ShareInbox(context,batchRoot);resumed.load()
            try {resumed.setTarget(batchId,"wrong-target");error("Partial handoff target changed")}
            catch(expected:IllegalArgumentException) {verify(true)}
            rejectSecond=false;verify(resumed.send(batchId,"qa-target",sender))
            verify(calls==listOf(batchFiles[0].id,batchFiles[1].id,batchFiles[1].id))
            verify(resumed.requests.value.single().complete)
            val repeated=resumed.capture(multiple);verify(repeated!=batchId)
            resumed.discard(batchId);resumed.discard(repeated)

            val blocked=ShareInbox(context,File(root,"send-lock"));val blockedId=blocked.capture(plain)
            val entered=CompletableDeferred<Unit>();val release=CompletableDeferred<Unit>()
            val waiting=object:ShareInbox.Sender {
                override suspend fun text(peerId:String,id:UUID,text:String):Boolean {entered.complete(Unit);release.await();return false}
                override suspend fun file(peerId:String,id:UUID,source:Uri,name:String,size:Long)=error("No file expected")
            }
            coroutineScope {
                val job=async {blocked.send(blockedId,"qa-target",waiting)};entered.await()
                verify(!blocked.send(blockedId,"qa-target",waiting))
                try {blocked.discard(blockedId);error("Discarded active handoff")}
                catch(expected:IllegalStateException) {verify(true)}
                release.complete(Unit);verify(!job.await())
            }
            verify(!blocked.requests.value.single().textSubmitted && blocked.sending.value==null)
            blocked.discard(blockedId)
            for((intent,code) in listOf(input("denied") to ShareInputException.Code.UNREADABLE,
                input("bad-name") to ShareInputException.Code.UNSUPPORTED,input("too-large") to ShareInputException.Code.TOO_LARGE,
                Intent(Intent.ACTION_SEND).setType("application/zip").putExtra(Intent.EXTRA_TEXT,"bad MIME") to ShareInputException.Code.UNSUPPORTED,
                Intent(plain).putExtra(Intent.EXTRA_TEXT,"x".repeat(ShareInbox.MAX_TEXT_BYTES+1)) to ShareInputException.Code.TOO_LARGE,
                Intent(Intent.ACTION_SEND).setType("application/pdf").putExtra(Intent.EXTRA_STREAM,Uri.fromFile(File(context.cacheDir,"qa-share-input.bin"))) to ShareInputException.Code.UNSUPPORTED)) {
                try {store.capture(intent);error("Invalid input accepted")} catch(expected:ShareInputException) {verify(expected.code==code)}
            }
            val noSpace=ShareInbox(context,File(root,"low-space"),{0L})
            try {noSpace.capture(input("space"));error("Expected low space")} catch(expected:ShareInputException) {verify(expected.code==ShareInputException.Code.NO_SPACE)}
            val cancel=ShareInbox(context,File(root,"canceled"))
            try {cancel.capture(input("cancel")){_,bytes->if(bytes>0)throw CancellationException("QA canceled")};error("Expected cancellation")}
            catch(expected:CancellationException) {verify(cancel.requests.value.isEmpty())}
            verify(File(context.cacheDir,"qa-share-input.bin").readBytes().contentEquals(original))
            val snapshot=store.source(fileId,item);snapshot.writeText("QA tamper")
            try {store.source(fileId,item);error("Expected hash mismatch")} catch(expected:ShareInputException) {verify(true)}
            val sentinel=File(snapshot.parentFile,"unowned-sentinel");sentinel.writeText("preserve")
            store.discard(fileId);verify(!snapshot.exists() && sentinel.readText()=="preserve")
            verify(File(context.cacheDir,"qa-share-input.bin").readBytes().contentEquals(original))
            store.markText(first)
            try {store.setTarget(first,"another-peer");error("Expected fixed target")} catch(expected:IllegalArgumentException) {verify(true)}
            store.discard(first);verify(store.requests.value.isEmpty())
            report="PASSED: $checks inbox checks; content dedupe, durable partial retry, send lock, reopen, URI, limits, cancel and owned cleanup"
            File(context.filesDir,"qa-inbox-result.txt").writeText(report)
        } catch(error:Throwable) {report="FAILED: "+error;File(context.filesDir,"qa-inbox-result.txt").writeText(report)}
    }
    Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding()) {
        Text(report)
        TextButton(onClick={context.startActivity(Intent(plain).setClass(context,IncomingShareActivity::class.java))}) {Text("QA Receive text")}
        TextButton(onClick={context.startActivity(Intent(Intent.ACTION_SEND).setType("application/pdf")
            .putExtra(Intent.EXTRA_STREAM,Uri.parse("content://${context.packageName}.qa-share-input/unknown"))
            .setClass(context,IncomingShareActivity::class.java).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION))}) {Text("QA Receive file")}
        TextButton(onClick=close) {Text("QA Close")}
    }
}
