package com.bluelink.android.ui.files

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bluelink.android.R
import com.bluelink.android.domain.*
import com.bluelink.android.files.FileInteraction
import com.bluelink.android.ui.components.BlueLinkPrompt
import com.bluelink.android.ui.components.PromptField
import com.bluelink.android.ui.content.*
import com.bluelink.android.ui.devices.*
import java.time.Instant

@Composable
internal fun FileDetailsPrompt(attachment: ChatAttachment, transfer: TransferItem? = null,
                               peerName: String? = null, outgoing: Boolean? = transfer?.outgoing,
                               timestamp: Instant? = transfer?.let { Instant.ofEpochMilli(it.startedAtEpochMs) },
                               dismiss: () -> Unit) {
    val context = LocalContext.current
    val metadata by produceState<FileInteraction.ImageMetadata?>(null, attachment) {
        value = if (attachment.isImage) FileInteraction.imageMetadata(context, attachment) else null
    }
    val location by produceState<String?>(null, attachment.localUri) {
        value = FileInteraction.locationLabel(context, attachment)
    }
    BlueLinkPrompt(if (attachment.isImage) context.getString(R.string.content_image_details) else context.getString(R.string.content_file_details), dismiss) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.Top) {
            Box(Modifier.size(52.dp).background(DeviceColors.Selected, RoundedCornerShape(14.dp)),
                contentAlignment = Alignment.Center) {
                FileTypeIcon(attachment.fileName, mimeType = attachment.mimeType, size = 24.dp)
            }
            Column(Modifier.weight(1f).padding(start = 16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                PromptField(context.getString(R.string.content_file_name), attachment.fileName)
                if (attachment.isImage) PromptField(context.getString(R.string.content_dimensions), metadata?.let { "${it.width} × ${it.height}" } ?: context.getString(R.string.content_unreadable))
                else PromptField(context.getString(R.string.content_file_type), attachment.mimeType)
                PromptField(context.getString(R.string.content_file_size), contentBytes(attachment.sizeBytes))
                PromptField(context.getString(R.string.content_direction), when (outgoing) {
                    true -> contentRoute(true, peerName ?: context.getString(R.string.content_peer), context)
                    false -> contentRoute(false, peerName ?: context.getString(R.string.content_peer), context)
                    null -> context.getString(R.string.content_not_recorded)
                })
                PromptField(context.getString(R.string.content_location), location ?: context.getString(R.string.content_not_saved))
                if (attachment.state != TransferStatus.COMPLETED.name) PromptField(context.getString(R.string.content_status), attachment.contentStatus(context))
                PromptField(if (outgoing == true) context.getString(R.string.content_sent_at) else context.getString(R.string.content_received_at),
                    timestamp?.let { "${contentDay(it, context)} ${contentTime(it)}" } ?: context.getString(R.string.content_not_recorded), divider = false)
            }
        }
    }
}

@Composable
internal fun TransferFailurePrompt(transfer: TransferItem, dismiss: () -> Unit) {
    val context = LocalContext.current
    BlueLinkPrompt(context.getString(R.string.content_failed), dismiss) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.Top) {
            Box(Modifier.size(52.dp).background(DeviceColors.Selected, RoundedCornerShape(14.dp)),
                contentAlignment = Alignment.Center) {
                FigmaIcon(R.drawable.figma_dialog_warning, size = 24.dp, tint = DeviceColors.Error)
            }
            Column(Modifier.weight(1f).padding(start = 16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                PromptField(context.getString(R.string.content_file_name), transfer.name)
                PromptField(context.getString(R.string.content_failure_reason), transfer.failureDetail?.takeIf { it.isNotBlank() }
                    ?: contentStatus(transfer.status, context), valueColor = DeviceColors.Error)
                PromptField(context.getString(R.string.content_occurred_at), Instant.ofEpochMilli(transfer.updatedAtEpochMs).let { "${contentDay(it, context)} ${contentTime(it)}" })
                PromptField(context.getString(R.string.content_error_code), context.getString(R.string.content_not_provided), divider = false)
            }
        }
        Text(context.getString(R.string.content_retry_hint),
            Modifier.fillMaxWidth().background(DeviceColors.Error.copy(alpha = .07f), RoundedCornerShape(12.dp))
                .padding(14.dp), color = DeviceColors.Error, fontSize = 13.sp, lineHeight = 20.sp)
    }
}
