package com.bluelink.android.ui.conversation

import android.widget.Toast
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.animation.core.animate
import androidx.compose.animation.core.tween
import androidx.compose.animation.core.FastOutSlowInEasing
import kotlinx.coroutines.launch
import kotlinx.coroutines.Job
import kotlinx.coroutines.withTimeoutOrNull
import kotlin.math.abs
import java.util.UUID
import androidx.compose.foundation.gestures.*
import androidx.compose.ui.input.pointer.positionChanged
import com.bluelink.android.domain.ImageNavigation
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
                          loadAdjacent: suspend (Int) -> ChatAttachment? = { null },
                          selectImage: (ChatAttachment) -> Unit = {},
                          dismiss: () -> Unit) {
    val context = LocalContext.current
    val resolveAdjacent by rememberUpdatedState(loadAdjacent)
    val selectCurrentImage by rememberUpdatedState(selectImage)
    val animationScope = rememberCoroutineScope()
    val boundaryNotices = remember { SnackbarHostState() }
    var boundaryNoticeJob by remember { mutableStateOf<Job?>(null) }
    fun showBoundaryNotice(step: Int) {
        boundaryNoticeJob?.cancel()
        boundaryNoticeJob = animationScope.launch {
            withTimeoutOrNull(2_000) {
                boundaryNotices.showSnackbar(context.getString(
                    if (step < 0) R.string.content_first_image else R.string.content_last_image), duration = SnackbarDuration.Short)
            }
        }
    }
    // Keep only the current image and its neighbors decoded. Switching reuses the
    // already visible neighbor so the settled page never flashes a loading frame.
    val cache = remember { linkedMapOf<UUID, PreviewPage>() }
    suspend fun loadPage(item: ChatAttachment): PreviewPage {
        cache[item.transferId]?.takeIf { it.attachment.localUri == item.localUri }?.let { return it }
        val page = PreviewPage(item, FileInteraction.loadPreviewBitmap(context, item))
        cache[item.transferId] = page
        while (cache.size > 5) cache.remove(cache.keys.first())
        return page
    }
    val currentPage by key(attachment.transferId, attachment.localUri, attachment.state) {
        produceState(cache[attachment.transferId]) { value = loadPage(attachment) }
    }
    val neighbors by key(attachment.transferId) {
        produceState<PreviewNeighbors?>(null) {
            val previous = resolveAdjacent(-1)?.let { loadPage(it) }
            val next = resolveAdjacent(1)?.let { loadPage(it) }
            value = PreviewNeighbors(previous, next)
            val keep = setOfNotNull(attachment.transferId, previous?.attachment?.transferId, next?.attachment?.transferId)
            cache.keys.filter { it !in keep }.forEach { cache.remove(it) }
        }
    }
    val bitmap = currentPage?.bitmap
    val error = currentPage != null && bitmap == null
    var pageOffset by remember(attachment.transferId) { mutableFloatStateOf(0f) }
    var settling by remember(attachment.transferId) { mutableStateOf(false) }
    val metadata by produceState<FileInteraction.ImageMetadata?>(null, attachment.attachmentId, attachment.localUri, attachment.state) {
        value = null
        value = FileInteraction.imageMetadata(context, attachment)
    }
    var zoom by remember(attachment.transferId) { mutableFloatStateOf(1f) }
    var turns by remember(attachment.transferId) { mutableIntStateOf(0) }
    var pan by remember(attachment.transferId) { mutableStateOf(Offset.Zero) }
    var more by remember(attachment.transferId) { mutableStateOf(false) }
    var info by remember(attachment.transferId) { mutableStateOf(false) }
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
                    val image by rememberUpdatedState(bitmap)
                    val swipeThreshold = with(density) { 64.dp.toPx() }
                    val pageWidth = with(density) { maxWidth.toPx() }
                    val adjacent by rememberUpdatedState(neighbors)
                    Box(Modifier.fillMaxSize().pointerInput(attachment.transferId) { detectTapGestures(onDoubleTap = { if (!settling) reset() }) }
                        .pointerInput(attachment.transferId, viewportWidth, viewportHeight) {
                            awaitEachGesture {
                                awaitFirstDown(requireUnconsumed = false)
                                var distance = Offset.Zero
                                var multiplePointers = false
                                var horizontal = false
                                val initialZoom = zoom
                                val canDrag = !settling
                                do {
                                    val event = awaitPointerEvent()
                                    multiplePointers = multiplePointers || event.changes.count { it.pressed || it.previousPressed } > 1
                                    val delta = event.calculatePan()
                                    distance += delta
                                    if (canDrag && (multiplePointers || initialZoom > 1.01f)) {
                                        pageOffset = 0f
                                        zoom = (zoom * event.calculateZoom()).coerceIn(0.5f, 8f)
                                        image?.let {
                                            val next = ImageViewport.fit(it.width, it.height, viewportWidth, viewportHeight, turns, zoom)
                                            pan = Offset((pan.x + delta.x).coerceIn(-next.limitX, next.limitX), (pan.y + delta.y).coerceIn(-next.limitY, next.limitY))
                                        }
                                    } else if (canDrag) {
                                        horizontal = horizontal || (abs(distance.x) > viewConfiguration.touchSlop && abs(distance.x) > abs(distance.y) * 1.4f)
                                        if (horizontal) {
                                            val available = if (distance.x < 0) adjacent?.next != null else adjacent?.previous != null
                                            pageOffset = ImageNavigation.dragOffset(distance.x, pageWidth, available)
                                        }
                                    }
                                    if (multiplePointers || horizontal || initialZoom > 1.01f || !canDrag)
                                        event.changes.filter { it.positionChanged() }.forEach { it.consume() }
                                } while (event.changes.any { it.pressed })
                                if (canDrag && horizontal && !multiplePointers && initialZoom <= 1.01f) {
                                    val step = ImageNavigation.swipeStep(distance.x, distance.y, swipeThreshold, initialZoom, multiplePointers)
                                    val candidate = if (step > 0) adjacent?.next else if (step < 0) adjacent?.previous else null
                                    settling = true
                                    animationScope.launch {
                                        try {
                                            val destination = if (candidate != null) -step * pageWidth else 0f
                                            animate(pageOffset, destination, animationSpec = tween(220, easing = FastOutSlowInEasing)) { value, _ -> pageOffset = value }
                                            if (candidate != null) selectCurrentImage(candidate.attachment)
                                            else if (step != 0 && adjacent != null) showBoundaryNotice(step)
                                        } finally { settling = false }
                                    }
                                }
                            }
                        }, contentAlignment = Alignment.Center) {
                        neighbors?.previous?.let { page ->
                            PreviewNeighbor(page, pageOffset - pageWidth, viewportWidth, viewportHeight)
                        }
                        neighbors?.next?.let { page ->
                            PreviewNeighbor(page, pageOffset + pageWidth, viewportWidth, viewportHeight)
                        }
                        Box(Modifier.fillMaxSize().graphicsLayer { translationX = pageOffset }, contentAlignment = Alignment.Center) {
                            if (bitmap != null) {
                                val geometry = ImageViewport.fit(bitmap.width, bitmap.height, viewportWidth, viewportHeight, turns, zoom)
                                Image(bitmap.asImageBitmap(), attachment.fileName,
                                    Modifier.requiredSize(with(density) { geometry.width.toDp() }, with(density) { geometry.height.toDp() })
                                        .graphicsLayer { scaleX = zoom; scaleY = zoom; rotationZ = turns * 90f
                                            translationX = pan.x.coerceIn(-geometry.limitX, geometry.limitX); translationY = pan.y.coerceIn(-geometry.limitY, geometry.limitY) },
                                    contentScale = ContentScale.Fit)
                            } else Text(if (error) context.getString(R.string.content_image_unavailable) else context.getString(R.string.content_loading_image), color = DeviceColors.Secondary)
                        }
                    }
                    if (boundaryNotices.currentSnackbarData == null) {
                        Text(context.getString(R.string.content_image_gestures), Modifier.align(Alignment.BottomCenter).padding(bottom = 38.dp), fontSize = 11.sp, color = DeviceColors.Secondary)
                    }
                    SnackbarHost(boundaryNotices, Modifier.align(Alignment.BottomCenter).padding(horizontal = 16.dp, vertical = 12.dp)) { notice ->
                        Surface(color = DeviceColors.Surface, contentColor = DeviceColors.Ink,
                            shape = RoundedCornerShape(12.dp), shadowElevation = 6.dp,
                            modifier = Modifier.widthIn(max = 360.dp)) {
                            Row(Modifier.padding(horizontal = 16.dp, vertical = 12.dp),
                                verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                                FigmaIcon(R.drawable.figma_preview_info, size = 20.dp, tint = DeviceColors.Blue)
                                Text(notice.visuals.message, fontSize = 13.sp)
                            }
                        }
                    }
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
                listOf(Triple(R.drawable.figma_action_copy, context.getString(R.string.content_copy_filename), { FileInteraction.copyFileName(context, attachment.fileName) }),
                    Triple(R.drawable.figma_preview_save, context.getString(R.string.content_save_as), { save.launch(attachment.fileName) }),
                    Triple(R.drawable.ic_file_share, context.getString(R.string.content_share), { fileAction { FileInteraction.share(context, attachment) } }),
                    Triple(R.drawable.ic_file_open, context.getString(R.string.content_open_other_app), { fileAction { FileInteraction.openExternal(context, attachment) } }),
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

private data class PreviewPage(val attachment: ChatAttachment, val bitmap: android.graphics.Bitmap?)
private data class PreviewNeighbors(val previous: PreviewPage?, val next: PreviewPage?)

@Composable
private fun PreviewNeighbor(page: PreviewPage, offset: Float, viewportWidth: Float, viewportHeight: Float) {
    val density = LocalDensity.current
    Box(Modifier.fillMaxSize().graphicsLayer { translationX = offset }, contentAlignment = Alignment.Center) {
        val image = page.bitmap
        if (image != null) {
            val geometry = ImageViewport.fit(image.width, image.height, viewportWidth, viewportHeight, 0, 1f)
            Image(image.asImageBitmap(), page.attachment.fileName,
                Modifier.requiredSize(with(density) { geometry.width.toDp() }, with(density) { geometry.height.toDp() }),
                contentScale = ContentScale.Fit)
        } else Text(LocalContext.current.getString(R.string.content_image_unavailable), color = DeviceColors.Secondary)
    }
}
