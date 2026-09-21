package com.bluelink.android

import com.bluelink.android.data.local.toTransferItem

import android.net.Uri
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.room.Room
import androidx.room.withTransaction
import com.bluelink.android.data.local.*
import com.bluelink.android.domain.*
import com.bluelink.android.ui.conversation.ConversationScreen
import com.bluelink.android.ui.files.*
import kotlinx.coroutines.*
import java.io.File
import java.time.Instant
import java.util.UUID

/** Isolated Room database and generated fixtures only. Never connects or modifies user data. */
@Composable
internal fun ProductAcceptance(scene: String,image: File,close: ()->Unit) {
    val context=LocalContext.current
    val scope=rememberCoroutineScope()
    val peer=remember { ConversationSummary("product-qa", "Product QA",PeerPlatform.WINDOWS,DeviceAvailability.OFFLINE) }
    var message by remember { mutableStateOf("QA initializing") }
    var repository by remember { mutableStateOf<BlueLinkRepository?>(null) }
    var database by remember { mutableStateOf<BlueLinkDatabase?>(null) }
    var rows by remember { mutableStateOf(emptyList<ChatItem>()) }
    var draft by remember { mutableStateOf("") }
    var search by remember { mutableStateOf(false) }
    var shown by remember { mutableStateOf<TransferItem?>(null) }
    var selectFromMenu by remember { mutableStateOf<(() -> Unit)?>(null) }
    var files by remember { mutableStateOf((1L..250).map { n -> TransferItem(UUID(0,n),
        if(n == 1L) "很长的文件名称前缀用于验证摘要不会切断家庭👨‍👩‍👧‍👦字符-needle-最终报告.pdf" else "QA-$n.png",n * 100,
        outgoing = n % 2 == 0L, status = when(n % 4) { 0L -> TransferStatus.CANCELED; 1L -> TransferStatus.COMPLETED; 2L -> TransferStatus.TRANSFERRING; else -> TransferStatus.FAILED },
        localUri = Uri.fromFile(image).toString(),peerId = peer.peerId, mimeType = "image/png") }) }
    LaunchedEffect(scene) {
        try {
            val dbName="product-qa-"+UUID.randomUUID()+".db"
            var db=Room.databaseBuilder(context.applicationContext,BlueLinkDatabase::class.java,dbName).build()
            var repo=BlueLinkRepository(db)
            withContext(Dispatchers.IO) {
                db.withTransaction {
                    db.peers().upsert(PeerEntity(peer.peerId,createdAt=0,lastSeenAt=0))
                    db.conversations().upsert(ConversationEntity("peer:product-qa",peer.peerId,0))
                    for(n in 1..500) db.messages().upsert(MessageEntity(UUID(0,n.toLong()).toString(),"peer:product-qa",peer.peerId,
                        "RECEIVED","TEXT","QA needle record $n", "RECEIVED",n*1000L,n.toLong()))
                }
                val exact="草稿 QA 🌍\n  spaces  "
                check(repo.saveDraft(peer.peerId,exact))
                db.close()
                db=Room.databaseBuilder(context.applicationContext,BlueLinkDatabase::class.java,dbName).build(); repo=BlueLinkRepository(db)
                check(repo.loadDrafts()[peer.peerId]==exact)
                val last=repo.loadHistory(peer.peerId); check(last.size==200 && last.first().id==UUID(0,301))
                val older=repo.loadHistory(peer.peerId,before=last.first().id); check(older.size==200 && older.first().id==UUID(0,101))
                val around=repo.loadHistory(peer.peerId,around=UUID(0,100)); check(around.size==200 && around.any { it.id==UUID(0,100) })
                check(repo.loadHistory("other",around=UUID(0,100)).isEmpty())
                check(repo.loadHistory(peer.peerId,all=true).size==500)
                val interrupted=listOf("SENDING","LOCAL_QUEUED","DELIVERED","READ","SENT","FAILED")
                for((index,status) in interrupted.withIndex()) db.messages().upsert(MessageEntity(
                    UUID(2,index.toLong()).toString(),"peer:product-qa",peer.peerId,"OUTGOING","TEXT",
                    "  QA recover exact 🌍 "+index,status,600000L+index,index.toLong()))
                check(db.messages().recoverInterruptedOutgoing()==2)
                check(db.messages().recoverInterruptedOutgoing()==0)
                for((index,status) in interrupted.withIndex()) {
                    val recovered=checkNotNull(db.messages().find(UUID(2,index.toLong()).toString()))
                    check(recovered.status==if(index<2) "FAILED" else status)
                    check(recovered.content=="  QA recover exact 🌍 "+index)
                    db.messages().delete(recovered.messageId)
                }
                check(repo.loadDrafts()[peer.peerId]==exact)
                val state=ManagedSessionState(UUID.randomUUID(),peer.peerId,peer.peerName,"qa",ConnectionPhase.CONNECTED,"QA",0)
                val outgoing=ChatItem(UUID(1,1),"  share exact 🌍  ",true,status=MessageStatus.SENDING)
                check(!repo.prepareSharedText(state,outgoing))
                repo.updateMessageStatus(outgoing.id.toString(),peer.peerId,MessageStatus.FAILED)
                check(!repo.prepareSharedText(state,outgoing))
                repo.updateMessageStatus(outgoing.id.toString(),peer.peerId,MessageStatus.DELIVERED)
                repo.updateMessageStatus(outgoing.id.toString(),peer.peerId,MessageStatus.SENT)
                check(repo.prepareSharedText(state,outgoing))
                check(repo.loadHistory(peer.peerId).first {it.id==outgoing.id}.status==MessageStatus.DELIVERED)
                repo.deleteMessage(outgoing.id)
                val sharedTransfer=TransferItem(UUID(1,2),"QA durable.pdf",123,outgoing=true,
                    status=TransferStatus.QUEUED,localUri="file:///qa-owned-snapshot",sourceSha256="ab".repeat(32))
                repo.prepareSharedTransfer(state,sharedTransfer)
                check(db.transfers().find(sharedTransfer.id.toString())?.localUri==sharedTransfer.localUri)
                check(db.transfers().find(sharedTransfer.id.toString())?.sha256?.contentEquals(ByteArray(32) { 0xab.toByte() }) == true)
                check(db.transfers().find(sharedTransfer.id.toString())!!.toTransferItem().sourceSha256 == sharedTransfer.sourceSha256)
                try {repo.prepareSharedTransfer(state.copy(peerId=null),sharedTransfer);error("Missing peer accepted")}
                catch(expected:IllegalArgumentException) { }
                repo.deleteTransfer(sharedTransfer.id)

            }
            database=db; repository=repo; rows=repo.loadHistory(peer.peerId); draft=repo.loadDrafts()[peer.peerId].orEmpty()
            if(scene == "product-benchmark") {
                val report=withContext(Dispatchers.Default) {
                    val metrics=org.json.JSONArray()
                    for(count in listOf(1000,10000,100000)) {
                        val samples=(1L..count.toLong()).map { n -> TransferItem(UUID(0,n),"资料-${n % 100}报告.PDF",n,
                            outgoing=false,status=TransferStatus.COMPLETED,startedAtEpochMs=n) }
                        val options=FileQueryOptions(query="报告",sort=HistorySort.NAME,locale=java.util.Locale.CHINA)
                        repeat(3) { HistorySearch.files(samples,options) }
                        val timings=(1..20).map {
                            val start=System.nanoTime(); delay(125); check(HistorySearch.files(samples,options).size==count)
                            (System.nanoTime()-start)/1e6
                        }.sorted()
                        metrics.put(org.json.JSONObject().put("count",count).put("p95Ms",timings[18]).put("includesDebounce",true).put("includesRendering",false))
                    }
                    metrics.toString(2)
                }
                File(context.filesDir,"product-qa-benchmark.json").writeText(report)
                message="PASSED: device query benchmark saved " + report
                return@LaunchedEffect
            }
            message="PASSED: Room pages/context, draft, durable hash + message recovery"
        } catch(error: Exception) { message="FAILED: "+error.toString() }
    }
    DisposableEffect(Unit) { onDispose { database?.close() } }
    val batch=remember { object : FileBatchOperations {
        override suspend fun check(ids: List<UUID>,action: FileBatchAction)=ids.map { id-> FileBatchPolicy.check(id,files.find { it.id==id },action,false,true) }
        override suspend fun run(ids: List<UUID>,action: FileBatchAction)=FileBatchRunner.run(ids,{check(listOf(it),action).single()}) { id->
            if(action==FileBatchAction.DELETE_RECORDS) files=files.filterNot { it.id==id }
            else if(action==FileBatchAction.SHARE) error("Use native share fixture; no external dispatch here")
        }
    } }
    Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding()) {
        Row {
            TextButton(onClick=close) { Text("QA Close") }
            TextButton(onClick={ search=true }) { Text("QA Search") }
            TextButton(onClick={ scope.launch {
                try {
                    val saved=repository!!.loadDrafts()[peer.peerId]
                    check(saved==draft); message="PASSED: draft matches Room"
                } catch(error:Exception) { message="FAILED: "+error }
            } }) { Text("QA Draft") }
        }
        Text(message)
        if(scene == "product-files") FilesScreen(Modifier.weight(1f),files,listOf(peer),receiveDirectory="QA only",openSettings={},
            open={shown=it},more={shown=it},batch=batch,moreWithSelection={item,select->shown=item;selectFromMenu=select})
        else ConversationScreen(Modifier.weight(1f),peer.peerId,rows,ConnectionState(ConnectionPhase.OFFLINE),listOf(peer),emptyList(),true,
            "QA only",close,{},{},{},{},{},{},{},{},{},
            searchRequested=search,searchRequestHandled={search=false},
            draftText=draft,draftChanged={draft=it;scope.launch { repository?.saveDraft(peer.peerId,it) }},
            loadSearchHistory={repository!!.loadHistory(peer.peerId,all=true)},
            loadHistoryContext={item -> rows=(rows+repository!!.loadHistory(peer.peerId,around=item.id)).distinctBy {it.id}.sortedBy {it.timestamp}; true},
            loadEarlierHistory={val earlier=repository!!.loadHistory(peer.peerId,before=rows.first().id);rows=(earlier+rows).distinctBy {it.id};earlier.isNotEmpty()})
    }
    shown?.let { TransferActionSheet(it,false,{shown=null},selectMultiple=selectFromMenu) {} }
}
