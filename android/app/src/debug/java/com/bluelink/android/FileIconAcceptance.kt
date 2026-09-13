package com.bluelink.android

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bluelink.android.ui.content.FileTypeIcon
import com.bluelink.android.ui.devices.DeviceColors

/** Real-device rendering of production components; no database or transport operations. */
@Composable
internal fun FileIconAcceptance() {
    val names = listOf("report.pdf", "letter.docx", "schema.sql", "notes.md", "photo.png",
        "archive.zip", "main.tsx", "settings.yaml", "budget.xlsx", "slides.pptx",
        "music.flac", "movie.mkv", "app.apk", "unknown.bin", "font.woff2",
        "disk.qcow2", "design.psd", "model.glb", "cert.p12")
    Column(Modifier.fillMaxSize().background(DeviceColors.Canvas).systemBarsPadding()
        .verticalScroll(rememberScrollState()).padding(12.dp)) {
        Text("BlueLink · 文件图标验收", color = DeviceColors.Ink, fontSize = 16.sp)
        Text("20 / 24 / 40 dp · 当前应用主题", color = DeviceColors.Secondary, fontSize = 12.sp)
        Spacer(Modifier.height(12.dp))
        names.chunked(3).forEach { row ->
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                row.forEach { name ->
                    Column(Modifier.weight(1f).height(94.dp).background(DeviceColors.Surface).padding(6.dp),
                        horizontalAlignment = Alignment.CenterHorizontally) {
                        Row(Modifier.height(48.dp), verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(3.dp)) {
                            listOf(20, 24, 40).forEach { size -> FileTypeIcon(name, size = size.dp) }
                        }
                        Text(name, color = DeviceColors.Ink, fontSize = 11.sp, maxLines = 1)
                    }
                }
                repeat(3 - row.size) { Spacer(Modifier.weight(1f)) }
            }
            Spacer(Modifier.height(6.dp))
        }
    }
}
