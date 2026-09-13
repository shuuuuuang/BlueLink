package com.bluelink.android.ui.settings

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.bluelink.android.R
import com.bluelink.android.data.local.AppSettings
import com.bluelink.android.ui.devices.DeviceScreenTheme

@Composable
internal fun GeneralSettings(modifier: Modifier, settings: AppSettings, save: (AppSettings) -> Unit) = DeviceScreenTheme {
    var restore by remember { mutableStateOf(false) }
    var selector by rememberSaveable { mutableStateOf<String?>(null) }
    val themes = listOf("system" to stringResource(R.string.system_theme),
        "light" to stringResource(R.string.light_theme), "dark" to stringResource(R.string.dark_theme))
    val languages = listOf("zh-CN" to "简体中文", "zh-TW" to "繁體中文", "en" to "English")
    Column(modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp, 24.dp),
        verticalArrangement = Arrangement.spacedBy(24.dp)) {
        SettingsGroup(stringResource(R.string.appearance)) {
            SettingsValueRow(stringResource(R.string.theme), themes.first { it.first == settings.theme }.second, 48.dp) { selector = "theme" }
            SettingsDivider()
            SettingsValueRow(stringResource(R.string.language), languages.firstOrNull { it.first == settings.language }?.second ?: stringResource(R.string.system_theme), 48.dp) { selector = "language" }
        }
        SettingsGroup(stringResource(R.string.notifications)) {
            SettingsToggleRow(stringResource(R.string.message_notifications), settings.messageNotifications) { save(settings.copy(messageNotifications = it)) }
            SettingsDivider()
            SettingsToggleRow(stringResource(R.string.connection_notifications), settings.connectionNotifications) {
                save(settings.copy(connectionNotifications = it))
            }
            SettingsDivider()
            SettingsToggleRow(stringResource(R.string.transfer_notifications), settings.transferNotifications) {
                save(settings.copy(transferNotifications = it))
            }
        }
        SettingsGroup(stringResource(R.string.settings_preferences)) {
            SettingsValueRow(stringResource(R.string.restore_settings), "") { restore = true }
        }
    }
    if (restore) com.bluelink.android.ui.components.BlueLinkConfirmation(
        stringResource(R.string.restore_settings), stringResource(R.string.restore_settings_question),
        stringResource(R.string.restore_settings_note), stringResource(R.string.restore_settings),
        { restore = false }, { save(AppSettings()); restore = false })
    selector?.let { type -> SettingsSelectionSheet(
        stringResource(if (type == "theme") R.string.choose_theme else R.string.choose_language),
        if (type == "theme") themes else languages, if (type == "theme") settings.theme else settings.language,
        dismiss = { selector = null }, select = {
            save(if (type == "theme") settings.copy(theme = it) else settings.copy(language = it))
        }) }
}
