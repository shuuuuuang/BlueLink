package com.bluelink.android.files

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.webkit.*
import android.widget.Toast
import androidx.activity.compose.BackHandler
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.outlined.Info
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.OpenInNew
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.foundation.background
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import com.bluelink.android.BlueLinkApplication
import com.bluelink.android.R
import com.bluelink.android.domain.ChatAttachment
import com.bluelink.android.ui.content.FileTypeIcon
import com.bluelink.android.ui.devices.DeviceColors
import com.bluelink.android.ui.devices.DeviceScreenTheme
import com.bluelink.android.ui.theme.BlueLinkTheme
import kotlinx.coroutines.*
import java.io.ByteArrayInputStream
import java.util.UUID

/** Private, read-only viewer. Every entry comes through the existing completed-file open action. */
class TextPreviewActivity : ComponentActivity() {
    private val app get() = application as BlueLinkApplication
    override fun onStart() { super.onStart(); app.clientStarted(this) }
    override fun onStop() { app.clientStopped(this); super.onStop() }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val uri = intent.getStringExtra("source") ?: return finish()
        val name = intent.getStringExtra("name") ?: return finish()
        val attachment = ChatAttachment(UUID.randomUUID(), UUID.randomUUID(), name,
            intent.getStringExtra("mime") ?: "application/octet-stream", intent.getLongExtra("size",0), uri, "COMPLETED")
        setContent {
            val settings by app.runtime.settings.collectAsState()
            BlueLinkTheme(settings.theme, settings.language) { DeviceScreenTheme {
                TextPreviewScreen(attachment, close = { finish() }, external = {
                    FileInteraction.openExternal(this, attachment)
                })
            } }
        }
    }
    companion object {
        fun open(context: Context, attachment: ChatAttachment) {
            require(attachment.canOpen)
            context.startActivity(Intent(context, TextPreviewActivity::class.java)
                .putExtra("source", attachment.localUri).putExtra("name", attachment.fileName)
                .putExtra("mime", attachment.mimeType).putExtra("size", attachment.sizeBytes))
        }
    }
}

