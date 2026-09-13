package com.bluelink.android.ui.files

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bluelink.android.R
import com.bluelink.android.domain.IncomingFileRequest
import com.bluelink.android.ui.components.BlueLinkPrompt
import com.bluelink.android.ui.devices.*

@Composable
internal fun IncomingFileConfirmation(request: IncomingFileRequest, decide: (Boolean) -> Unit) = DeviceScreenTheme {
    BlueLinkPrompt(stringResource(R.string.storage_receive_confirmation), { decide(false) }, footer = {
        OutlinedButton(onClick = { decide(false) }, border = BorderStroke(1.dp, DeviceColors.Border), shape = RoundedCornerShape(11.dp),
            colors = ButtonDefaults.outlinedButtonColors(contentColor = DeviceColors.Ink)) {
            Text(stringResource(R.string.storage_reject), fontSize = 14.sp, fontWeight = FontWeight.Normal)
        }
        Button(onClick = { decide(true) }, shape = RoundedCornerShape(11.dp)) {
            Text(stringResource(R.string.storage_accept), fontSize = 14.sp, fontWeight = FontWeight.Normal)
        }
    }) {
        Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text(stringResource(R.string.storage_receive_from, request.peerName), fontSize = 15.sp)
            Text(request.transfer.name, fontSize = 16.sp)
            Text(android.text.format.Formatter.formatShortFileSize(androidx.compose.ui.platform.LocalContext.current, request.transfer.totalBytes),
                color = DeviceColors.Secondary, fontSize = 13.sp)
        }
    }
}
