package com.bluelink.android.ui.conversation

import android.content.Context
import android.graphics.*
import android.graphics.drawable.Drawable
import android.text.*
import android.text.style.ImageSpan
import android.view.ContentInfo
import android.view.Gravity
import android.view.inputmethod.EditorInfo
import android.widget.EditText
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.*
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import com.bluelink.android.R
import com.bluelink.android.composer.*
import com.bluelink.android.files.ChatThumbnailGeometry
import com.bluelink.android.files.FileTypeCatalog
import com.bluelink.android.ui.content.fileTypeAssets
import com.bluelink.android.ui.devices.*

private class AttachmentSpan(val file: ComposerAttachment, drawable: Drawable, private val gap: Int) : ImageSpan(drawable) {
    init { contentDescription = file.name + " " + file.size }
    override fun getSize(paint: Paint, text: CharSequence, start: Int, end: Int, fm: Paint.FontMetricsInt?): Int {
        val metrics = paint.fontMetricsInt; val center = (metrics.ascent + metrics.descent) / 2
        fm?.apply { ascent = center - drawable.bounds.height() / 2 - gap; descent = center + (drawable.bounds.height() + 1) / 2 + gap; top = ascent; bottom = descent }
        return drawable.bounds.width() + gap * 2
    }
    override fun draw(canvas: Canvas, text: CharSequence, start: Int, end: Int, x: Float, top: Int, y: Int, bottom: Int, paint: Paint) {
        val metrics = paint.fontMetricsInt
        canvas.save(); canvas.translate(x + gap, y + (metrics.ascent + metrics.descent - drawable.bounds.height()) / 2f)
        drawable.draw(canvas); canvas.restore()
    }
}

// Android expands the cursor bounds to the tallest span on its line. Keep native blinking,
// selection and IME behavior, but draw only the current font's ascent/descent within those bounds.
internal class ComposerCursor(private val metrics: () -> Paint.FontMetricsInt, private val thickness: Int) : Drawable() {
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG)
    override fun getIntrinsicWidth() = thickness
    override fun setTint(tintColor: Int) { paint.color = tintColor; invalidateSelf() }
    override fun draw(canvas: Canvas) {
        val font = metrics(); val height = (font.descent - font.ascent).coerceAtMost(bounds.height())
        val top = bounds.exactCenterY() - height / 2f
        canvas.drawRect(bounds.left.toFloat(), top, bounds.right.toFloat(), top + height, paint)
    }
    override fun setAlpha(alpha: Int) { paint.alpha = alpha }
    override fun setColorFilter(colorFilter: ColorFilter?) { paint.colorFilter = colorFilter }
    @Deprecated("Deprecated in Android") override fun getOpacity() = PixelFormat.TRANSLUCENT
}

private data class ComposerPreview(val bitmap: Bitmap, val width: Int, val height: Int)