@Composable internal fun TextPreviewScreen(attachment: ChatAttachment, close: () -> Unit, external: () -> Unit) {
    val context = LocalContext.current
    var document by remember(attachment.localUri) { mutableStateOf<TextDocument?>(null) }
    var loading by remember(attachment.localUri) { mutableStateOf(true) }
    var failure by remember(attachment.localUri) { mutableIntStateOf(0) }
    var formatted by rememberSaveable(attachment.localUri) { mutableStateOf<Boolean?>(null) }
    var showContents by rememberSaveable(attachment.localUri) { mutableStateOf(false) }
    var headings by remember(attachment.localUri) { mutableStateOf<List<DocumentHeading>>(emptyList()) }
    var activeHeading by remember(attachment.localUri) { mutableStateOf<String?>(null) }
    var jump by remember(attachment.localUri) { mutableStateOf<HeadingJump?>(null) }
    var jumpVersion by remember(attachment.localUri) { mutableIntStateOf(0) }
    BackHandler(showContents) { showContents = false }
    fun openExternal() = runCatching(external).onFailure {
        Toast.makeText(context, context.getString(R.string.content_open_failed), Toast.LENGTH_SHORT).show()
    }
    LaunchedEffect(attachment.localUri) {
        loading = true
        try {
            document = FileInteraction.readTextDocument(context, attachment)
            if (document == null) {
                failure = R.string.text_preview_binary
                if (openExternal().isSuccess) close()
            }
        } catch (cancelled: CancellationException) { throw cancelled }
        catch (_: Exception) { failure = R.string.text_preview_unreadable }
        finally { loading = false }
    }
    Column(Modifier.fillMaxSize().backgroundForPreview().statusBarsPadding().navigationBarsPadding()) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = close) { Icon(Icons.AutoMirrored.Filled.ArrowBack, stringResource(R.string.close), tint = DeviceColors.Ink) }
            Surface(shape = RoundedCornerShape(12.dp), color = DeviceColors.Canvas) {
                Box(Modifier.size(40.dp), contentAlignment = Alignment.Center) {
                    FileTypeIcon(attachment.fileName, mimeType = attachment.mimeType, size = 24.dp)
                }
            }
            Column(Modifier.weight(1f).padding(horizontal = 10.dp)) {
                Text(attachment.fileName, fontSize = 16.sp, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(listOfNotNull(document?.encoding, android.text.format.Formatter.formatShortFileSize(context, attachment.sizeBytes)).joinToString(" · "),
                    color = DeviceColors.Secondary, fontSize = 12.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            if (document?.markdown == true) IconToggleButton(checked = showContents, onCheckedChange = { showContents = it }) {
                Icon(painterResource(R.drawable.ic_document_contents), stringResource(R.string.text_preview_contents),
                    tint = if (showContents) DeviceColors.Blue else DeviceColors.Secondary)
            }
        }
        HorizontalDivider(color = DeviceColors.Border)
        val current = document
        if (loading) Box(Modifier.weight(1f).fillMaxWidth(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
        else if (current == null) Column(Modifier.weight(1f).fillMaxWidth().padding(24.dp),
            verticalArrangement = Arrangement.Center, horizontalAlignment = Alignment.CenterHorizontally) {
            Text(stringResource(if (failure == 0) R.string.text_preview_unreadable else failure))
            TextButton(onClick = { openExternal() }) { Text(stringResource(R.string.content_open_other_app)) }
        }
        else {
            val markdown = current.markdown && (formatted ?: true)
            val chipColors = FilterChipDefaults.filterChipColors(
                containerColor = DeviceColors.Surface, labelColor = DeviceColors.Secondary,
                selectedContainerColor = DeviceColors.Selected, selectedLabelColor = DeviceColors.Blue)
            Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                if (current.markdown) Row(Modifier.weight(1f).horizontalScroll(rememberScrollState()),
                    horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                    FilterChip(colors = chipColors, selected = markdown, onClick = { formatted = true; jump = null }, label = { Text(stringResource(R.string.text_preview_rendered)) })
                    if (current.truncated) PreviewLimitInfo(attachment.localUri)
                    FilterChip(colors = chipColors, selected = !markdown, onClick = { formatted = false; jump = null }, label = { Text(stringResource(R.string.text_preview_source)) })
                } else Row(Modifier.weight(1f), verticalAlignment = Alignment.CenterVertically) {
                    Text(stringResource(R.string.text_preview_title), Modifier.padding(start = 4.dp).weight(1f, fill = false),
                        fontSize = 13.sp, color = DeviceColors.Secondary, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    if (current.truncated) PreviewLimitInfo(attachment.localUri)
                }
                IconButton(onClick = {
                    context.getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText(attachment.fileName,current.text))
                    Toast.makeText(context,context.getString(R.string.message_batch_copied),Toast.LENGTH_SHORT).show()
                }) { Icon(Icons.Default.ContentCopy, stringResource(R.string.text_preview_copy), tint = DeviceColors.Secondary, modifier = Modifier.size(21.dp)) }
                IconButton(onClick = { openExternal() }) {
                    Icon(Icons.Default.OpenInNew, stringResource(R.string.content_open_other_app), tint = DeviceColors.Secondary, modifier = Modifier.size(21.dp))
                }
            }
            if (current.markdown && showContents) ContentsPanel(headings, if (markdown) activeHeading else null, close = { showContents = false }) { heading ->
                formatted = true
                jump = HeadingJump(heading.id, ++jumpVersion)
            }
            if (current.text.isEmpty()) Box(Modifier.weight(1f).fillMaxWidth(), contentAlignment = Alignment.Center) {
                Text(stringResource(R.string.text_preview_empty), color = DeviceColors.Secondary)
            } else DocumentWebView(current.text, markdown, current.markdown, jump,
                onHeadings = { headings = it }, onActiveHeading = { activeHeading = it }, modifier = Modifier.weight(1f).fillMaxWidth())
        }
    }
}

@Composable private fun PreviewLimitInfo(source: String?) {
    // Resolve in the app's locale before DropdownMenu creates its popup window configuration.
    val notice = stringResource(R.string.text_preview_truncated)
    var expanded by remember(source) { mutableStateOf(false) }
    Box {
        IconButton(onClick = { expanded = !expanded }, modifier = Modifier.size(40.dp)) {
            Icon(Icons.Outlined.Info, stringResource(R.string.text_preview_limit_info),
                tint = if (expanded) DeviceColors.Blue else DeviceColors.Secondary, modifier = Modifier.size(18.dp))
        }
        MaterialTheme(shapes = MaterialTheme.shapes.copy(extraSmall = RoundedCornerShape(12.dp))) {
            DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false },
                modifier = Modifier.widthIn(max = 280.dp).background(DeviceColors.Surface)) {
                Text(notice, Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
                    color = DeviceColors.Ink, fontSize = 13.sp, lineHeight = 20.sp)
            }
        }
    }
}

