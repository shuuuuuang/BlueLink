package com.bluelink.android.ui.settings

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.res.stringResource
import com.bluelink.android.R
import com.bluelink.android.ui.devices.*

internal const val SETTINGS_HOME = -2
internal const val SETTINGS_GENERAL = -1
internal const val SETTINGS_HELP = 4
internal const val SETTINGS_HELP_CONNECTION = 5
internal const val SETTINGS_HELP_MESSAGES = 6
internal const val SETTINGS_HELP_FAQ = 7
internal const val SETTINGS_PRIVACY_POLICY = 8
internal const val SETTINGS_AGREEMENT = 9
internal const val SETTINGS_LICENSES = 10
internal const val SETTINGS_TRUSTED = 11

internal fun settingsParent(page: Int) = when (page) {
    SETTINGS_TRUSTED -> 0
    SETTINGS_HELP_CONNECTION, SETTINGS_HELP_MESSAGES, SETTINGS_HELP_FAQ -> SETTINGS_HELP
    SETTINGS_HELP, SETTINGS_PRIVACY_POLICY, SETTINGS_AGREEMENT, SETTINGS_LICENSES -> 3
    else -> SETTINGS_HOME
}

@Composable
internal fun settingsTitle(page: Int): String = stringResource(when (page) {
    SETTINGS_HOME -> R.string.settings
    SETTINGS_GENERAL -> R.string.general
    0 -> R.string.connections
    1 -> R.string.files_storage
    2 -> R.string.privacy_data
    SETTINGS_HELP -> R.string.support_help_feedback
    SETTINGS_HELP_CONNECTION -> R.string.support_connection
    SETTINGS_HELP_MESSAGES -> R.string.support_messages
    SETTINGS_HELP_FAQ -> R.string.support_faq
    SETTINGS_PRIVACY_POLICY -> R.string.support_privacy
    SETTINGS_AGREEMENT -> R.string.support_agreement
    SETTINGS_LICENSES -> R.string.support_licenses
    SETTINGS_TRUSTED -> R.string.connection_trusted
    else -> R.string.about
})

@Composable
internal fun SettingsHeader(page: Int, back: () -> Unit) = DeviceScreenTheme {
    if (page == SETTINGS_HOME) {
        HomePageHeader(settingsTitle(page))
    } else Surface(color = DeviceColors.Surface) {
        Column {
            Box(Modifier.fillMaxWidth().heightIn(min = 58.dp)) {
                IconButton(onClick = back, modifier = Modifier.align(Alignment.CenterStart)) {
                    androidx.compose.foundation.Image(androidx.compose.ui.res.painterResource(R.drawable.figma_content_back),
                        stringResource(R.string.back), Modifier.size(24.dp),
                        colorFilter = androidx.compose.ui.graphics.ColorFilter.tint(DeviceColors.Ink))
                }
                Text(settingsTitle(page), Modifier.align(Alignment.Center).padding(horizontal = 52.dp, vertical = 16.dp),
                    fontSize = 18.sp, maxLines = 2, overflow = TextOverflow.Ellipsis)
            }
            HorizontalDivider(color = DeviceColors.Border)
        }
    }
}

@Composable
internal fun SettingsHome(modifier: Modifier, select: (Int) -> Unit) = DeviceScreenTheme {
    Column(modifier.fillMaxSize().verticalScroll(rememberScrollState())
        .padding(horizontal = 16.dp, vertical = 22.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        val entries = listOf(
            Triple(SETTINGS_GENERAL, R.drawable.figma_settings_general, R.string.general_description),
            Triple(0, R.drawable.figma_settings_connections, R.string.connections_description),
            Triple(1, R.drawable.figma_settings_files, R.string.files_description),
            Triple(2, R.drawable.figma_settings_privacy, R.string.privacy_description),
            Triple(3, R.drawable.figma_settings_about, R.string.about_description),
        )
        entries.forEach { (page, icon, description) ->
            Surface(color = DeviceColors.Surface, shape = RoundedCornerShape(12.dp),
                border = BorderStroke(1.dp, DeviceColors.Border)) {
                Row(Modifier.fillMaxWidth().clickable(role = Role.Button) { select(page) }
                    .heightIn(min = 72.dp).padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
                    Surface(shape = RoundedCornerShape(12.dp), color = DeviceColors.Selected) {
                        Box(Modifier.size(44.dp), contentAlignment = Alignment.Center) {
                            FigmaIcon(icon, size = 22.dp, tint = DeviceColors.Blue)
                        }
                    }
                    Column(Modifier.weight(1f).padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                        Text(settingsTitle(page), fontSize = 15.sp, lineHeight = 22.sp)
                        Text(stringResource(description), fontSize = 12.sp, lineHeight = 18.sp, color = DeviceColors.Secondary)
                    }
                    SettingsChevron()
                }
            }
        }
    }
}

@Composable
internal fun SettingsChevron() = FigmaIcon(R.drawable.figma_chevron, Modifier.rotate(-90f),
    size = 22.dp, tint = DeviceColors.Secondary)

@Composable
internal fun SettingsGroup(title: String, content: @Composable ColumnScope.() -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(title, color = DeviceColors.Secondary, fontSize = 13.sp, lineHeight = 20.sp)
        Surface(color = DeviceColors.Surface, shape = RoundedCornerShape(14.dp), border = BorderStroke(1.dp, DeviceColors.Border)) {
            Column(Modifier.fillMaxWidth(), content = content)
        }
    }
}

