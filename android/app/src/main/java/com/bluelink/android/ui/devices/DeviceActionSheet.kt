package com.bluelink.android.ui.devices

import android.content.Context
import androidx.compose.runtime.Composable
import androidx.compose.ui.platform.LocalContext
import com.bluelink.android.R
import com.bluelink.android.domain.*
import com.bluelink.android.ui.components.ActionOption
import com.bluelink.android.ui.components.ActionSheet
import com.bluelink.android.ui.content.contentStatus

@Composable
internal fun DeviceActionSheet(peer: ConversationSummary, transfer: TransferItem?, canConnect: Boolean,
                               dismiss: () -> Unit, searchConversation: (() -> Unit)? = null, onAction: (DeviceAction) -> Unit) {
    val context = LocalContext.current
    val online = peer.availability == DeviceAvailability.CONNECTED
    val active = transfer?.takeIf { online && it.peerId == peer.peerId && it.status in HistoryQuery.activeStatuses }
    val detail = active?.let { deviceTransferDetail(it, context) } ?: context.getString(
        when {
            online -> R.string.content_connected_protocol
            canConnect -> R.string.device_nearby_connectable
            else -> R.string.content_offline_history
        })
    val options = DeviceActions.available(peer, active, canConnect).map { action ->
        val label = when (action) {
            DeviceAction.OPEN -> if (searchConversation != null) R.string.content_search_chat_history else R.string.content_open_conversation
            DeviceAction.CONNECT -> R.string.content_connect_device
            DeviceAction.INFO -> R.string.content_view_device
            DeviceAction.TRANSFERS -> R.string.content_view_transfers
            DeviceAction.PAUSE -> if (active?.outgoing == true) R.string.content_pause_sending else R.string.content_pause_receiving
            DeviceAction.RESUME -> if (active?.outgoing == true) R.string.content_resume_sending else R.string.content_resume_receiving
            DeviceAction.DISCONNECT -> R.string.content_disconnect
            DeviceAction.CLEAR -> R.string.content_clear_conversation
            DeviceAction.REMOVE_TRUST -> R.string.content_remove_trust
        }
        val icon = when (action) {
            DeviceAction.OPEN -> if (searchConversation != null) R.drawable.figma_content_search else R.drawable.figma_action_message
            DeviceAction.CONNECT -> R.drawable.figma_device_connect
            DeviceAction.RESUME -> R.drawable.figma_action_resume
            DeviceAction.INFO, DeviceAction.TRANSFERS -> R.drawable.figma_action_info
            DeviceAction.PAUSE -> R.drawable.figma_action_pause
            DeviceAction.DISCONNECT, DeviceAction.REMOVE_TRUST -> R.drawable.figma_action_trust
            DeviceAction.CLEAR -> R.drawable.figma_action_delete
        }
        ActionOption(context.getString(label), icon,
            destructive = action in setOf(DeviceAction.PAUSE, DeviceAction.DISCONNECT, DeviceAction.CLEAR, DeviceAction.REMOVE_TRUST),
            accent = action == DeviceAction.RESUME) {
                if (action == DeviceAction.OPEN && searchConversation != null) searchConversation() else onAction(action)
            }
    }
    ActionSheet(peer.peerName, detail, options, dismiss,
        detailColor = if (online) DeviceColors.Success else DeviceColors.Secondary)
}

@Composable
internal fun NearbyDeviceActionSheet(device: NearbyDevice, canConnect: Boolean, dismiss: () -> Unit,
                                     connect: () -> Unit, info: () -> Unit) {
    val context = LocalContext.current
    val platform = when (device.platform) {
        PeerPlatform.ANDROID -> "Android"
        PeerPlatform.WINDOWS -> "Windows"
        PeerPlatform.UNKNOWN -> context.getString(R.string.content_other_device)
    }
    ActionSheet(device.name, "${context.getString(R.string.device_nearby)} · $platform${device.rssi?.let { " · $it dBm" }.orEmpty()}",
        listOf(ActionOption(context.getString(R.string.content_connect_device), R.drawable.figma_device_connect,
            enabled = canConnect, action = connect),
            ActionOption(context.getString(R.string.content_view_device), R.drawable.figma_action_info, action = info)), dismiss)
}

internal fun deviceTransferDetail(transfer: TransferItem, context: Context): String = when (transfer.status) {
    TransferStatus.TRANSFERRING, TransferStatus.RESUMING -> context.getString(
        if (transfer.outgoing) R.string.device_sending_progress else R.string.device_receiving_progress,
        (transfer.progress.coerceIn(0f, 1f) * 100).toInt())
    else -> "${contentStatus(transfer.status, context, transfer.outgoing)} · ${(transfer.progress.coerceIn(0f, 1f) * 100).toInt()}%"
}
