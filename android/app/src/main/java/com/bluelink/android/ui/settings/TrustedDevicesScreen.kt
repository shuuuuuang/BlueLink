package com.bluelink.android.ui.settings

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.text.font.FontWeight
import com.bluelink.android.R
import com.bluelink.android.domain.ConversationSummary
import com.bluelink.android.ui.components.BlueLinkConfirmation
import com.bluelink.android.ui.devices.*
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

@Composable
internal fun TrustedDevicesScreen(modifier: Modifier, conversations: List<ConversationSummary>, forget: (String) -> Unit, forgetAll: () -> Unit) = DeviceScreenTheme {
    val peers = conversations.filter { it.isTrusted }
    var remove by remember { mutableStateOf<ConversationSummary?>(null) }
    var all by remember { mutableStateOf(false) }
    Column(modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp, 24.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
            Text(stringResource(R.string.connection_trusted_count, peers.size), fontSize = 13.sp, lineHeight = 22.sp, color = DeviceColors.Secondary)
            Text(stringResource(R.string.connection_trusted_description), fontSize = 12.sp, lineHeight = 20.sp, color = DeviceColors.Secondary)
        }
        Spacer(Modifier.height(2.dp))
        if (peers.isEmpty()) TrustedDevicesEmpty()
        peers.forEach { peer ->
            Surface(shape = RoundedCornerShape(12.dp), color = DeviceColors.Surface, border = BorderStroke(1.dp, DeviceColors.Border)) {
                Row(Modifier.fillMaxWidth().padding(12.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    Surface(shape = RoundedCornerShape(12.dp), color = DeviceColors.Selected) {
                        Box(Modifier.size(42.dp), contentAlignment = Alignment.Center) { FigmaIcon(R.drawable.figma_settings_connections, size = 24.dp, tint = DeviceColors.Blue) }
                    }
                    Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                        Text(peer.peerName, fontSize = 15.sp)
                        val time = peer.lastConnectedAt?.let { DateTimeFormatter.ofPattern("MM-dd HH:mm").withZone(ZoneId.systemDefault()).format(Instant.ofEpochMilli(it)) }
                            ?: stringResource(R.string.connection_no_connection_time)
                        Text(stringResource(R.string.connection_last_connected, time), color = DeviceColors.Secondary, fontSize = 11.sp)
                    }
                    OutlinedButton(onClick = { remove = peer }, shape = RoundedCornerShape(10.dp), contentPadding = PaddingValues(12.dp, 6.dp),
                        border = BorderStroke(1.dp, DeviceColors.Error), colors = ButtonDefaults.outlinedButtonColors(contentColor = DeviceColors.Error)) {
                        Text(stringResource(R.string.connection_remove), fontSize = 12.sp, fontWeight = FontWeight.Normal)
                    }
                }
            }
        }
        if (peers.isNotEmpty()) OutlinedButton(onClick = { all = true }, Modifier.fillMaxWidth().padding(top = 16.dp).heightIn(min = 48.dp),
            shape = RoundedCornerShape(10.dp), border = BorderStroke(1.dp, DeviceColors.Error),
            colors = ButtonDefaults.outlinedButtonColors(contentColor = DeviceColors.Error)) { Text(stringResource(R.string.connection_remove_all), fontWeight = FontWeight.Normal, fontSize = 14.sp) }
    }
    if (all || remove != null) BlueLinkConfirmation(
        stringResource(if (all) R.string.connection_remove_all else R.string.connection_remove_device),
        if (all) stringResource(R.string.connection_remove_all_message) else stringResource(R.string.connection_remove_message, remove!!.peerName),
        stringResource(R.string.connection_remove_note), stringResource(R.string.connection_remove),
        dismiss = { all = false; remove = null }, confirm = {
            if (all) forgetAll() else remove?.let { forget(it.peerId) }
            all = false; remove = null
        })
}

/** Original 567:4086; keeps the empty list panel separate from informational notices. */
@Composable
private fun TrustedDevicesEmpty() {
    val outline = DeviceColors.Border
    Column(Modifier.fillMaxWidth().heightIn(min = 250.dp)
        .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(12.dp))
        .drawBehind {
            drawRoundRect(outline, cornerRadius = CornerRadius(12.dp.toPx()),
                style = Stroke(1.dp.toPx(), pathEffect = PathEffect.dashPathEffect(floatArrayOf(4.dp.toPx(), 4.dp.toPx()))))
        }.padding(horizontal = 16.dp, vertical = 40.dp), horizontalAlignment = Alignment.CenterHorizontally) {
        Box(Modifier.size(64.dp), contentAlignment = Alignment.Center) {
            Box(Modifier.size(32.dp).background(DeviceColors.Canvas, RoundedCornerShape(10.dp)), contentAlignment = Alignment.Center) {
                FigmaIcon(R.drawable.figma_generic, size = 24.dp, tint = DeviceColors.Secondary)
            }
        }
        Spacer(Modifier.height(14.dp))
        Text(stringResource(R.string.connection_trusted_empty), fontSize = 18.sp, lineHeight = 30.sp, textAlign = TextAlign.Center)
        Spacer(Modifier.height(3.dp))
        Text(stringResource(R.string.connection_trusted_empty_description), fontSize = 13.sp, lineHeight = 24.sp,
            color = DeviceColors.Secondary, textAlign = TextAlign.Center)
    }
}