@Composable
internal fun SettingsDivider() = HorizontalDivider(Modifier.padding(horizontal = 16.dp),
    color = MaterialTheme.colorScheme.outlineVariant)

@Composable
internal fun SettingsValueRow(title: String, value: String, minimumHeight: androidx.compose.ui.unit.Dp = 54.dp, onClick: () -> Unit) {
    Row(Modifier.fillMaxWidth().clickable(role = Role.Button, onClick = onClick)
        .heightIn(min = minimumHeight).padding(horizontal = 16.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(title, Modifier.weight(1f), fontSize = 15.sp, lineHeight = 22.sp)
        Spacer(Modifier.width(12.dp))
        Text(value, Modifier.widthIn(max = 145.dp), fontSize = 13.sp, color = DeviceColors.Secondary,
            maxLines = 2, overflow = TextOverflow.Ellipsis)
        Spacer(Modifier.width(4.dp))
        SettingsChevron()
    }
}

@Composable
internal fun SettingsToggleRow(title: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth().toggleable(checked, role = Role.Switch, onValueChange = onChange)
        .heightIn(min = 58.dp).padding(horizontal = 16.dp, vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically) {
        Text(title, Modifier.weight(1f), fontSize = 15.sp, lineHeight = 22.sp)
        Spacer(Modifier.width(12.dp))
        SettingsSwitchVisual(checked)
    }
}

/** The row owns the switch semantics and its full-height touch target. */
@Composable
internal fun SettingsSwitchVisual(checked: Boolean, enabled: Boolean = true) {
    Surface(color = if (checked) DeviceColors.Blue else DeviceColors.SwitchOff,
        shape = RoundedCornerShape(13.dp), modifier = Modifier.size(46.dp, 26.dp).alpha(if (enabled) 1f else .45f)) {
        Box(Modifier.fillMaxSize().padding(2.dp)) {
            Surface(color = androidx.compose.ui.graphics.Color.White, shape = RoundedCornerShape(11.dp),
                modifier = Modifier.size(22.dp).align(if (checked) Alignment.CenterEnd else Alignment.CenterStart)) {}
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun SettingsSelectionSheet(title: String, choices: List<Pair<String, String>>, selected: String,
                                    dismiss: () -> Unit, select: (String) -> Unit) {
    ModalBottomSheet(onDismissRequest = dismiss, containerColor = DeviceColors.Surface, tonalElevation = 0.dp,
        shape = androidx.compose.ui.graphics.RectangleShape,
        scrimColor = androidx.compose.ui.graphics.Color(0x763D4A5E),
        dragHandle = { Surface(color = DeviceColors.Border, shape = RoundedCornerShape(3.dp),
            modifier = Modifier.padding(top = 12.dp, bottom = 8.dp).size(56.dp, 5.dp)) {} },
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)) {
        Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).selectableGroup().padding(horizontal = 16.dp)) {
            Text(title, Modifier.padding(horizontal = 8.dp, vertical = 8.dp), fontSize = 17.sp)
            choices.forEachIndexed { index, (value, label) ->
                Surface(color = if (selected == value) DeviceColors.Selected else DeviceColors.Surface,
                    shape = RoundedCornerShape(10.dp)) {
                    Row(Modifier.fillMaxWidth().selectable(selected == value, role = Role.RadioButton,
                        onClick = { select(value); dismiss() }).heightIn(min = 48.dp).padding(horizontal = 8.dp, vertical = 10.dp),
                        verticalAlignment = Alignment.CenterVertically) {
                        Text(label, Modifier.weight(1f), fontSize = 15.sp,
                            color = if (selected == value) DeviceColors.Blue else DeviceColors.Ink)
                        if (selected == value) Text("✓", Modifier.width(36.dp), fontSize = 18.sp,
                            textAlign = androidx.compose.ui.text.style.TextAlign.Center, color = DeviceColors.Blue)
                    }
                }
                if (index != choices.lastIndex) SettingsDivider()
            }
            Spacer(Modifier.height(16.dp))
        }
    }
}
