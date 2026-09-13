package com.bluelink.android.ui.conversation

import android.widget.Toast
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectTransformGestures
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bluelink.android.R
import com.bluelink.android.domain.ChatAttachment
import com.bluelink.android.domain.ImageViewport
import com.bluelink.android.files.FileInteraction
import com.bluelink.android.ui.content.*
import com.bluelink.android.ui.files.FileDetailsPrompt
import com.bluelink.android.ui.devices.*
import java.util.Locale

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun ImagePreview(attachment: ChatAttachment,
                          transfer: com.bluelink.android.domain.TransferItem? = null,
                          peerName: String? = null, outgoing: Boolean? = transfer?.outgoing,
                          timestamp: java.time.Instant? = transfer?.let { java.time.Instant.ofEpochMilli(it.startedAtEpochMs) },
                          dismiss: () -> Unit) {
    val context = LocalContext.current
    var error by remember(attachment.attachmentId, attachment.localUri, attachment.state) { mutableStateOf(false) }
    val bitmap by produceState<android.graphics.Bitmap?>(null, attachment.attachmentId, attachment.localUri, attachment.state) {
        value = null
        error = false
        value = FileInteraction.loadPreviewBitmap(context, attachment); error = value == null
    }
    val metadata by produceState<FileInteraction.ImageMetadata?>(null, attachment.attachmentId, attachment.localUri, attachment.state) {
        value = null
        value = FileInteraction.imageMetadata(context, attachment)
    }
    var zoom by remember { mutableFloatStateOf(1f) }
    var turns by remember { mutableIntStateOf(0) }
    var pan by remember { mutableStateOf(Offset.Zero) }
    var more by remember { mutableStateOf(false) }
    var info by remember { mutableStateOf(false) }
    val reset: () -> Unit = { zoom = 1f; turns = 0; pan = Offset.Zero }
    val save = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument(attachment.mimeType)) { uri ->
        if (uri != null) runCatching { FileInteraction.copyTo(context, attachment, uri) }
            .onSuccess { Toast.makeText(context, context.getString(R.string.content_copy_saved), Toast.LENGTH_SHORT).show() }
            .onFailure { Toast.makeText(context, it.message ?: context.getString(R.string.content_save_failed), Toast.LENGTH_SHORT).show() }
    }
    fun fileAction(action: () -> Unit) { runCatching(action).onFailure { Toast.makeText(context, it.message ?: context.getString(R.string.content_file_action_failed), Toast.LENGTH_SHORT).show() } }
    val detail = metadata?.let { "${it.mimeType.substringAfter('/').uppercase(Locale.ROOT)} · ${it.width} × ${it.height} · ${contentBytes(attachment.sizeBytes)}" }
        ?: contentBytes(attachment.sizeBytes)
    BackHandler(onBack = dismiss)
        DeviceScreenTheme {
            Column(Modifier.fillMaxSize().background(DeviceColors.Surface)) {
                Row(Modifier.fillMaxWidth().heightIn(min = 64.dp).padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    ContentIconButton(R.drawable.figma_preview_back, context.getString(R.string.content_close_preview), Modifier.size(44.dp).background(DeviceColors.Canvas, RoundedCornerShape(12.dp)), action = dismiss)
                    Column(Modifier.weight(1f).padding(horizontal = 8.dp)) {
                        Text(attachment.fileName, fontSize = 16.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(detail, color = DeviceColors.Secondary, fontSize = 10.sp, lineHeight = 18.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                    ContentIconButton(R.drawable.figma_preview_more, context.getString(R.string.content_image_actions), Modifier.size(44.dp).background(DeviceColors.Canvas, RoundedCornerShape(12.dp))) { more = true }
                }
                HorizontalDivider(color = DeviceColors.Border)
                BoxWithConstraints(Modifier.weight(1f).fillMaxWidth().clipToBounds(), contentAlignment = Alignment.Center) {
                    val density = LocalDensity.current
                    val viewportWidth = with(density) { (maxWidth - 64.dp).coerceAtLeast(1.dp).toPx() }
                    val viewportHeight = with(density) { (maxHeight - 80.dp).coerceAtLeast(1.dp).toPx() }
                    val image = bitmap
                    if (image != null) {
                        val geometry = ImageViewport.fit(image.width, image.height, viewportWidth, viewportHeight, turns, zoom)
                        Box(Modifier.fillMaxSize().pointerInput(attachment.attachmentId) { detectTapGestures(onDoubleTap = { reset() }) }
                            .pointerInput(attachment.attachmentId, viewportWidth, viewportHeight) {
                                detectTransformGestures { _, delta, scale, _ ->
                                    zoom = (zoom * scale).coerceIn(0.5f, 8f)
                                    val next = ImageViewport.fit(image.width, image.height, viewportWidth, viewportHeight, turns, zoom)
                                    pan = Offset((pan.x + delta.x).coerceIn(-next.limitX, next.limitX), (pan.y + delta.y).coerceIn(-next.limitY, next.limitY))
                                }
                            }, contentAlignment = Alignment.Center) {
                            Image(image.asImageBitmap(), attachment.fileName,
                                Modifier.requiredSize(with(density) { geometry.width.toDp() }, with(density) { geometry.height.toDp() })
                                    .graphicsLayer { scaleX = zoom; scaleY = zoom; rotationZ = turns * 90f
                                        translationX = pan.x.coerceIn(-geometry.limitX, geometry.limitX); translationY = pan.y.coerceIn(-geometry.limitY, geometry.limitY) },
                                contentScale = ContentScale.Fit)
                        }
                    } else Text(if (error) context.getString(R.string.content_image_unavailable) else context.getString(R.string.content_loading_image), color = DeviceColors.Secondary)
                    Text(context.getString(R.string.content_image_gestures), Modifier.align(Alignment.BottomCenter).padding(bottom = 38.dp), fontSize = 11.sp, color = DeviceColors.Secondary)
                }
                HorizontalDivider(color = DeviceColors.Border)
                Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 14.dp), horizontalArrangement = Arrangement.spacedBy(14.dp), verticalAlignment = Alignment.CenterVertically) {
                    Row(Modifier.weight(1f).height(52.dp).background(DeviceColors.Canvas, RoundedCornerShape(12.dp))
                        .border(1.dp, DeviceColors.Border, RoundedCornerShape(12.dp)), verticalAlignment = Alignment.CenterVertically) {
                        ContentIconButton(R.drawable.figma_preview_minus, context.getString(R.string.content_zoom_out), Modifier.size(44.dp), bitmap != null) { zoom = (zoom / 1.25f).coerceAtLeast(0.5f) }
                        Text("${(zoom * 100).toInt()}%", Modifier.weight(1f), fontSize = 12.sp, textAlign = androidx.compose.ui.text.style.TextAlign.Center)
                        ContentIconButton(R.drawable.figma_preview_plus, context.getString(R.string.content_zoom_in), Modifier.size(44.dp), bitmap != null) { zoom = (zoom * 1.25f).coerceAtMost(8f) }
                    }
                    listOf(Triple(R.drawable.figma_preview_rotate_left, context.getString(R.string.content_rotate_left), { turns = (turns + 3) % 4; pan = Offset.Zero }),
                        Triple(R.drawable.figma_preview_rotate_right, context.getString(R.string.content_rotate_right), { turns = (turns + 1) % 4; pan = Offset.Zero }),
                        Triple(R.drawable.figma_preview_reset, context.getString(R.string.content_reset_image), reset)).forEach { (asset, label, action) ->
                        PreviewTransformButton(asset, label, enabled = bitmap != null, action = action)
                    }
                }
            }
            if (more) ModalBottomSheet(onDismissRequest = { more = false }, containerColor = DeviceColors.Surface, tonalElevation = 0.dp, dragHandle = null, shape = androidx.compose.ui.graphics.RectangleShape,
                sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)) {
                Row(Modifier.fillMaxWidth().height(56.dp).padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text(context.getString(R.string.content_more_actions), Modifier.weight(1f), fontSize = 16.sp)
                    ContentIconButton(R.drawable.figma_content_close, context.getString(R.string.content_close_image_actions)) { more = false }
                }
                HorizontalDivider(color = DeviceColors.Border)
                listOf(Triple(R.drawable.figma_preview_save, context.getString(R.string.content_save_as), { save.launch(attachment.fileName) }),
                    Triple(R.drawable.figma_preview_share, context.getString(R.string.content_share), { fileAction { FileInteraction.share(context, attachment) } }),
                    Triple(R.drawable.figma_preview_open, context.getString(R.string.content_open_other_app), { fileAction { FileInteraction.open(context, attachment) } }),
                    Triple(R.drawable.figma_preview_info, context.getString(R.string.content_file_info), { info = true })).forEach { (asset, label, action) ->
                    Row(Modifier.fillMaxWidth().heightIn(min = 56.dp).clickable { more = false; action() }.padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                        Box(Modifier.size(40.dp).background(DeviceColors.Selected, RoundedCornerShape(12.dp)), contentAlignment = Alignment.Center) { FigmaIcon(asset, size = 20.dp) }
                        Text(label, Modifier.weight(1f).padding(horizontal = 12.dp), fontSize = 14.sp)
                        FigmaIcon(R.drawable.figma_content_chevron, size = 18.dp)
                    }
                    HorizontalDivider(Modifier.padding(start = 68.dp), color = DeviceColors.Border)
                }
            }
            if (info) FileDetailsPrompt(attachment, transfer, peerName, outgoing, timestamp, dismiss = { info = false })
        }
}

/** Matches the compact Windows preview tools; retain a phone-sized touch target. */
@Composable
private fun PreviewTransformButton(asset: Int, label: String, enabled: Boolean, action: () -> Unit) {
    val background = if (DeviceColors.Surface.luminance() < 0.5f) Color(0xFF293241) else Color(0xFFF5F7FB)
    IconButton(onClick = action, enabled = enabled,
        modifier = Modifier.size(52.dp).clip(RoundedCornerShape(8.dp)).background(background)
            .semantics { contentDescription = label }) {
        FigmaIcon(asset, size = 20.dp, tint = if (enabled) DeviceColors.Ink else DeviceColors.Secondary)
    }
}
