package com.bluelink.android.ui.components

import androidx.annotation.DrawableRes
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.RectangleShape
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bluelink.android.ui.devices.*

internal data class ActionOption(
    val label: String,
    @DrawableRes val icon: Int,
    val destructive: Boolean = false,
    val accent: Boolean = false,
    val enabled: Boolean = true,
    val action: () -> Unit,
)

/** Shared original sheets: 370:376, 372:153, 380:83/195/395. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun ActionSheet(title: String, detail: String, actions: List<ActionOption>, dismiss: () -> Unit,
                         detailColor: Color = DeviceColors.Secondary,
                         extraContent: (@Composable () -> Unit)? = null) {
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = DeviceColors.Surface,
        tonalElevation = 0.dp, shape = RectangleShape, scrimColor = Color(0xFF0D1729).copy(alpha = .34f),
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), dragHandle = null) {
        DeviceScreenTheme {
            Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(bottom = 12.dp)) {
                Box(Modifier.padding(top = 10.dp, bottom = 16.dp).size(56.dp, 5.dp)
                    .background(MaterialTheme.colorScheme.outlineVariant, RoundedCornerShape(3.dp))
                    .align(Alignment.CenterHorizontally))
                Text(title, Modifier.padding(horizontal = 24.dp), color = DeviceColors.Ink,
                    fontSize = 17.sp, lineHeight = 25.sp, fontWeight = FontWeight.Normal,
                    maxLines = 2, overflow = TextOverflow.Ellipsis)
                Text(detail, Modifier.padding(start = 24.dp, end = 24.dp, top = 2.dp, bottom = 14.dp),
                    color = detailColor, fontSize = 12.sp, lineHeight = 18.sp)
                extraContent?.invoke()
                HorizontalDivider(Modifier.padding(horizontal = 16.dp), color = DeviceColors.Border)
                actions.forEachIndexed { index, option ->
                    val color = when {
                        !option.enabled -> DeviceColors.Secondary.copy(alpha = .55f)
                        option.destructive -> DeviceColors.Error
                        option.accent -> DeviceColors.Blue
                        else -> DeviceColors.Ink
                    }
                    Row(Modifier.fillMaxWidth().heightIn(min = 52.dp)
                        .clickable(enabled = option.enabled, role = Role.Button) { dismiss(); option.action() }
                        .padding(horizontal = 22.dp, vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                        FigmaIcon(option.icon, size = 22.dp, tint = color)
                        Text(option.label, Modifier.weight(1f).padding(start = 14.dp), color = color,
                            fontSize = 15.sp, lineHeight = 22.sp, fontWeight = FontWeight.Normal)
                    }
                    if (index < actions.lastIndex) HorizontalDivider(Modifier.padding(start = 58.dp, end = 24.dp),
                        color = MaterialTheme.colorScheme.outlineVariant)
                }
            }
        }
    }
}
