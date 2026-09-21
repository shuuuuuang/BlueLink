package com.bluelink.android

import android.graphics.Bitmap
import android.graphics.Color
import android.net.Uri
import android.widget.Toast
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.platform.LocalContext
import com.bluelink.android.domain.ChatAttachment
import com.bluelink.android.ui.conversation.ImagePreview
import java.io.File
import java.util.UUID
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

@Composable
internal fun ImageNavigationAcceptance(close: () -> Unit) {
    val context = LocalContext.current
    val images by produceState<List<ChatAttachment>>(emptyList()) {
        value = withContext(Dispatchers.IO) {
            listOf(Color.rgb(70, 140, 220), Color.rgb(70, 180, 130), Color.rgb(230, 150, 70)).mapIndexed { index, color ->
                val file = File(context.cacheDir, "shared/qa-gallery/$index.png").apply { parentFile?.mkdirs() }
                val bitmap = Bitmap.createBitmap(900, 600, Bitmap.Config.ARGB_8888)
                bitmap.eraseColor(color)
                file.outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }; bitmap.recycle()
                ChatAttachment(UUID(0, index.toLong()+1), UUID(0, index.toLong()+1), "Gallery-${index+1}.png", "image/png", file.length(), Uri.fromFile(file).toString(), "COMPLETED")
            }
        }
    }
    var index by remember { mutableIntStateOf(1) }
    if (images.isEmpty()) Text("QA loading")
    else ImagePreview(images[index], loadAdjacent = { step -> images.getOrNull(index + step) },
        selectImage = { selected -> index = images.indexOfFirst { it.transferId == selected.transferId } }, dismiss = close)
}
