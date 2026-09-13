package com.bluelink.android.ui.settings

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bluelink.android.R
import com.bluelink.android.domain.ReceiveLimitPolicy
import com.bluelink.android.ui.components.BlueLinkPrompt
import com.bluelink.android.ui.devices.DeviceColors

/** Figma Android/Dialog/ReceiveLimit 1270:95; common prompt preserves theme and keyboard insets. */
@Composable
internal fun ReceiveLimitEditor(bytes: Long, enabled: Boolean, dismiss: () -> Unit, save: (Long) -> Unit) {
    var draft by rememberSaveable(bytes) { mutableStateOf(ReceiveLimitPolicy.initialMiB(bytes)) }
    val parsed = ReceiveLimitPolicy.parseMiB(draft)
    BlueLinkPrompt(stringResource(R.string.storage_receive_limit), dismiss, footer = {
        OutlinedButton(onClick = dismiss, shape = RoundedCornerShape(10.dp), border = BorderStroke(1.dp, DeviceColors.Border),
            colors = ButtonDefaults.outlinedButtonColors(contentColor = DeviceColors.Ink)) {
            Text(stringResource(R.string.cancel), fontSize = 14.sp, fontWeight = FontWeight.Normal)
        }
        Button(onClick = { parsed?.let(save) }, enabled = parsed != null, shape = RoundedCornerShape(10.dp)) {
            Text(stringResource(R.string.connection_save), fontSize = 14.sp, fontWeight = FontWeight.Normal)
        }
    }) {
        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(stringResource(R.string.storage_limit_maximum), color = DeviceColors.Secondary, fontSize = 13.sp)
            OutlinedTextField(draft, { draft = it }, Modifier.fillMaxWidth(), singleLine = true,
                shape = RoundedCornerShape(10.dp), keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                suffix = { Text("MiB", color = DeviceColors.Secondary, fontSize = 13.sp) }, isError = parsed == null)
            if (parsed == null) Text(stringResource(R.string.storage_limit_invalid), color = DeviceColors.Error, fontSize = 12.sp)
            Text(stringResource(R.string.storage_limit_help), color = DeviceColors.Secondary, fontSize = 12.sp, lineHeight = 18.sp)
            if (!enabled) Text(stringResource(R.string.storage_limit_enable_note), color = DeviceColors.Secondary, fontSize = 12.sp)
        }
    }
}
