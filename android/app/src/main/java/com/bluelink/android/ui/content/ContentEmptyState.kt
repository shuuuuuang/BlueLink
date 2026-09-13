package com.bluelink.android.ui.content

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bluelink.android.R
import com.bluelink.android.ui.devices.*

/** Original Android/Search/EmptyState 503:76; naturally grows for longer text. */
@Composable
internal fun ContentSearchEmpty(title: String, description: String, modifier: Modifier = Modifier) {
    Column(modifier.fillMaxWidth().heightIn(min = 202.dp)
        .background(DeviceColors.Surface, RoundedCornerShape(14.dp))
        .border(1.dp, DeviceColors.Border, RoundedCornerShape(14.dp))
        .padding(horizontal = 16.dp, vertical = 27.dp), horizontalAlignment = Alignment.CenterHorizontally) {
        FigmaIcon(R.drawable.figma_search_empty, size = 64.dp, tint = DeviceColors.Secondary)
        Spacer(Modifier.height(13.dp))
        Text(title, fontSize = 17.sp, lineHeight = 25.sp, textAlign = TextAlign.Center)
        Spacer(Modifier.height(7.dp))
        Text(description, fontSize = 13.sp, lineHeight = 20.sp, color = DeviceColors.Secondary, textAlign = TextAlign.Center)
    }
}

/** Original Android/File/EmptyInline 514:76. */
@Composable
internal fun ContentFilesEmpty(modifier: Modifier = Modifier, conversation: Boolean = true) {
    val context = LocalContext.current
    Column(modifier.fillMaxWidth().padding(horizontal = 16.dp), horizontalAlignment = Alignment.CenterHorizontally) {
        FigmaIcon(R.drawable.figma_file_empty, size = 80.dp, tint = DeviceColors.Secondary)
        Spacer(Modifier.height(22.dp))
        Text(context.getString(R.string.content_no_files), fontSize = 18.sp, lineHeight = 26.sp)
        Spacer(Modifier.height(6.dp))
        Text(context.getString(if (conversation) R.string.content_no_files_body else R.string.content_no_files_global_body), fontSize = 13.sp, lineHeight = 20.sp,
            color = DeviceColors.Secondary, textAlign = TextAlign.Center)
    }
}