private class AttachmentDrawable(context: Context, file: ComposerAttachment, width: Int, height: Int, private val ink: Int,
    private val surface: Int, private val border: Int) : Drawable() {
    private val scale = context.resources.displayMetrics.density
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val title = file.name
    private val textScale = context.resources.displayMetrics.scaledDensity
    private val font = context.resources.getFont(R.font.noto_sans_sc_regular)
    private val sizeText = when { file.size < 0 -> "…"; file.size >= 1024 * 1024 -> "%.1f MiB".format(file.size / 1048576.0); file.size >= 1024 -> "%.1f KiB".format(file.size / 1024.0); else -> "${file.size} B" }
    private val picture: ComposerPreview? = if (file.mime.startsWith("image/") && file.size >= 0) runCatching {
        val options = BitmapFactory.Options().apply { inJustDecodeBounds = true }; BitmapFactory.decodeFile(file.path, options)
        var sample = 1; while (options.outWidth / sample > 480 || options.outHeight / sample > 480) sample *= 2
        BitmapFactory.decodeFile(file.path, BitmapFactory.Options().apply { inSampleSize = sample })?.let { ComposerPreview(it, options.outWidth, options.outHeight) }
    }.getOrNull() else null
    private val asset = fileTypeAssets.getValue(FileTypeCatalog.classify(file.name, file.mime))
    private val icon = context.getDrawable(asset.drawable)?.mutate()?.also { it.setTint((if (android.graphics.Color.luminance(ink) > 0.5f) asset.dark else asset.light).toArgb()) }
    private val geometry = picture?.let {
        val maximumWidth = minOf(88.0, (width / scale - 4).toDouble().coerceAtLeast(24.0))
        val maximumHeight = minOf(64.0, (height / scale - 4).toDouble().coerceAtLeast(24.0))
        ChatThumbnailGeometry.calculate(it.width.toDouble(), it.height.toDouble(), maximumWidth, maximumHeight,
            minOf(48.0, maximumWidth, maximumHeight))
    }
    init {
        val titleMeasure = Paint(paint).apply { textSize = 12 * textScale; typeface = font }
        val targetHeight = geometry?.let { (it.height * scale).toInt() }
            ?: maxOf(48 * scale, 29 * textScale + 12 * scale).toInt()
        val targetWidth = geometry?.let { (it.width * scale).toInt() }
            ?: (titleMeasure.measureText(title) + 55 * scale).toInt().coerceIn((152 * scale).toInt(), (204 * scale).toInt())
        setBounds(0, 0, targetWidth.coerceAtMost((width - 4 * scale).toInt().coerceAtLeast((24 * scale).toInt())), targetHeight)
    }
    override fun draw(canvas: Canvas) {
        val rect = RectF(bounds); paint.style = Paint.Style.FILL; paint.color = surface; canvas.drawRoundRect(rect, 6 * scale, 6 * scale, paint)
        paint.style = Paint.Style.STROKE; paint.strokeWidth = scale; paint.color = border; canvas.drawRoundRect(rect, 6 * scale, 6 * scale, paint); paint.style = Paint.Style.FILL
        if (picture != null && geometry != null) {
            val g = geometry
            val left = ((g.insetX - g.cropX * g.scale) * scale).toFloat()
            val top = ((g.insetY - g.cropY * g.scale) * scale).toFloat()
            canvas.save()
            canvas.clipPath(Path().apply { addRoundRect(rect, 6 * scale, 6 * scale, Path.Direction.CW) })
            paint.isFilterBitmap = true
            canvas.drawBitmap(picture.bitmap, null, RectF(left, top,
                left + (picture.width * g.scale * scale).toFloat(), top + (picture.height * g.scale * scale).toFloat()), paint)
            canvas.restore()
        } else {
            val pad = (7 * scale).toInt(); val side = (26 * scale).toInt(); val iconTop = (bounds.height() - side) / 2
            icon?.setBounds(pad, iconTop, pad + side, iconTop + side); icon?.draw(canvas)
            paint.color = ink; paint.textSize = 12 * textScale; paint.typeface = font
            val start = pad * 2 + side; val available = (bounds.width() - start - pad).coerceAtLeast(1).toFloat()
            val titleMetrics = paint.fontMetrics; val titleHeight = titleMetrics.descent - titleMetrics.ascent
            val secondary = Paint(paint).apply { textSize = 10.5f * textScale; alpha = 170 }
            val sizeMetrics = secondary.fontMetrics; val sizeHeight = sizeMetrics.descent - sizeMetrics.ascent
            val top = (bounds.height() - titleHeight - sizeHeight - 2 * scale) / 2
            canvas.drawText(TextUtils.ellipsize(title, TextPaint(paint), available, TextUtils.TruncateAt.MIDDLE).toString(), start.toFloat(), top - titleMetrics.ascent, paint)
            canvas.drawText(sizeText, start.toFloat(), top + titleHeight + 2 * scale - sizeMetrics.ascent, secondary)
        }
    }
    override fun setAlpha(alpha: Int) { paint.alpha = alpha }
    override fun setColorFilter(colorFilter: ColorFilter?) { paint.colorFilter = colorFilter }
    @Deprecated("Deprecated in Android") override fun getOpacity() = PixelFormat.TRANSLUCENT
}

