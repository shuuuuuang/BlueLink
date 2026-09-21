package com.bluelink.android.ui.content

import android.content.Context
import android.provider.DocumentsContract
import android.net.Uri
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ColorFilter
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bluelink.android.R
import com.bluelink.android.domain.*
import com.bluelink.android.files.ChatThumbnail
import com.bluelink.android.files.ChatThumbnailLoader
import com.bluelink.android.files.FileInteraction
import com.bluelink.android.files.FileTypeCatalog
import com.bluelink.android.ui.devices.*
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

@Composable
internal fun ContentIconButton(asset: Int, description: String, modifier: Modifier = Modifier,
                               enabled: Boolean = true, tint: Color? = null, action: () -> Unit) {
    IconButton(onClick = action, enabled = enabled,
        modifier = modifier.semantics { contentDescription = description }) {
        FigmaIcon(asset, size = 24.dp, tint = tint ?: if (enabled) DeviceColors.Ink else DeviceColors.Secondary)
    }
}

@Composable
internal fun ContentSearch(value: String, onChange: (String) -> Unit, placeholder: String,
                           modifier: Modifier = Modifier, height: Dp = 40.dp, cornerRadius: Dp = 12.dp) {
    val context = LocalContext.current
    Row(modifier.fillMaxWidth().heightIn(min = height).clip(RoundedCornerShape(cornerRadius))
        .background(DeviceColors.Surface).border(1.dp, DeviceColors.Border, RoundedCornerShape(cornerRadius))
        .padding(start = 12.dp, end = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        FigmaIcon(R.drawable.figma_content_search, size = 20.dp)
        BasicTextField(value, onChange, singleLine = true,
            textStyle = TextStyle(fontFamily = DeviceFont, fontSize = 13.sp, color = DeviceColors.Ink),
            cursorBrush = SolidColor(DeviceColors.Blue),
            modifier = Modifier.weight(1f).padding(horizontal = 10.dp, vertical = 10.dp)
                .semantics { contentDescription = placeholder },
            decorationBox = { input ->
                Box { if (value.isEmpty()) Text(placeholder, color = DeviceColors.Secondary, fontSize = 13.sp); input() }
            })
        if (value.isNotEmpty()) ContentIconButton(R.drawable.figma_content_close, context.getString(R.string.content_clear_search),
            Modifier.size(height)) { onChange("") }
    }
}

@Composable
internal fun ContentTabs(labels: List<String>, selected: Int, onSelect: (Int) -> Unit) {
    Column(Modifier.background(DeviceColors.Surface)) {
        Row(Modifier.fillMaxWidth().selectableGroup()) {
            labels.forEachIndexed { index, label ->
                Column(Modifier.weight(1f).heightIn(min = 48.dp).selectable(index == selected,
                    role = Role.Tab, onClick = { onSelect(index) }), horizontalAlignment = Alignment.CenterHorizontally) {
                    Box(Modifier.heightIn(min = 45.dp), contentAlignment = Alignment.Center) {
                        Text(label, color = if (index == selected) DeviceColors.Blue else DeviceColors.Secondary,
                            fontSize = 14.sp, maxLines = 1)
                    }
                    Box(Modifier.width(if (labels.size == 2) 80.dp else 30.dp).height(3.dp)
                        .background(if (index == selected) DeviceColors.Blue else Color.Transparent, RoundedCornerShape(2.dp)))
                }
            }
        }
        HorizontalDivider(color = DeviceColors.Border)
    }
}

@Composable
internal fun <T> ContentFilter(label: String, choices: List<Pair<String, T>>, selected: T,
                              onSelect: (T) -> Unit, modifier: Modifier = Modifier) {
    var expanded by remember { mutableStateOf(false) }
    Box(modifier) {
        OutlinedButton(onClick = { expanded = true }, shape = RoundedCornerShape(10.dp),
            border = androidx.compose.foundation.BorderStroke(1.dp, DeviceColors.Border),
            colors = ButtonDefaults.outlinedButtonColors(containerColor = DeviceColors.Surface, contentColor = DeviceColors.Ink),
            contentPadding = PaddingValues(horizontal = 10.dp, vertical = 8.dp),
            modifier = Modifier.fillMaxWidth().heightIn(min = 38.dp)) {
            Text(label, Modifier.weight(1f), fontSize = 12.sp, fontWeight = androidx.compose.ui.text.font.FontWeight.Normal, maxLines = 1, overflow = TextOverflow.Ellipsis)
            FigmaIcon(R.drawable.figma_chevron, size = 14.dp)
        }
        DropdownMenu(expanded, onDismissRequest = { expanded = false }) {
            choices.forEach { (title, value) ->
                DropdownMenuItem(text = { Text(title, color = if (selected == value) DeviceColors.Blue else DeviceColors.Ink) },
                    onClick = { expanded = false; onSelect(value) })
            }
        }
    }
}

@Composable
internal fun FileTypeIcon(name: String, modifier: Modifier = Modifier, mimeType: String? = null, size: Dp = 40.dp) {
    val asset = fileTypeAssets.getValue(FileTypeCatalog.classify(name, mimeType))
    // Use the app's selected theme, including when it differs from the system theme.
    val tint = if (MaterialTheme.colorScheme.surface.luminance() < 0.5f) asset.dark else asset.light
    Image(painterResource(asset.drawable), null, modifier.size(size), contentScale = ContentScale.Fit,
        colorFilter = ColorFilter.tint(tint))
}

@Composable
internal fun AttachmentThumbnail(attachment: ChatAttachment, modifier: Modifier) {
    val context = LocalContext.current
    val bitmap by produceState<android.graphics.Bitmap?>(null, attachment.localUri, attachment.previewUri, attachment.state) {
        value = if (attachment.showsThumbnail(true)) FileInteraction.loadBitmap(context, attachment, 640) else null
    }
    Box(modifier.clip(RoundedCornerShape(12.dp)).background(DeviceColors.Selected), contentAlignment = Alignment.Center) {
        bitmap?.let { Image(it.asImageBitmap(), attachment.fileName, Modifier.fillMaxSize(), contentScale = ContentScale.Crop) }
            ?: FileTypeIcon(attachment.fileName, mimeType = attachment.mimeType, size = 32.dp)
    }
}

@Composable
internal fun ChatAttachmentThumbnail(attachment: ChatAttachment, modifier: Modifier = Modifier,
                                     overlay: @Composable BoxScope.() -> Unit = {}) {
    val context = LocalContext.current
    val thumbnail by produceState<ChatThumbnail?>(null, attachment.localUri, attachment.previewUri, attachment.state) {
        value = ChatThumbnailLoader.load(context, attachment)
    }
    // Keep measured dimensions while returning from preview or reloading an image URI.
    // Falling back to a 48 dp square would move the saved LazyColumn anchor.
    var measuredWidth by rememberSaveable(attachment.attachmentId) { mutableDoubleStateOf(48.0) }
    var measuredHeight by rememberSaveable(attachment.attachmentId) { mutableDoubleStateOf(48.0) }
    val width = thumbnail?.geometry?.width ?: measuredWidth
    val height = thumbnail?.geometry?.height ?: measuredHeight
    SideEffect { measuredWidth = width; measuredHeight = height }
    Box(modifier.size(width.dp, height.dp)
        .clip(RoundedCornerShape(12.dp)).background(DeviceColors.Selected), contentAlignment = Alignment.Center) {
        thumbnail?.let { Image(it.bitmap.asImageBitmap(), attachment.fileName, Modifier.fillMaxSize(), contentScale = ContentScale.FillBounds) }
            ?: FileTypeIcon(attachment.fileName, mimeType = attachment.mimeType, size = 32.dp)
        overlay()
    }
}

internal fun contentBytes(bytes: Long): String = when {
    bytes < 1024 -> "$bytes B"
    bytes < 1024 * 1024 -> String.format(Locale.ROOT, "%.1f KB", bytes / 1024.0)
    bytes < 1024 * 1024 * 1024 -> String.format(Locale.ROOT, "%.2f MB", bytes / 1048576.0)
    else -> String.format(Locale.ROOT, "%.2f GB", bytes / 1073741824.0)
}

internal fun contentDay(instant: Instant, context: Context): String {
    val date = instant.atZone(ZoneId.systemDefault()).toLocalDate()
    return when (date) { LocalDate.now() -> context.getString(R.string.content_today); LocalDate.now().minusDays(1) -> context.getString(R.string.content_yesterday); else -> date.toString() }
}
internal fun contentTime(instant: Instant): String = DateTimeFormatter.ofPattern("HH:mm").withZone(ZoneId.systemDefault()).format(instant)
internal fun contentStatus(item: TransferItem, context: Context): String =
    if (item.recoveryPending) context.getString(if (item.outgoing) R.string.transfer_recovery_pending else R.string.transfer_recovery_wait_sender)
    else if (item.queuedForUsb && item.status == TransferStatus.QUEUED) context.getString(R.string.transfer_usb_waiting)
    else contentStatus(item.status, context, item.outgoing)

internal fun contentStatus(status: TransferStatus, context: Context, outgoing: Boolean? = null): String = when (status) {
    TransferStatus.OFFERED, TransferStatus.QUEUED -> context.getString(if (outgoing == false) R.string.content_waiting_receive else R.string.content_waiting)
    TransferStatus.TRANSFERRING -> context.getString(if (outgoing == false) R.string.content_receiving else R.string.content_transferring)
    TransferStatus.PAUSED -> context.getString(when (outgoing) { true -> R.string.content_paused_sending; false -> R.string.content_paused_receiving; null -> R.string.content_paused })
    TransferStatus.REMOTE_PAUSED -> context.getString(if (outgoing == false) R.string.content_remote_paused_receive else R.string.content_remote_paused)
    TransferStatus.RESUMING -> context.getString(R.string.content_resuming)
    TransferStatus.VERIFYING -> context.getString(R.string.content_verifying)
    TransferStatus.COMMITTING -> context.getString(R.string.content_saving)
    TransferStatus.COMPLETED -> context.getString(when (outgoing) { true -> R.string.content_sent; false -> R.string.content_received; null -> R.string.content_completed })
    TransferStatus.REJECTED -> context.getString(R.string.content_rejected)
    TransferStatus.FAILED -> context.getString(if (outgoing == false) R.string.content_receive_failed else R.string.content_failed)
    TransferStatus.CANCELED -> context.getString(R.string.content_canceled)
}
internal fun ChatAttachment.contentStatus(context: Context, outgoing: Boolean? = null): String =
    if (recoveryPending) context.getString(if (outgoing ?: recoveryOutgoing) R.string.transfer_recovery_pending else R.string.transfer_recovery_wait_sender)
    else if (queuedForUsb && state == "QUEUED") context.getString(R.string.transfer_usb_waiting)
    else TransferStatus.entries.firstOrNull { it.name == state }
        ?.let { contentStatus(it, context, outgoing) } ?: context.getString(R.string.content_waiting)

internal fun receiveDirectoryLabel(raw: String, context: Context): String = when {
    raw.startsWith("downloads://") -> "Download/${raw.removePrefix("downloads://")}" 
    raw.startsWith("content://") -> runCatching { DocumentsContract.getTreeDocumentId(Uri.parse(raw)).replace(':', '/') }
        .getOrDefault(context.getString(R.string.content_custom_directory))
    else -> raw
}

internal fun FileStatusFilter.contentLabel(context: Context): String = context.getString(when (this) {
    FileStatusFilter.ALL -> R.string.content_all_statuses
    FileStatusFilter.ACTIVE -> R.string.content_transferring
    FileStatusFilter.COMPLETED -> R.string.content_completed
    FileStatusFilter.INCOMPLETE -> R.string.content_incomplete_group
    FileStatusFilter.FAILED -> R.string.content_failed_group
    FileStatusFilter.REJECTED -> R.string.content_rejected
    FileStatusFilter.CANCELED -> R.string.content_canceled
})
internal fun FileDirectionFilter.contentLabel(context: Context): String = context.getString(when (this) {
    FileDirectionFilter.ALL -> R.string.content_all_directions
    FileDirectionFilter.SENT -> R.string.content_sent
    FileDirectionFilter.RECEIVED -> R.string.content_received
})
internal fun HistoryKind.contentLabel(context: Context): String = context.getString(when (this) {
    HistoryKind.ALL -> R.string.content_all
    HistoryKind.TEXT -> R.string.content_text
    HistoryKind.IMAGES -> R.string.content_images
    HistoryKind.FILES -> R.string.content_files
    HistoryKind.DATE -> R.string.content_date
})
internal fun contentRoute(outgoing: Boolean, peerName: String, context: Context): String =
    context.getString(if (outgoing) R.string.content_outgoing_route else R.string.content_incoming_route, peerName)
