package com.bluelink.android
import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import com.bluelink.android.domain.ChatAttachment
import com.bluelink.android.files.FileInteraction
import java.io.File
import java.util.UUID

@Composable
internal fun MultiFileShareAcceptance(close:()->Unit) {
    val context=LocalContext.current
    val attachments=remember {
        val folder=File(context.cacheDir,"shared/acceptance-multiple").apply {mkdirs()}
        val png=File(folder,"QA-image.png")
        context.assets.open("bluelink-final-logo.png").use {input->png.outputStream().use(input::copyTo)}
        val pdf=File(folder,"QA-document.pdf")
        val document=android.graphics.pdf.PdfDocument()
        try { val page=document.startPage(android.graphics.pdf.PdfDocument.PageInfo.Builder(300,200,1).create())
            page.canvas.drawText("BlueLink QA",20f,50f,android.graphics.Paint());document.finishPage(page)
            pdf.outputStream().use(document::writeTo)
        } finally { document.close() }
        listOf(png to "image/png",pdf to "application/pdf").map { (f,type)->ChatAttachment(UUID.randomUUID(),UUID.randomUUID(),f.name,type,f.length(),Uri.fromFile(f).toString(),"COMPLETED") }
    }
    var report by remember {mutableStateOf("QA checking")}
    LaunchedEffect(Unit) {
        try {
            val intent=FileInteraction.multiShareIntent(context,attachments)
            check(intent.action==Intent.ACTION_SEND_MULTIPLE && intent.type=="*/*")
            val uris=intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM,Uri::class.java)!!
            check(uris.size==2 && intent.clipData?.itemCount==2)
            uris.forEachIndexed { index,uri->
                check(uri.scheme=="content" && intent.clipData!!.getItemAt(index).uri==uri)
                context.contentResolver.openInputStream(uri)!!.use {check(it.readBytes().size.toLong()==attachments[index].sizeBytes)}
            }
            val chooser=Intent.createChooser(intent,null)
            check(chooser.flags and Intent.FLAG_GRANT_READ_URI_PERMISSION != 0 && chooser.clipData?.itemCount==2)
            check(!chooser.hasExtra(Intent.EXTRA_INITIAL_INTENTS) && !chooser.hasExtra(Intent.EXTRA_EXCLUDE_COMPONENTS))
            report="PASSED: 2 files, content URIs, exact bytes, read grants, mixed MIME, native chooser"
        } catch(error:Exception) {report="FAILED: "+error}
    }
    Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding()) {
        Text(report)
        TextButton(onClick={context.startActivity(Intent.createChooser(FileInteraction.multiShareIntent(context,attachments),null))}) {Text("QA Share 2 files")}
        TextButton(onClick=close) {Text("QA Close")}
    }
}