internal class ComposerEditText(context: Context) : EditText(context) {
    var changed: (List<ComposerPart>) -> Unit = {}
    var receive: (ContentInfo) -> Unit = {}
    var lostFocus: () -> Unit = {}
    private var updating = false
    private var parts = emptyList<ComposerPart>()
    private var colors = intArrayOf(Color.BLACK, Color.WHITE, Color.LTGRAY)
    init {
        isSaveEnabled = false // The ordered draft store owns restoration, including attachment metadata.
        setBackgroundColor(Color.TRANSPARENT); setPadding(0, 0, 0, 0); gravity = Gravity.TOP or Gravity.START
        inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE or InputType.TYPE_TEXT_FLAG_CAP_SENTENCES
        imeOptions = EditorInfo.IME_FLAG_NO_EXTRACT_UI or EditorInfo.IME_FLAG_NO_ENTER_ACTION
        setHorizontallyScrolling(false); typeface = context.resources.getFont(R.font.noto_sans_sc_regular); textSize = 13f
        includeFontPadding = false
        textCursorDrawable = ComposerCursor({ paint.fontMetricsInt }, (2 * resources.displayMetrics.density).toInt().coerceAtLeast(1))
        setOnReceiveContentListener(arrayOf("image/*", "video/*", "audio/*", "application/*", "text/*")) { _, payload ->
            val split = androidx.core.view.ContentInfoCompat.partition(payload) { it.uri != null }
            split.first?.let(receive); split.second
        }
        addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {}
            override fun afterTextChanged(s: Editable?) { if (!updating) { parts = readParts(); changed(parts) } }
        })
        onFocusChangeListener = android.view.View.OnFocusChangeListener { _, focused -> if (!focused) lostFocus() }
        addOnLayoutChangeListener { _, l, t, r, b, ol, ot, or, ob -> if (r - l != or - ol || b - t != ob - ot) render() }
    }
    fun readParts(): List<ComposerPart> {
        val value = text ?: return emptyList(); val result = mutableListOf<ComposerPart>(); var cursor = 0
        for (span in value.getSpans(0, value.length, AttachmentSpan::class.java).sortedBy(value::getSpanStart)) {
            val start = value.getSpanStart(span); val end = value.getSpanEnd(span)
            if (start > cursor) result.add(ComposerPart(text = value.subSequence(cursor, start).toString()))
            result.add(ComposerPart(span.file.id, file = span.file)); cursor = end
        }
        if (cursor < value.length) result.add(ComposerPart(text = value.subSequence(cursor, value.length).toString()))
        return result
    }
    private fun span(file: ComposerAttachment) = AttachmentSpan(file, AttachmentDrawable(context, file,
        width - paddingLeft - paddingRight, height - paddingTop - paddingBottom, colors[0], colors[1], colors[2]), (2 * resources.displayMetrics.density).toInt())
    fun insert(files: List<ComposerPart>) {
        val content = SpannableStringBuilder()
        files.forEach { part -> val start = content.length; content.append("\uFFFC"); content.setSpan(span(part.file!!), start, start + 1, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE) }
        val start = minOf(selectionStart, selectionEnd).coerceAtLeast(0); val end = maxOf(selectionStart, selectionEnd).coerceAtLeast(start)
        text!!.replace(start, end, content); setSelection(start + content.length)
    }
    fun updateDocument(value: List<ComposerPart>, ink: Int, surface: Int, border: Int) {
        val palette = intArrayOf(ink, surface, border)
        if (parts == value && colors.contentEquals(palette)) return
        parts = value; colors = palette; render()
    }
    private fun render() {
        if (updating) return
        val start = selectionStart.coerceAtLeast(0); val end = selectionEnd.coerceAtLeast(start)
        val value = SpannableStringBuilder()
        parts.forEach { part -> if (part.file != null) { val offset = value.length; value.append("\uFFFC"); value.setSpan(span(part.file), offset, offset + 1, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE) } else value.append(part.text.orEmpty()) }
        updating = true
        try { setText(value); setSelection(start.coerceAtMost(value.length), end.coerceAtMost(value.length)) } finally { updating = false }
    }
}

