package com.bluelink.android.ui.settings

import android.bluetooth.BluetoothManager
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.text.font.FontWeight
import com.bluelink.android.R
import com.bluelink.android.data.local.AppSettings
import com.bluelink.android.domain.*
import com.bluelink.android.ui.components.BlueLinkPrompt
import com.bluelink.android.ui.content.ContentIconButton
import com.bluelink.android.ui.devices.*

@android.annotation.SuppressLint("MissingPermission")
@Composable
internal fun ConnectionSettings(modifier: Modifier, settings: AppSettings, conversations: List<ConversationSummary>,
                                bluetoothAccess: BluetoothAccessState, changeBluetooth: () -> Unit,
                                save: (AppSettings) -> Unit, select: (Int) -> Unit) = DeviceScreenTheme {
    val context = LocalContext.current
    val systemName = remember(bluetoothAccess) { runCatching { if (bluetoothAccess == BluetoothAccessState.READY || bluetoothAccess == BluetoothAccessState.OFF) context.getSystemService(BluetoothManager::class.java)?.adapter?.name else null }.getOrNull().orEmpty() }
    val localName = settings.localDeviceName.ifBlank { systemName.ifBlank { "Android" } }
    var editName by remember { mutableStateOf(false) }
    var draft by remember { mutableStateOf("") }
    Column(modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp, 24.dp),
        verticalArrangement = Arrangement.spacedBy(24.dp)) {
        SettingsGroup(stringResource(R.string.connection_device)) {
            SettingsValueRow(stringResource(R.string.connection_name), localName) { draft = localName; editName = true }
        }
        SettingsGroup(stringResource(R.string.connection_behavior)) {
            SettingsValueRow(stringResource(R.string.connection_bluetooth), stringResource(
                if (bluetoothAccess.canUseBluetooth) R.string.bluetooth_enabled else R.string.bluetooth_action)) { changeBluetooth() }
            SettingsDivider()
            UsbSettingsRow(settings, save)
            SettingsDivider()
            SettingsToggleRow(stringResource(R.string.scan_on_startup), settings.scanOnStartup) { save(settings.copy(scanOnStartup = it)) }
            SettingsDivider()
            SettingsToggleRow(stringResource(R.string.connection_discoverable), settings.allowDiscovery) { save(settings.copy(allowDiscovery = it)) }
            SettingsDivider()
            SettingsToggleRow(stringResource(R.string.connection_auto_connect), settings.autoConnectTrustedDevices) { save(settings.copy(autoConnectTrustedDevices = it)) }
            SettingsDivider()
            SettingsToggleRow(stringResource(R.string.connection_reconnect), settings.reconnectAfterDisconnect) { save(settings.copy(reconnectAfterDisconnect = it)) }
        }
        SettingsGroup(stringResource(R.string.connection_trust_management)) {
            SettingsValueRow(stringResource(R.string.connection_trusted), stringResource(R.string.connection_device_count, conversations.count { it.isTrusted })) { select(SETTINGS_TRUSTED) }
        }
    }
    if (editName) BlueLinkPrompt(stringResource(R.string.connection_edit_name), { editName = false }, footer = {
        OutlinedButton(onClick = { editName = false }, shape = RoundedCornerShape(11.dp), border = BorderStroke(1.dp, DeviceColors.Border),
            colors = ButtonDefaults.outlinedButtonColors(contentColor = DeviceColors.Ink)) { Text(stringResource(R.string.cancel), fontSize = 14.sp, fontWeight = FontWeight.Normal) }
        Button(onClick = { save(settings.copy(localDeviceName = draft.trim())); editName = false }, enabled = DeviceNamePolicy.isValid(draft),
            shape = RoundedCornerShape(11.dp)) { Text(stringResource(R.string.connection_save), fontSize = 14.sp, fontWeight = FontWeight.Normal) }
    }) {
        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(stringResource(R.string.connection_name_label), color = DeviceColors.Secondary, fontSize = 13.sp)
            OutlinedTextField(draft, { draft = it }, Modifier.fillMaxWidth(), singleLine = true, shape = RoundedCornerShape(10.dp),
                isError = !DeviceNamePolicy.isValid(draft))
            Text(stringResource(R.string.connection_name_description), color = DeviceColors.Secondary, fontSize = 12.sp)
        }
    }
}
