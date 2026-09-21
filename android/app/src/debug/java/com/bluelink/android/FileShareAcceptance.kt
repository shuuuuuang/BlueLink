package com.bluelink.android

import android.content.Intent
import android.net.Uri
import android.provider.OpenableColumns
import androidx.compose.foundation.layout.*
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import com.bluelink.android.domain.*
import com.bluelink.android.files.FileInteraction
import com.bluelink.android.ui.files.TransferActionSheet
import java.io.File
import java.util.UUID

/** Shares only generated QA fixtures, never user files or messages. */
@Composable
internal fun FileShareAcceptance(scene: String, close: () -> Unit) {
    val context = LocalContext.current
    val image = scene.endsWith("image")
    val attachment = remember(scene) {
        val file = File(context.cacheDir, "shared/" + if (image) "qa-storage.png" else "qa-storage.pdf")
        file.parentFile?.mkdirs()
        if (image) context.assets.open("bluelink-final-logo.png").use { source -> file.outputStream().use(source::copyTo) }
        else {
            val document = android.graphics.pdf.PdfDocument()
            try {
                val page = document.startPage(android.graphics.pdf.PdfDocument.PageInfo.Builder(300, 200, 1).create())
                page.canvas.drawText("BlueLink sharing acceptance", 20f, 50f, android.graphics.Paint())
                document.finishPage(page); file.outputStream().use(document::writeTo)
            } finally { document.close() }
        }
        ChatAttachment(UUID.randomUUID(), UUID.randomUUID(), if (image) "BlueLink-QA.png" else "BlueLink-QA.pdf",
            if (image) "image/png" else "application/pdf", file.length(), Uri.fromFile(file).toString(), "COMPLETED")
    }
    var result by remember { mutableStateOf("RUNNING") }
    var menu by remember { mutableStateOf(true) }
    LaunchedEffect(attachment) {
        try {
            val intent = FileInteraction.shareIntent(context, attachment)
            val uri = requireNotNull(intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java))
            check(uri.scheme == "content" && intent.clipData?.getItemAt(0)?.uri == uri)
            check(intent.flags and Intent.FLAG_GRANT_READ_URI_PERMISSION != 0)
            check(intent.type == attachment.mimeType)
            context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)!!.use {
                check(it.moveToFirst() && it.getString(0) == attachment.fileName && it.getLong(1) == attachment.sizeBytes)
            }
            context.contentResolver.openInputStream(uri)!!.use { check(it.readBytes().size.toLong() == attachment.sizeBytes) }
            val chooser = FileInteraction.shareChooserIntent(context, attachment)
            check(chooser.action == Intent.ACTION_CHOOSER)
            val shared = requireNotNull(chooser.getParcelableExtra(Intent.EXTRA_INTENT, Intent::class.java))
            check(shared.action == Intent.ACTION_SEND && shared.`package` == null && shared.component == null)
            check(shared.type == attachment.mimeType && shared.clipData?.getItemAt(0)?.uri == uri)
            check(shared.flags and Intent.FLAG_GRANT_READ_URI_PERMISSION != 0)
            check(chooser.flags and Intent.FLAG_GRANT_READ_URI_PERMISSION != 0)
            check(chooser.clipData?.getItemAt(0)?.uri == uri)
            check(!chooser.hasExtra(Intent.EXTRA_INITIAL_INTENTS) && !chooser.hasExtra(Intent.EXTRA_EXCLUDE_COMPONENTS))
            result = "PASSED: content URI, read grant, MIME, original filename, bytes, native chooser"
        } catch (failure: Exception) { result = "FAILED: ${failure.message}" }
    }
    Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding()) {
        Text(result)
        TextButton(onClick = { menu = true }) { Text("QA file menu") }
        TextButton(onClick = {
            val value = context.getSystemService(android.content.ClipboardManager::class.java).primaryClip
            result = if (value?.description?.label == "BlueLink" && value.getItemAt(0).text?.toString() == attachment.fileName)
                "PASSED: original file name copied" else "FAILED: file name copy mismatch"
        }) { Text("QA Check filename") }
        TextButton(onClick = close) { Text("QA Close") }
    }
    if (menu) TransferActionSheet(TransferItem(attachment.transferId, attachment.fileName, attachment.sizeBytes,
        attachment.sizeBytes, false, TransferStatus.COMPLETED, mimeType = attachment.mimeType, localUri = attachment.localUri),
        true, { menu = false }) { action ->
        if (action == TransferAction.SHARE) FileInteraction.share(context, attachment)
    }
}
