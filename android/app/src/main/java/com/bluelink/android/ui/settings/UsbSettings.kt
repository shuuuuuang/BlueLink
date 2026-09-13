package com.bluelink.android.ui.settings

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.selection.toggleable
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bluelink.android.R
import com.bluelink.android.data.local.AppSettings
import com.bluelink.android.ui.devices.DeviceColors

@Composable
internal fun UsbSettingsRow(settings: AppSettings, save: (AppSettings) -> Unit) {
    Row(Modifier.fillMaxWidth().toggleable(settings.usbTransferEnabled, role = Role.Switch,
        onValueChange = { save(settings.copy(usbTransferEnabled = it)) }).padding(horizontal = 16.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text(stringResource(R.string.usb_title), fontSize = 15.sp, lineHeight = 22.sp)
            Text(stringResource(R.string.usb_automatic_description), color = DeviceColors.Secondary, fontSize = 12.sp, lineHeight = 18.sp)
        }
        Spacer(Modifier.width(12.dp))
        SettingsSwitchVisual(settings.usbTransferEnabled)
    }
}