@Composable private fun Modifier.backgroundForPreview() = this.then(Modifier.background(DeviceColors.Surface))

@Composable private fun ContentsPanel(headings: List<DocumentHeading>, activeHeading: String?, close: () -> Unit, select: (DocumentHeading) -> Unit) {
    val listState = rememberLazyListState()
    LaunchedEffect(activeHeading, headings) {
        val index = headings.indexOfFirst { it.id == activeHeading }
        if (index >= 0 && listState.layoutInfo.visibleItemsInfo.none { it.index == index }) listState.scrollToItem(index)
    }
    Surface(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp),
        shape = RoundedCornerShape(14.dp), color = DeviceColors.Canvas) {
        Column {
            Row(Modifier.fillMaxWidth().height(40.dp).padding(start = 16.dp, end = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(stringResource(R.string.text_preview_contents), Modifier.weight(1f), fontSize = 13.sp, fontWeight = FontWeight.Medium, color = DeviceColors.Secondary)
                IconButton(onClick = close) { Icon(Icons.Default.Close, stringResource(R.string.close), tint = DeviceColors.Secondary) }
            }
            if (headings.isEmpty()) Text(stringResource(R.string.text_preview_no_headings),
                Modifier.padding(start = 16.dp, end = 16.dp, bottom = 16.dp), color = DeviceColors.Secondary)
            else LazyColumn(Modifier.heightIn(max = 180.dp).fillMaxWidth(), state = listState, contentPadding = PaddingValues(bottom = 8.dp)) {
                items(headings, key = { it.id }) { heading ->
                    val selected = heading.id == activeHeading
                    Row(Modifier.fillMaxWidth().selectable(selected = selected, onClick = { select(heading) })
                        .heightIn(min = 44.dp).padding(start = (16 + (heading.level - 1) * 12).dp, end = 16.dp, top = 8.dp, bottom = 8.dp),
                        verticalAlignment = Alignment.CenterVertically) {
                        Box(Modifier.width(3.dp).height(16.dp).background(
                            if (selected) DeviceColors.Blue else androidx.compose.ui.graphics.Color.Transparent, RoundedCornerShape(2.dp)))
                        Spacer(Modifier.width(8.dp))
                        Text(heading.title.ifBlank { stringResource(R.string.text_preview_untitled_heading) },
                            color = if (selected) DeviceColors.Blue else DeviceColors.Ink,
                            fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal,
                            fontSize = 14.sp, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    }
                }
            }
        }
    }
}

private const val DOCUMENT_URL = "https://bluelink.invalid/document"
private data class HeadingJump(val id: String, val version: Int)
private class DocumentPageState(val html: String, var ready: Boolean = false, var jumpVersion: Int = -1)

private fun WebView.jumpToHeading(jump: HeadingJump?) {
    val state = tag as? DocumentPageState ?: return
    if (jump == null || !state.ready || jump.version == state.jumpVersion) return
    state.jumpVersion = jump.version
    loadUrl("$DOCUMENT_URL#${jump.id}")
}

@Composable private fun DocumentWebView(text: String, markdown: Boolean, hasContents: Boolean, jump: HeadingJump?,
    onHeadings: (List<DocumentHeading>) -> Unit, onActiveHeading: (String?) -> Unit, modifier: Modifier) {
    val dark = DeviceColors.Surface.luminance() < .5f
    val background = DeviceColors.Surface.toArgb()
    var failed by remember(text, markdown) { mutableStateOf(false) }
    val pageContent = remember { java.util.concurrent.atomic.AtomicReference("") }
    val latestJump by rememberUpdatedState(jump)
    val latestHeadings by rememberUpdatedState(onHeadings)
    val latestActiveHeading by rememberUpdatedState(onActiveHeading)
    LaunchedEffect(text, markdown) { latestActiveHeading(null) }
    val markup by produceState<DocumentMarkup?>(null, text, hasContents) {
        try { value = withContext(Dispatchers.Default) { DocumentHtml.render(text, hasContents) } }
        catch (cancelled: CancellationException) { throw cancelled }
        catch (_: Exception) { failed = true }
        catch (_: StackOverflowError) { failed = true }
    }
    LaunchedEffect(markup) { latestHeadings(markup?.headings.orEmpty()) }
    val html by produceState<String?>(null, text, markup, markdown, dark) {
        value = null
        if (markup == null && markdown) return@produceState
        try { value = withContext(Dispatchers.Default) {
            DocumentHtml.page(if (markdown) requireNotNull(markup).body else DocumentHtml.body(text, false), dark)
        } }
        catch (cancelled: CancellationException) { throw cancelled }
        catch (_: Exception) { failed = true }
        catch (_: StackOverflowError) { failed = true }
    }
    if (failed) Box(modifier, contentAlignment = Alignment.Center) { Text(stringResource(R.string.text_preview_unreadable)) }
    else if (html == null) Box(modifier, contentAlignment = Alignment.Center) { CircularProgressIndicator() }
    else AndroidView(modifier = modifier, factory = { context ->
        ReadingPositionWebView(context).apply {
            setBackgroundColor(background)
            settings.apply {
                // Only app-owned evaluateJavascript measures heading geometry. The generated page
                // keeps CSP script-src/default-src 'none', raw HTML escaped and no JS bridges.
                javaScriptEnabled = markdown
                allowFileAccess = false
                allowContentAccess = false
                blockNetworkLoads = true
                blockNetworkImage = true
                loadsImagesAutomatically = false
                domStorageEnabled = false
                setSupportZoom(true)
                builtInZoomControls = true
                displayZoomControls = false
            }
            webViewClient = object : WebViewClient() {
                override fun shouldInterceptRequest(view: WebView?, request: WebResourceRequest?): WebResourceResponse {
                    // WebView may reload a loadDataWithBaseURL document for its first fragment.
                    // Serve only our own generated main document; never resolve its URL on the network.
                    val ownDocument = request?.isForMainFrame == true &&
                        request.url.toString().substringBefore('#') == DOCUMENT_URL
                    return if (ownDocument) WebResourceResponse("text/html", "UTF-8",
                        ByteArrayInputStream(pageContent.get().toByteArray(Charsets.UTF_8)))
                    else WebResourceResponse("text/plain", "UTF-8", ByteArrayInputStream(ByteArray(0)))
                }
                override fun shouldOverrideUrlLoading(view: WebView?, request: WebResourceRequest?): Boolean {
                    val url = request?.url?.toString().orEmpty()
                    return !Regex("https://bluelink\\.invalid/document#section-[0-9]+").matches(url)
                }
                override fun onPageStarted(view: WebView?, url: String?, favicon: android.graphics.Bitmap?) {
                    (view as? ReadingPositionWebView)?.pageStarted()
                    (view?.tag as? DocumentPageState)?.ready = false
                }
                override fun onPageFinished(view: WebView?, url: String?) {
                    (view?.tag as? DocumentPageState)?.ready = true
                    view?.jumpToHeading(latestJump)
                    (view as? ReadingPositionWebView)?.pageFinished()
                }
                override fun onScaleChanged(view: WebView?, oldScale: Float, newScale: Float) {
                    (view as? ReadingPositionWebView)?.requestPosition()
                }
                override fun onRenderProcessGone(view: WebView?, detail: RenderProcessGoneDetail?): Boolean {
                    failed = true
                    return true
                }
            }
        }
    }, onRelease = { it.release() }, update = { web ->
        web.trackPosition = markdown
        web.settings.javaScriptEnabled = markdown
        web.onHeadingChanged = { id -> latestActiveHeading(id?.takeIf { candidate -> markup?.headings?.any { it.id == candidate } == true }) }
        if ((web.tag as? DocumentPageState)?.html != html) {
            pageContent.set(requireNotNull(html))
            web.tag = DocumentPageState(requireNotNull(html))
            web.setBackgroundColor(background)
            web.loadDataWithBaseURL(DOCUMENT_URL, requireNotNull(html), "text/html", "UTF-8", DOCUMENT_URL)
        } else web.jumpToHeading(jump)
    })
}
