package com.bluelink.android.ui.devices

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.bluelink.android.R
import com.bluelink.android.domain.ConversationSummary
import com.bluelink.android.domain.PeerPlatform
import com.bluelink.android.ui.components.*
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

@Composable
internal fun DeviceDetailsPrompt(conversation: ConversationSummary, dismiss: () -> Unit) {
    val context = LocalContext.current
    BlueLinkPrompt(context.getString(R.string.content_device_details), dismiss = dismiss) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.Top) {
            Box(Modifier.size(52.dp).background(DeviceColors.Selected, RoundedCornerShape(14.dp)),
                contentAlignment = Alignment.Center) {
                com.bluelink.android.ui.devices.FigmaIcon(when (conversation.platform) {
                    PeerPlatform.ANDROID -> R.drawable.figma_phone
                    PeerPlatform.WINDOWS -> R.drawable.figma_desktop
                    PeerPlatform.UNKNOWN -> R.drawable.figma_generic
                }, tint = DeviceColors.Blue)
            }
            Column(Modifier.weight(1f).padding(start = 16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                PromptField(context.getString(R.string.content_device_name), conversation.peerName)
                PromptField(context.getString(R.string.content_device_type), when (conversation.platform) {
                    PeerPlatform.ANDROID -> context.getString(R.string.content_android_phone)
                    PeerPlatform.WINDOWS -> context.getString(R.string.content_windows_pc)
                    PeerPlatform.UNKNOWN -> context.getString(R.string.content_other_device)
                })
                PromptField(context.getString(R.string.content_system_version), context.getString(R.string.content_peer_not_provided))
                PromptField(context.getString(R.string.content_device_id), conversation.peerId.let { id ->
                    if (id.matches(Regex("[0-9a-fA-F]{32}"))) id.uppercase().chunked(4).joinToString(":") else id
                })
                PromptField(context.getString(R.string.content_trust_status), if (conversation.isTrusted) context.getString(R.string.content_trusted) else context.getString(R.string.content_untrusted),
                    valueColor = if (conversation.isTrusted) DeviceColors.Success else DeviceColors.Secondary)
                PromptField(context.getString(R.string.content_last_connected), conversation.lastConnectedAt?.let {
                    DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm").withZone(ZoneId.systemDefault()).format(Instant.ofEpochMilli(it))
                } ?: context.getString(R.string.content_never_connected), divider = false)
            }
        }
    }
}
