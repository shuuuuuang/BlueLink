package com.bluelink.android.ui.files

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bluelink.android.R
import com.bluelink.android.domain.FileBatchSelection
import com.bluelink.android.domain.MessageBatch
import com.bluelink.android.ui.devices.DeviceColors

@Composable
internal fun BoxScope.FileRangeSelectionButton(state: LazyListState, ids: List<String>, selected: List<String>,
    anchor: String?, enabled: Boolean, select: (String) -> Unit) {
    val currentTarget by remember(state, ids, selected, anchor) {
        derivedStateOf {
            val layout = state.layoutInfo
            val visible = layout.visibleItemsInfo.filter {
                it.key.toString() in ids && MessageBatch.fullyVisible(it.offset, it.size, layout.viewportStartOffset, layout.viewportEndOffset)
            }
            FileBatchSelection.rangeTarget(ids, selected, anchor,
                visible.firstOrNull()?.key?.toString(), visible.lastOrNull()?.key?.toString())
        }
    }
    val target = currentTarget ?: return
    val above = ids.indexOf(target) < ids.indexOf(anchor)
    Box(Modifier.align(if (above) Alignment.TopStart else Alignment.BottomStart)
        .padding(horizontal = 12.dp, vertical = 4.dp).heightIn(min = 48.dp)
        .clickable(enabled = enabled, role = Role.Button) { select(target) }, contentAlignment = Alignment.Center) {
        Surface(shape = CircleShape, color = DeviceColors.Surface, shadowElevation = 2.dp,
            border = BorderStroke(1.dp, DeviceColors.Border)) {
            Row(Modifier.heightIn(min = 32.dp).padding(horizontal = 12.dp, vertical = 5.dp),
                verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                Text(if (above) "↓" else "↑", color = DeviceColors.Secondary, fontSize = 15.sp, lineHeight = 18.sp)
                Text(LocalContext.current.getString(R.string.message_batch_select_to_here), color = DeviceColors.Secondary,
                    fontSize = 13.sp, lineHeight = 18.sp)
            }
        }
    }
}
