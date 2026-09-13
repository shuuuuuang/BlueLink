package com.bluelink.android.ui.components

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.compose.ui.window.DialogWindowProvider
import com.bluelink.android.R
import com.bluelink.android.ui.content.ContentIconButton
import com.bluelink.android.ui.devices.*

/** Android information/confirmation primitives 424:126 / 424:2670. */
@Composable
internal fun BlueLinkPrompt(title: String, dismiss: () -> Unit,
                            footer: (@Composable RowScope.() -> Unit)? = null,
                            content: @Composable ColumnScope.() -> Unit) {
    val localizedContext = LocalContext.current
    val localizedConfiguration = LocalConfiguration.current
    Dialog(onDismissRequest = dismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        CompositionLocalProvider(LocalContext provides localizedContext, LocalConfiguration provides localizedConfiguration) {
            val dialogWindow = (LocalView.current.parent as? DialogWindowProvider)?.window
            SideEffect { dialogWindow?.setDimAmount(.34f) }
            DeviceScreenTheme {
                Box(Modifier.fillMaxWidth().padding(horizontal = 28.dp), contentAlignment = Alignment.Center) {
                    Surface(color = DeviceColors.Surface, shape = RoundedCornerShape(18.dp),
                        modifier = Modifier.widthIn(max = 356.dp).fillMaxWidth()
                            .heightIn(max = (LocalConfiguration.current.screenHeightDp - 64).coerceAtLeast(240).dp)) {
                        Column {
                            Row(Modifier.fillMaxWidth().heightIn(min = 68.dp).padding(start = 24.dp, end = 12.dp),
                                verticalAlignment = Alignment.CenterVertically) {
                                Text(title, Modifier.weight(1f).padding(vertical = 16.dp), fontSize = 19.sp, lineHeight = 27.sp)
                                ContentIconButton(R.drawable.figma_content_close, stringResource(R.string.close),
                                    Modifier.size(40.dp), action = dismiss)
                            }
                            HorizontalDivider(color = DeviceColors.Border)
                            Column(Modifier.weight(1f, fill = false).verticalScroll(rememberScrollState())
                                .padding(24.dp), verticalArrangement = Arrangement.spacedBy(24.dp), content = content)
                            if (footer != null) {
                                HorizontalDivider(color = DeviceColors.Border)
                                Row(Modifier.fillMaxWidth().padding(horizontal = 24.dp, vertical = 16.dp),
                                    horizontalArrangement = Arrangement.spacedBy(12.dp, Alignment.End),
                                    verticalAlignment = Alignment.CenterVertically, content = footer)
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
internal fun BlueLinkConfirmation(title: String, message: String, note: String?, confirmLabel: String,
                                  dismiss: () -> Unit, confirm: () -> Unit) {
    BlueLinkPrompt(title, dismiss, footer = {
        OutlinedButton(onClick = dismiss, shape = RoundedCornerShape(11.dp),
            border = BorderStroke(1.dp, DeviceColors.Border),
            colors = ButtonDefaults.outlinedButtonColors(contentColor = DeviceColors.Ink),
            modifier = Modifier.heightIn(min = 44.dp)) { Text(stringResource(R.string.cancel), fontSize = 14.sp, fontWeight = FontWeight.Normal) }
        Button(onClick = confirm, shape = RoundedCornerShape(11.dp), modifier = Modifier.heightIn(min = 44.dp),
            colors = ButtonDefaults.buttonColors(containerColor = Color(0xFFDC3545), contentColor = Color.White)) {
            Text(confirmLabel, fontSize = 14.sp, fontWeight = FontWeight.Normal)
        }
    }) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(52.dp).background(DeviceColors.Error.copy(alpha = .08f), RoundedCornerShape(14.dp)),
                contentAlignment = Alignment.Center) {
                FigmaIcon(R.drawable.figma_dialog_warning, size = 24.dp, tint = DeviceColors.Error)
            }
            Text(message, Modifier.weight(1f).padding(start = 14.dp), fontSize = 16.sp, lineHeight = 24.sp)
        }
        if (!note.isNullOrBlank()) {
            val light = DeviceColors.Surface == Color.White
            Text(note, Modifier.fillMaxWidth().background(if (light) Color(0xFFFFF7E7) else Color(0xFF362D1D),
                RoundedCornerShape(12.dp)).padding(14.dp, 16.dp), fontSize = 13.sp, lineHeight = 20.sp,
                color = if (light) Color(0xFF9A6500) else Color(0xFFFFD58B))
        }
    }
}

@Composable
internal fun PromptField(label: String, value: String, valueColor: Color = DeviceColors.Ink, divider: Boolean = true) {
    Column(Modifier.fillMaxWidth()) {
        Text(label, fontSize = 12.sp, lineHeight = 18.sp, color = DeviceColors.Secondary)
        Spacer(Modifier.height(2.dp))
        Text(value, fontSize = 14.sp, lineHeight = 20.sp, color = valueColor)
        if (divider) HorizontalDivider(Modifier.padding(top = 6.dp), color = MaterialTheme.colorScheme.outlineVariant)
    }
}