@Composable
internal fun RichComposer(controller: ComposerController, peer: String?, draft: String, ready: Boolean,
    connected: Boolean, changed: (String) -> Unit, flush: () -> Unit, pickFile: () -> Unit) {
    val context = LocalContext.current; val density = LocalDensity.current
    val preferred by controller.height.collectAsState(); val drafts by controller.drafts.collectAsState()
    val sending by controller.sending.collectAsState(); val preparing by controller.preparing.collectAsState()
    val parts = drafts[peer?.lowercase(java.util.Locale.ROOT)].orEmpty()
    LaunchedEffect(peer, ready) { if (peer != null && ready) controller.initialize(peer, draft) }
    DisposableEffect(peer) { onDispose { peer?.let(controller::unbind) } }
    val available = LocalConfiguration.current.screenHeightDp - WindowInsets.ime.getBottom(density) / density.density
    // The old single-line field was 20sp + 20dp. The resize handle adds 20dp above it.
    val singleLineMinimum = with(density) { 20.sp.toDp().value } + 48f
    // Keep one attachment readable at the smallest setting without overwriting the user's height.
    val minimum = if (parts.any { it.file != null }) maxOf(singleLineMinimum, maxOf(48f, 29f * density.fontScale + 12f) + 44f) else singleLineMinimum
    val height = ComposerDraftStore.clampHeight(preferred, available).coerceAtLeast(minimum)
    var dragHeight by remember { mutableFloatStateOf(height) }
    val ink = DeviceColors.Ink.toArgb(); val muted = DeviceColors.Secondary.toArgb(); val blue = DeviceColors.Blue.toArgb()
    val surface = DeviceColors.Surface.toArgb(); val border = DeviceColors.Border.toArgb()
    val canSend = connected && peer !in sending && (preparing[peer] ?: 0) == 0 && ComposerDraftStore.messages(parts).isNotEmpty() && parts.none { (it.file?.size ?: 0) < 0 }
    Column(Modifier.fillMaxWidth().background(DeviceColors.Surface).imePadding()) {
        Column(Modifier.fillMaxWidth().height(height.dp).padding(horizontal = 8.dp).padding(bottom = 8.dp)) {
            Box(Modifier.fillMaxWidth().height(20.dp).semantics {
                contentDescription = context.getString(R.string.composer_resize)
                customActions = listOf(CustomAccessibilityAction(context.getString(R.string.composer_taller)) { controller.resize(height + 16, true); true }, CustomAccessibilityAction(context.getString(R.string.composer_shorter)) { controller.resize(height - 16, true); true })
            }.pointerInput(available) { detectVerticalDragGestures(onDragStart = { dragHeight = controller.height.value }, onDragEnd = { controller.resize(dragHeight, true) }, onDragCancel = { controller.resize(dragHeight, true) }) { change, dy -> change.consume(); dragHeight = ComposerDraftStore.clampHeight(dragHeight - dy / density.density, available); controller.resize(dragHeight, false) } }, contentAlignment = Alignment.Center) {
                Box(Modifier.width(32.dp).height(3.dp).clip(RoundedCornerShape(2.dp)).background(DeviceColors.Border))
            }
            Row(Modifier.weight(1f), verticalAlignment = Alignment.Bottom, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                ComposerIconButton(R.drawable.figma_content_attachment, context.getString(R.string.content_choose_file), 40.dp, ready && peer != null, DeviceColors.Selected, DeviceColors.Ink, pickFile)
                AndroidView(factory = { ComposerEditText(it) }, modifier = Modifier.weight(1f).fillMaxHeight().border(1.dp, DeviceColors.Border, RoundedCornerShape(12.dp)).padding(horizontal = 10.dp, vertical = 6.dp), update = { view ->
                    view.changed = { value -> peer?.let { controller.edit(it, value) }; changed(value.joinToString("") { it.text.orEmpty() }) }
                    view.lostFocus = flush; view.receive = { payload -> peer?.let { controller.receive(it, payload) } }
                    peer?.let { controller.bind(it, view::insert) }
                    // Center the native text/hint/caret together at the compact height. Expanded
                    // and attachment-sized composers remain top-aligned for multiline editing.
                    view.gravity = Gravity.START or if (height <= singleLineMinimum + 0.5f) Gravity.CENTER_VERTICAL else Gravity.TOP
                    view.isEnabled = ready && peer != null; view.setTextColor(ink); view.setHintTextColor(muted)
                    view.textCursorDrawable?.setTint(blue); view.textSelectHandle?.setTint(blue); view.textSelectHandleLeft?.setTint(blue); view.textSelectHandleRight?.setTint(blue)
                    view.hint = context.getString(if (connected) R.string.content_type_message else R.string.content_offline_composer); view.contentDescription = context.getString(R.string.content_type_message)
                    view.updateDocument(parts, ink, surface, border)
                })
                ComposerIconButton(R.drawable.figma_content_send, context.getString(R.string.content_send_message), 40.dp, canSend, if (canSend) DeviceColors.Blue else DeviceColors.Secondary, MaterialTheme.colorScheme.onPrimary) { peer?.let { controller.send(it) { changed("") } } }
            }
        }
    }
}
