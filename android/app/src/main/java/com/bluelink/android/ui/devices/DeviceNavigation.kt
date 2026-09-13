package com.bluelink.android.ui.devices

import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ColorFilter
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bluelink.android.R
import com.bluelink.android.domain.BluetoothAccessState
import com.bluelink.android.ui.components.BlueLinkLogo

@Composable
internal fun FigmaIcon(asset: Int, modifier: Modifier = Modifier, size: Dp = 24.dp, tint: Color? = null) {
    Image(painterResource(asset), contentDescription = null, modifier = modifier.size(size),
        colorFilter = tint?.let(ColorFilter::tint))
}

@Composable
internal fun HomePageHeader(title: String, trailing: @Composable () -> Unit = {}) {
    Row(Modifier.fillMaxWidth().background(DeviceColors.Surface).heightIn(min = 64.dp).padding(horizontal = 16.dp),
        verticalAlignment = Alignment.CenterVertically) {
        BlueLinkLogo(Modifier.size(34.dp))
        Text(title, Modifier.weight(1f), fontSize = 18.sp, lineHeight = 26.sp,
            fontFamily = DeviceFont, fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
        trailing()
    }
}

@Composable
fun DeviceHomeHeader(access: BluetoothAccessState, connectedCount: Int, title: String = stringResource(R.string.content_devices_conversations)) = DeviceScreenTheme {
    val context = LocalContext.current
    HomePageHeader(title) {
        val connected = connectedCount > 0
        Surface(color = if (connected) DeviceColors.Success.copy(alpha = .08f) else MaterialTheme.colorScheme.surfaceVariant,
            shape = RoundedCornerShape(17.dp)) {
            Row(Modifier.widthIn(min = 132.dp, max = 164.dp).heightIn(min = 34.dp).padding(horizontal = 11.dp, vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically) {
                FigmaIcon(if (connected) R.drawable.figma_connected else if (access == BluetoothAccessState.READY)
                    R.drawable.figma_connected_zero else R.drawable.figma_bluetooth_off,
                    size = 18.dp)
                Spacer(Modifier.width(5.dp))
                Text(if (connected) context.resources.getQuantityString(R.plurals.content_connected_count, connectedCount, connectedCount) else when (access) {
                    BluetoothAccessState.REQUIRED -> stringResource(R.string.device_permission_short)
                    BluetoothAccessState.DENIED -> stringResource(R.string.content_permission_off)
                    BluetoothAccessState.OFF -> stringResource(R.string.device_bluetooth_off)
                    BluetoothAccessState.READY -> context.resources.getQuantityString(R.plurals.content_connected_count, connectedCount, connectedCount)
                }, fontFamily = DeviceFont, fontSize = 12.sp, lineHeight = 18.sp, maxLines = 1, overflow = TextOverflow.Ellipsis,
                    color = if (connected) DeviceColors.Success else DeviceColors.Secondary)
            }
        }
    }
}

@Composable
fun DeviceBottomNavigation(selected: Int, onSelect: (Int) -> Unit) = DeviceScreenTheme {
    Column(Modifier.background(DeviceColors.Surface)) {
        HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant)
        Row(Modifier.fillMaxWidth().heightIn(min = 63.dp).selectableGroup()) {
            val entries = listOf(stringResource(R.string.conversations) to if (selected == 0) R.drawable.figma_nav_conversation else R.drawable.figma_nav_conversation_off,
                stringResource(R.string.files) to if (selected == 1) R.drawable.figma_nav_files_on else R.drawable.figma_nav_files,
                stringResource(R.string.settings) to if (selected == 2) R.drawable.figma_nav_settings_on else R.drawable.figma_nav_settings)
            entries.forEachIndexed { index, (label, icon) ->
                val color = if (index == selected) DeviceColors.Blue else DeviceColors.Secondary
                Column(Modifier.weight(1f).selectable(selected = index == selected, role = Role.Tab,
                    onClick = { onSelect(index) }).padding(top = 11.dp, bottom = 5.dp),
                    horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    // Selected exports contain white details inside the blue
                    // glyph. A flat tint erases those original Figma details.
                    FigmaIcon(icon, tint = if (index == selected) null else color)
                    Text(label, color = color, fontFamily = DeviceFont, fontSize = 11.sp, lineHeight = 20.sp)
                }
            }
        }
    }
}
