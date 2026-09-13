package com.bluelink.android.ui.settings

import android.text.format.Formatter
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bluelink.android.R
import com.bluelink.android.data.local.AppSettings
import com.bluelink.android.files.StorageUsage
import com.bluelink.android.files.readStorageUsage
import com.bluelink.android.ui.devices.*
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

@Composable
internal fun FileStorageSettings(modifier: Modifier, settings: AppSettings, save: (AppSettings) -> Unit,
                                 chooseDirectory: () -> Unit, manageFiles: () -> Unit) = DeviceScreenTheme {
    val context = LocalContext.current
    var publishing by remember { mutableStateOf(false) }
    var clearing by remember { mutableStateOf(false) }
    var cacheBusy by remember { mutableStateOf(false) }
    var cacheResult by remember { mutableIntStateOf(0) }
    val scope = rememberCoroutineScope()
    var duplicate by remember { mutableStateOf(false) }
    var receiveLimit by remember { mutableStateOf(false) }
    var usage by remember { mutableStateOf<StorageUsage?>(null) }
    var failed by remember { mutableStateOf(false) }
    LaunchedEffect(settings.downloadDirectory) {
        failed = false
        try { usage = readStorageUsage(context) }
        catch (canceled: CancellationException) { throw canceled }
        catch (_: Exception) { failed = true }
    }
    val choices = listOf("rename" to stringResource(R.string.storage_rename), "ask" to stringResource(R.string.storage_ask_me),
        "overwrite" to stringResource(R.string.storage_overwrite))
    val publishChoices = listOf("all" to stringResource(R.string.publish_all), "images" to stringResource(R.string.publish_images),
        "other" to stringResource(R.string.publish_other), "manual" to stringResource(R.string.publish_manual))
    val publishValue = if (settings.autoSaveImages) { if (settings.autoSaveOtherAttachments) "all" else "images" }
        else if (settings.autoSaveOtherAttachments) "other" else "manual"
    Column(modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp, 24.dp), verticalArrangement = Arrangement.spacedBy(24.dp)) {
        SettingsGroup(stringResource(R.string.storage_receive)) {
            SettingsValueRow(stringResource(R.string.storage_directory), if (settings.downloadDirectory.startsWith("content://"))
                stringResource(R.string.storage_custom) else "Download/BlueLink", onClick = chooseDirectory)
            SettingsDivider()
            SettingsToggleRow(stringResource(R.string.storage_ask), !settings.autoDownloadFiles) { save(settings.copy(autoDownloadFiles = !it)) }
            SettingsDivider()
            SettingsValueRow(stringResource(R.string.storage_duplicate), choices.first { it.first == settings.duplicateFilePolicy }.second) { duplicate = true }
            SettingsDivider()
            SettingsToggleRow(stringResource(R.string.receive_limit_enabled), settings.receiveSizeLimitEnabled) { save(settings.copy(receiveSizeLimitEnabled = it)) }
            SettingsDivider()
            SettingsValueRow(stringResource(R.string.storage_receive_limit), if (settings.receiveSizeLimitEnabled)
                "${com.bluelink.android.domain.ReceiveLimitPolicy.initialMiB(settings.receiveSizeLimitBytes)} MiB"
                else stringResource(R.string.storage_limit_not_enabled)) { receiveLimit = true }
        }
        SettingsGroup(stringResource(R.string.file_display_storage)) {
            SettingsToggleRow(stringResource(R.string.show_image_thumbnails), settings.showImageThumbnails) { save(settings.copy(showImageThumbnails = it)) }
            SettingsDivider()
            Row(Modifier.fillMaxWidth().padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(stringResource(R.string.storage_used), Modifier.weight(1f), fontSize = 15.sp)
                Text(usage?.let { Formatter.formatShortFileSize(context, it.totalBytes) } ?: stringResource(
                    if (failed) R.string.storage_unavailable else R.string.storage_calculating), color = DeviceColors.Secondary, fontSize = 13.sp)
            }
            usage?.let { value ->
                Column(Modifier.padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    LinearProgressIndicator(progress = { value.imageFraction }, modifier = Modifier.fillMaxWidth().height(8.dp).clip(RoundedCornerShape(4.dp)),
                        color = DeviceColors.Blue, trackColor = MaterialTheme.colorScheme.outlineVariant)
                    Text(stringResource(R.string.storage_breakdown, Formatter.formatShortFileSize(context, value.imageBytes),
                        Formatter.formatShortFileSize(context, value.otherBytes)), fontSize = 12.sp, color = DeviceColors.Secondary)
                }
            }
            SettingsValueRow(stringResource(R.string.storage_manage), "", onClick = manageFiles)
            SettingsDivider()
            SettingsValueRow(stringResource(R.string.clear_thumbnail_cache), if (cacheBusy) stringResource(R.string.cache_clearing) else "") { if (!cacheBusy) clearing = true }
            if (cacheResult != 0) Text(stringResource(cacheResult), Modifier.padding(horizontal = 16.dp, vertical = 8.dp), fontSize = 12.sp, color = DeviceColors.Secondary)
        }
        SettingsGroup(stringResource(R.string.mobile_storage_background)) {
            SettingsValueRow(stringResource(R.string.publish_directory), publishChoices.first { it.first == publishValue }.second) { publishing = true }
            Text(stringResource(R.string.publish_note), Modifier.padding(horizontal = 16.dp, vertical = 8.dp), fontSize = 12.sp, color = DeviceColors.Secondary)
            SettingsDivider()
            SettingsToggleRow(stringResource(R.string.storage_charging), settings.largeFilesOnlyWhileCharging) { save(settings.copy(largeFilesOnlyWhileCharging = it)) }
            if (settings.largeFilesOnlyWhileCharging) Text(stringResource(R.string.storage_charging_note),
                Modifier.padding(start = 16.dp, end = 16.dp, bottom = 12.dp), fontSize = 12.sp, color = DeviceColors.Secondary)
            SettingsDivider()
            SettingsToggleRow(stringResource(R.string.storage_background), settings.keepBackgroundSessions) { save(settings.copy(keepBackgroundSessions = it)) }
        }
    }
    if (clearing) com.bluelink.android.ui.components.BlueLinkConfirmation(stringResource(R.string.clear_thumbnail_cache),
        stringResource(R.string.clear_thumbnail_question), stringResource(R.string.clear_thumbnail_note), stringResource(R.string.privacy_clear),
        { clearing = false }, {
            clearing = false; cacheBusy = true; cacheResult = 0
            scope.launch {
                try { com.bluelink.android.files.ThumbnailCache.clear(context); cacheResult = R.string.cache_cleared }
                catch (canceled: CancellationException) { throw canceled }
                catch (_: Exception) { cacheResult = R.string.cache_clear_failed }
                finally { cacheBusy = false }
            }
        })
    if (publishing) SettingsSelectionSheet(stringResource(R.string.publish_directory), publishChoices, publishValue,
        dismiss = { publishing = false }, select = { save(settings.copy(autoSaveImages = it == "all" || it == "images", autoSaveOtherAttachments = it == "all" || it == "other")) })
    if (receiveLimit) ReceiveLimitEditor(settings.receiveSizeLimitBytes, settings.receiveSizeLimitEnabled,
        dismiss = { receiveLimit = false }, save = {
            save(settings.copy(receiveSizeLimitBytes = it)); receiveLimit = false
        })
    if (duplicate) SettingsSelectionSheet(stringResource(R.string.storage_duplicate_title), choices, settings.duplicateFilePolicy,
        dismiss = { duplicate = false }, select = { save(settings.copy(duplicateFilePolicy = it)) })
}
