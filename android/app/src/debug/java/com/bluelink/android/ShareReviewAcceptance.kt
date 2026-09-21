package com.bluelink.android

import androidx.compose.runtime.*
import com.bluelink.android.domain.*
import com.bluelink.android.sharing.*
import java.util.UUID

/** Actual presentation with isolated targets; no runtime send or real inbox mutation. */
@Composable internal fun ShareReviewAcceptance(close: () -> Unit) {
    val peer = remember { ConversationSummary("qa-share-review", "BlueLink Windows", PeerPlatform.WINDOWS, DeviceAvailability.CONNECTED) }
    var selected by remember { mutableStateOf<String?>(null) }
    var request by remember { mutableStateOf(SharedRequest(UUID.randomUUID(),"qa", "明天下午三点开会\n\n━━━━━━━━━━━━━━━━━━━━\n\n请提前十分钟到场",listOf(
        SharedFile(UUID.randomUUID(),"01-需要提前准备的材料.txt","text/plain",121,"qa"),
        SharedFile(UUID.randomUUID(),"项目参考资料与设计说明.pdf","application/pdf",1024*1024,"qa")))) }
    IncomingShareReview(request,listOf(request),listOf(peer),selected,false,false,false,"",0,
        {},{selected=it},close,close,close,close,
        {request=request.copy(peerId=selected,textSubmitted=true,files=request.files.map {it.copy(submitted=true)})})
}
