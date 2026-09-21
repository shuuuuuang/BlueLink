package com.bluelink.android

import android.content.ClipboardManager
import android.net.Uri
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.room.Room
import com.bluelink.android.data.local.*
import com.bluelink.android.domain.*
import com.bluelink.android.ui.components.*
import com.bluelink.android.ui.conversation.ConversationScreen
import com.bluelink.android.ui.files.TransferActionSheet
import java.io.File
import java.time.Instant
import java.util.UUID

/** Production conversation, batch runner, Room deletion and share intents; isolated QA history only. */
@Composable internal fun MessageBatchAcceptance(image: File, close: () -> Unit, longHistory: Boolean = false, groupedHistory: Boolean = false) {
    val context = LocalContext.current
    val peer = remember { ConversationSummary("message-batch-qa","BlueLink 多选验收",PeerPlatform.WINDOWS,DeviceAvailability.OFFLINE) }
    val file = remember { File(image.parentFile,"message-batch-QA.txt").apply { writeText("QA preserved file bytes") } }
    val imageAttachment = remember { ChatAttachment(UUID.randomUUID(),UUID.randomUUID(),"QA-image.png","image/png",image.length(),Uri.fromFile(image).toString(),"COMPLETED") }
    val attachment = remember { ChatAttachment(UUID.randomUUID(),UUID.randomUUID(),file.name,"text/plain",file.length(),Uri.fromFile(file).toString(),"COMPLETED") }
    val fixtures = remember { listOf(
        ChatItem(UUID(0,1),"QA 第一条 🌍",false,Instant.ofEpochSecond(1),MessageStatus.RECEIVED),
        ChatItem(UUID(0,2),"",true,Instant.ofEpochSecond(2),MessageStatus.SENT,ChatItemKind.FILE,listOf(attachment)),
        ChatItem(UUID(0,3),"",false,Instant.ofEpochSecond(3),MessageStatus.RECEIVED,ChatItemKind.IMAGE,listOf(imageAttachment)),
        ChatItem(UUID(0,4),"QA 最后一条",true,Instant.ofEpochSecond(4),MessageStatus.SENT),
        ChatItem(UUID(0,5),"QA 正在发送",true,Instant.ofEpochSecond(5),MessageStatus.SENDING)) + if (longHistory) (6L..45L).map { ChatItem(UUID(0,it), "QA 范围消息 $it", it % 2L == 0L, Instant.ofEpochSecond(it), MessageStatus.SENT) } else emptyList() }
    val displayed = remember { if (groupedHistory) listOf(
        fixtures[0].copy(text="明天下午三点开会"),
        fixtures[0].copy(id=UUID(0,6),text="请提前十分钟到场",timestamp=Instant.ofEpochSecond(2)),
        fixtures[1].copy(timestamp=Instant.ofEpochSecond(3)),
        fixtures[3].copy(text="需要提前准备的材料"),
        fixtures[4].copy(text="项目进度与待确认事项",status=MessageStatus.SENT)
    ) else fixtures }
    var messages by remember { mutableStateOf(displayed) }
    val db = remember { Room.databaseBuilder(context,BlueLinkDatabase::class.java,File(context.cacheDir,"message-batch-${UUID.randomUUID()}.db").absolutePath).build() }
    val repository = remember { BlueLinkRepository(db) }
    var ready by remember { mutableStateOf(false) }
    var result by remember { mutableStateOf("QA initializing") }
    var messageMenu by remember { mutableStateOf<ChatItem?>(null) }
    var fileMenu by remember { mutableStateOf<ChatAttachment?>(null) }
    var enter by remember { mutableStateOf<(() -> Unit)?>(null) }
    LaunchedEffect(db) {
        verifySeparateMessageSharing(context, fixtures)
        db.peers().upsert(PeerEntity(peer.peerId,createdAt=0,lastSeenAt=0))
        db.conversations().upsert(ConversationEntity("peer:"+peer.peerId,peer.peerId,0))
        displayed.forEach { item ->
            db.messages().upsert(MessageEntity(item.id.toString(),"peer:"+peer.peerId,peer.peerId,
                if(item.outgoing) "OUTGOING" else "INCOMING",item.kind.name,item.text,item.status.name,item.timestamp.toEpochMilli(),item.timestamp.toEpochMilli()))
        }
        ready=true; result="QA ready · ${displayed.size} messages"
    }
    DisposableEffect(db) { onDispose { db.close() } }
    Column(Modifier.fillMaxSize().statusBarsPadding()) {
        Row {
            TextButton(onClick={
                val clip=context.getSystemService(ClipboardManager::class.java).primaryClip?.getItemAt(0)?.text?.toString()
                result=if(clip==MessageBatch.copyText(displayed)) "PASS ordered copy" else "FAIL clipboard"
                File(context.cacheDir,"message-batch-copy-result.txt").writeText(result+"\n"+clip)
            }) { Text("QA Check copy") }
            TextButton(onClick=close) { Text("QA Close") }
        }
        Text(result)
        if(ready) ConversationScreen(Modifier.weight(1f),peer.peerId,messages,ConnectionState(ConnectionPhase.OFFLINE),
            listOf(peer),emptyList(),true,"QA",close,{},{},{},{},{},{},{},{},{},
            deleteSelectedMessages={ ids ->
                val outcome=MessageBatch.delete(ids,{id->messages.firstOrNull{it.id==id}},{false}) { id ->
                    repository.deleteMessage(id); messages=messages.filterNot{it.id==id}
                }
                val persisted=db.messages().loadConversation("peer:"+peer.peerId)
                check(outcome.filter{it.deleted}.none{removed->persisted.any{it.messageId==removed.id.toString()}})
                check(file.readText()=="QA preserved file bytes" && image.exists())
                result="PASS Room delete ${outcome.count{it.deleted}} / kept ${outcome.count{!it.deleted}} · files retained"
                File(context.cacheDir,"message-batch-delete-result.txt").writeText(result)
                outcome
            },
            longPressMessageWithSelection={item,select->messageMenu=item;enter=select},
            longPressAttachmentWithSelection={item,select->fileMenu=item;enter=select})
    }
    messageMenu?.let { ActionSheet("QA 消息","",listOfNotNull(enter?.let { callback ->
        ActionOption(context.getString(R.string.batch_select),R.drawable.ic_message_multiselect,action=callback)
    }), {messageMenu=null;enter=null}) }
    fileMenu?.let { item -> TransferActionSheet(TransferItem(item.transferId,item.fileName,item.sizeBytes,
        outgoing=false,status=TransferStatus.COMPLETED,localUri=item.localUri,mimeType=item.mimeType),false,
        {fileMenu=null;enter=null},messageContext=true,selectMultiple=enter) {} }
}
