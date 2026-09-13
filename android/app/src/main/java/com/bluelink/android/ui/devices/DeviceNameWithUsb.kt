package com.bluelink.android.ui.devices

import androidx.compose.foundation.layout.*
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bluelink.android.R

/** Shrink the name before the indicator; short names keep the icon directly beside them. */
@Composable
internal fun DeviceNameWithUsb(name: String, ready: Boolean, fontSize: TextUnit = 14.sp, lineHeight: TextUnit = 24.sp) {
    val description = stringResource(R.string.usb_session_ready)
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Text(name, modifier = Modifier.weight(1f, fill = false), color = DeviceColors.Ink,
            fontSize = fontSize, lineHeight = lineHeight, maxLines = 1, overflow = TextOverflow.Ellipsis)
        if (ready) {
            Spacer(Modifier.width(6.dp))
            FigmaIcon(R.drawable.figma_usb_ready, Modifier.semantics { contentDescription = description },
                size = 16.dp, tint = DeviceColors.Blue)
        }
    }
}
