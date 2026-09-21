package com.bluelink.android

import android.content.ClipData
import android.content.ClipboardManager
import android.app.Activity
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.view.View
import android.view.ViewGroup
import androidx.compose.ui.platform.LocalView
import com.bluelink.android.ui.conversation.ComposerEditText
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import com.bluelink.android.composer.*
import com.bluelink.android.ui.conversation.RichComposer
import java.io.File

@Composable internal fun ComposerAcceptance(close: () -> Unit) {
    val context = LocalContext.current
    val rootView = LocalView.current
    val sizes = listOf(200 to 200, 1200 to 800, 800 to 1200, 2000 to 10, 10 to 2000, 10 to 10)
    var imageIndex by remember { mutableIntStateOf(0) }
    var editorCheck by remember { mutableStateOf("Not run") }
    fun editor(view: View): ComposerEditText? = if (view is ComposerEditText) view
        else (view as? ViewGroup)?.let { group -> (0 until group.childCount).firstNotNullOfOrNull { editor(group.getChildAt(it)) } }
    var sent by remember { mutableStateOf(emptyList<String>()) }; var fail by remember { mutableStateOf(false) }
    val controller = remember { ComposerController(context, File(context.filesDir, "composer-qa-only")) { _, part ->
        if (fail && part.file != null) false else { sent = sent + (part.text ?: "[${part.file!!.name}]"); true }
    } }
    var peer by remember { mutableStateOf("qa-a") }; var legacy by remember(peer) { mutableStateOf("") }
    val documents by controller.drafts.collectAsState(); val height by controller.height.collectAsState()
    val clipboard = context.getSystemService(ClipboardManager::class.java)
    val before = remember { clipboard.primaryClip }
    DisposableEffect(Unit) { onDispose { controller.close(); if (before == null) clipboard.clearPrimaryClip() else clipboard.setPrimaryClip(before) } }
    fun fixture(image: Boolean): android.net.Uri {
        val directory = File(context.cacheDir, "shared/acceptance").apply { mkdirs() }
        val dimensions = sizes[imageIndex]
        val file = File(directory, if (image) "QA-${dimensions.first}x${dimensions.second}.png" else "QA-report.pdf")
        if (image) {
            val bitmap = Bitmap.createBitmap(dimensions.first, dimensions.second, Bitmap.Config.ARGB_8888)
            val canvas = Canvas(bitmap); canvas.drawColor(Color.rgb(255, 69, 0))
            canvas.drawRect(dimensions.first * .25f, dimensions.second * .25f, dimensions.first * .75f, dimensions.second * .75f, Paint().apply { color = Color.rgb(60, 179, 113) })
            file.outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }; bitmap.recycle()
        } else file.writeText("Generated composer QA PDF data")
        return FileProvider.getUriForFile(context, context.packageName + ".files", file)
    }
    Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding()) {
        Row { TextButton(onClick = close) { Text("QA Close") }; TextButton(onClick = { generateSequence(context) { (it as? android.content.ContextWrapper)?.baseContext }.filterIsInstance<Activity>().first().recreate() }) { Text("Recreate") }; TextButton(onClick = { controller.resize(220f, true) }) { Text("Height 220") } }
        Row {
            TextButton(onClick = { clipboard.setPrimaryClip(ClipData.newUri(context.contentResolver, "QA file", fixture(false))) }) { Text("Clipboard PDF") }
            TextButton(onClick = { clipboard.setPrimaryClip(ClipData.newUri(context.contentResolver, "QA image", fixture(true))) }) { Text("Clipboard image") }
        }
        Row {
            TextButton(onClick = { peer = if (peer == "qa-a") "qa-b" else "qa-a" }) { Text("Peer: $peer") }
            TextButton(onClick = { fail = !fail }) { Text("Fail files: $fail") }
            TextButton(onClick = { controller.edit(peer, emptyList()) }) { Text("Clear QA draft") }
        }
        Row {
            TextButton(onClick = { imageIndex = (imageIndex + 1) % sizes.size }) { Text("Image: ${sizes[imageIndex].first}x${sizes[imageIndex].second}") }
            TextButton(onClick = { controller.picked(peer, listOf(fixture(true))) }) { Text("Stage image") }
            TextButton(onClick = { editorCheck = runCatching {
                val field = requireNotNull(editor(rootView.rootView))
                val cursor = requireNotNull(field.textCursorDrawable)
                val original = android.graphics.Rect(cursor.bounds)
                val originalSize = field.textSize
                try {
                    for (size in listOf(13f, 18f)) {
                        field.textSize = size
                        val metrics = field.paint.fontMetricsInt
                        val bitmap = Bitmap.createBitmap(12, 300, Bitmap.Config.ARGB_8888)
                        cursor.setBounds(2, 0, 8, 300); cursor.draw(Canvas(bitmap))
                        val painted = (0 until bitmap.height).count { Color.alpha(bitmap.getPixel(4, it)) > 0 }
                        check(kotlin.math.abs(painted - (metrics.descent - metrics.ascent)) <= 1) { "Cursor height $painted at $size" }
                        bitmap.recycle()
                    }
                } finally { field.setTextSize(android.util.TypedValue.COMPLEX_UNIT_PX, originalSize); cursor.bounds = original }
                "PASS font cursor 13/18sp"
            }.getOrElse { "FAIL ${it.message}" } }) { Text("Check editor") }
        }
        Row {
            TextButton(onClick = { controller.resize(68f, true) }) { Text("Height min") }
            TextButton(onClick = { editorCheck = runCatching {
                val field = requireNotNull(editor(rootView.rootView))
                val metrics = field.paint.fontMetricsInt
                val center = field.baseline + (metrics.ascent + metrics.descent) / 2f
                val compact = height <= 68f && documents[peer].orEmpty().none { it.file != null }
                if (compact && field.lineCount <= 1) check(kotlin.math.abs(center - field.height / 2f) <= 2f) {
                    "Vertical center $center vs ${field.height / 2f}"
                }
                if (height >= 220f) check(field.gravity and android.view.Gravity.VERTICAL_GRAVITY_MASK == android.view.Gravity.TOP)
                "PASS alignment baseline=${field.baseline} center=$center height=${field.height} gravity=${field.gravity}"
            }.getOrElse { "FAIL ${it.message}" } }) { Text("Check alignment") }
        }
        Text("QA editor: $editorCheck")
        Text("QA height: $height")
        Text("QA document: " + documents[peer].orEmpty().joinToString(" | ") { it.text ?: "[${it.file!!.name}:${it.file.size}]" })
        Text("QA sent: " + sent.joinToString(" | "))
        Spacer(Modifier.weight(1f))
        RichComposer(controller, peer, legacy, true, true, { legacy = it }, {}, { controller.picked(peer, listOf(fixture(false))) })
    }
}
