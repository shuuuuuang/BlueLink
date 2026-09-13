package com.bluelink.android.ui.settings

import android.content.Intent
import android.os.Build
import android.widget.Toast
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.FileProvider
import com.bluelink.android.BuildConfig
import com.bluelink.android.R
import com.bluelink.android.data.local.AppSettings
import com.bluelink.android.domain.DiagnosticEntry
import com.bluelink.android.domain.PrivacyAction
import com.bluelink.android.feedback.DiagnosticSummary
import com.bluelink.android.ui.components.BlueLinkConfirmation
import com.bluelink.android.ui.components.BlueLinkPrompt
import com.bluelink.android.ui.devices.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

@Composable
internal fun PrivacySettings(modifier: Modifier, settings: AppSettings, save: (AppSettings) -> Unit,
                             fingerprint: String, diagnostics: List<DiagnosticEntry>, perform: suspend (PrivacyAction) -> Unit) = DeviceScreenTheme {
    var identityDetails by remember { mutableStateOf(false) }
    var retention by remember { mutableStateOf(false) }
    var confirmation by remember { mutableStateOf<PrivacyAction?>(null) }
    var busy by remember { mutableStateOf(false) }
    var failed by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    val successLabel = stringResource(R.string.privacy_done)
    val exportTitle = stringResource(R.string.privacy_export)
    val choices = listOf("7d" to pluralStringResource(R.plurals.privacy_days, 7, 7), "30d" to pluralStringResource(R.plurals.privacy_days, 30, 30),
        "90d" to pluralStringResource(R.plurals.privacy_days, 90, 90), "1y" to stringResource(R.string.privacy_year),
        "forever" to stringResource(R.string.privacy_forever))
    Column(modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp, 24.dp),
        verticalArrangement = Arrangement.spacedBy(24.dp)) {
        SettingsGroup(stringResource(R.string.privacy_records)) {
            SettingsToggleRow(stringResource(R.string.privacy_save_messages), settings.saveChatHistory) { save(settings.copy(saveChatHistory = it)) }
            SettingsDivider()
            SettingsToggleRow(stringResource(R.string.save_transfer_history), settings.saveTransferHistory) { save(settings.copy(saveTransferHistory = it)) }
            SettingsDivider()
            SettingsValueRow(stringResource(R.string.privacy_retention), choices.firstOrNull { it.first == settings.retentionPeriod }?.second ?: choices.last().second) { retention = true }
            SettingsDivider()
            PrivacyActionRow(stringResource(R.string.privacy_clear_messages), true) { confirmation = PrivacyAction.CLEAR_MESSAGES }
            SettingsDivider()
            PrivacyActionRow(stringResource(R.string.privacy_clear_transfers), true) { confirmation = PrivacyAction.CLEAR_TRANSFERS }
        }
        SettingsGroup(stringResource(R.string.privacy_identity)) {
            SettingsValueRow(stringResource(R.string.identity_details), "") { identityDetails = true }
        }
        SettingsGroup(stringResource(R.string.privacy_diagnostics)) {
            SettingsToggleRow(stringResource(R.string.diagnostics_enabled), settings.diagnosticsEnabled) { save(settings.copy(diagnosticsEnabled = it)) }
            SettingsDivider()
            PrivacyActionRow(exportTitle) {
                if (!busy) scope.launch {
                    busy = true
                    try {
                        val file = withContext(Dispatchers.IO) { DiagnosticSummary.create(File(context.cacheDir, "shared/diagnostics"),
                            BuildConfig.VERSION_NAME, Build.VERSION.SDK_INT, diagnostics) }
                        val uri = FileProvider.getUriForFile(context, "${context.packageName}.files", file)
                        context.startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).apply {
                            type = "text/plain"; putExtra(Intent.EXTRA_STREAM, uri)
                            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                        }, exportTitle))
                    } catch (canceled: CancellationException) { throw canceled }
                    catch (_: Exception) { failed = true }
                    finally { busy = false }
                }
            }
        }
        Surface(shape = RoundedCornerShape(12.dp), color = DeviceColors.Selected) {
            Text(stringResource(R.string.privacy_note), Modifier.fillMaxWidth().padding(16.dp),
                color = DeviceColors.Blue, fontSize = 13.sp, lineHeight = 20.sp)
        }
    }
    if (identityDetails) BlueLinkPrompt(stringResource(R.string.identity_details), { identityDetails = false }, footer = {
        TextButton(onClick = { identityDetails = false; confirmation = PrivacyAction.RESET_IDENTITY }) { Text(stringResource(R.string.privacy_reset_identity)) }
        TextButton(onClick = { identityDetails = false }) { Text(stringResource(R.string.close)) }
    }) {
        Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text(stringResource(R.string.identity_details_note), fontSize = 14.sp)
            androidx.compose.foundation.text.selection.SelectionContainer { Text(fingerprint.uppercase().filter { it in "0123456789ABCDEF" }.chunked(4).joinToString(":"), fontSize = 13.sp) }
        }
    }
    if (retention) SettingsSelectionSheet(stringResource(R.string.privacy_retention), choices, settings.retentionPeriod,
        { retention = false }, { save(settings.copy(retentionPeriod = it)) })
    confirmation?.let { action -> PrivacyActionConfirmation(action, { confirmation = null }) {
        confirmation = null
        if (!busy) scope.launch {
            busy = true
            try { perform(action); Toast.makeText(context, successLabel, Toast.LENGTH_SHORT).show() }
            catch (canceled: CancellationException) { throw canceled }
            catch (_: Exception) { failed = true }
            finally { busy = false }
        }
    } }
    if (busy) BlueLinkPrompt(stringResource(R.string.privacy_working), {}) { CircularProgressIndicator(Modifier.size(28.dp)) }
    if (failed) BlueLinkPrompt(stringResource(R.string.privacy_failed), { failed = false }) {
        Text(stringResource(R.string.privacy_failed_note), fontSize = 14.sp)
    }
}

@Composable
private fun PrivacyActionRow(title: String, destructive: Boolean = false, action: () -> Unit) {
    Row(Modifier.fillMaxWidth().clickable(role = Role.Button, onClick = action).heightIn(min = 54.dp)
        .padding(horizontal = 16.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(title, Modifier.weight(1f), color = if (destructive) DeviceColors.Error else DeviceColors.Ink, fontSize = 15.sp, lineHeight = 22.sp)
        Spacer(Modifier.width(12.dp)); SettingsChevron()
    }
}

@Composable
internal fun PrivacyActionConfirmation(action: PrivacyAction, dismiss: () -> Unit, confirm: () -> Unit) {
    val resources = when (action) {
        PrivacyAction.CLEAR_MESSAGES -> listOf(R.string.privacy_clear_messages_title, R.string.privacy_clear_messages_question, R.string.privacy_clear_messages_note, R.string.privacy_clear)
        PrivacyAction.CLEAR_TRANSFERS -> listOf(R.string.privacy_clear_transfers_title, R.string.privacy_clear_transfers_question, R.string.privacy_clear_transfers_note, R.string.privacy_clear)
        PrivacyAction.REMOVE_ALL_TRUST -> listOf(R.string.privacy_remove_title, R.string.privacy_remove_question, R.string.privacy_remove_note, R.string.privacy_remove_confirm)
        PrivacyAction.RESET_IDENTITY -> listOf(R.string.privacy_reset_title, R.string.privacy_reset_question, R.string.privacy_reset_note, R.string.privacy_reset_confirm)
    }.map { stringResource(it) }
    BlueLinkConfirmation(resources[0], resources[1], resources[2], resources[3], dismiss, confirm)
}
