package com.bluelink.android.ui.settings

import android.content.Intent
import android.os.Build
import android.widget.Toast
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.text.font.FontWeight
import androidx.core.content.FileProvider
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.bluelink.android.BuildConfig
import com.bluelink.android.R
import com.bluelink.android.domain.DiagnosticEntry
import com.bluelink.android.feedback.*
import com.bluelink.android.ui.devices.*
import java.io.File

@Composable
internal fun HelpFeedbackScreen(modifier: Modifier, diagnostics: List<DiagnosticEntry>, selectPage: (Int) -> Unit,
                                model: FeedbackViewModel = viewModel(), packageDirectory: File? = null) = DeviceScreenTheme {
    val state by model.state.collectAsStateWithLifecycle()
    val context = LocalContext.current
    var typesOpen by remember { mutableStateOf(false) }
    val typeLabels = listOf(R.string.support_connection_type, R.string.support_message_type,
        R.string.support_file_type, R.string.support_other_type).map { stringResource(it) }
    val descriptionLabel = stringResource(R.string.support_description)
    val shareTitle = stringResource(R.string.support_share)
    val shareError = stringResource(R.string.support_share_error)
    Column(modifier.fillMaxSize().imePadding().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(20.dp)) {
        SettingsGroup(stringResource(R.string.support_help)) {
            listOf(Triple(SETTINGS_HELP_CONNECTION, R.string.support_connection, R.string.support_connection_summary),
                Triple(SETTINGS_HELP_MESSAGES, R.string.support_messages, R.string.support_messages_summary),
                Triple(SETTINGS_HELP_FAQ, R.string.support_faq, R.string.support_faq_summary)).forEachIndexed { index, (page, title, detail) ->
                Row(Modifier.fillMaxWidth().clickable(role = Role.Button) { selectPage(page) }.heightIn(min = 52.dp)
                    .padding(horizontal = 16.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(stringResource(title), Modifier.weight(.32f), fontSize = 14.sp)
                    Text(stringResource(detail), Modifier.weight(.68f), fontSize = 11.sp, lineHeight = 18.sp, color = DeviceColors.Secondary)
                    FigmaIcon(R.drawable.figma_content_chevron, size = 14.dp, tint = DeviceColors.Secondary)
                }
                if (index < 2) SettingsDivider()
            }
        }
        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(stringResource(R.string.support_type), fontSize = 14.sp)
            Surface(shape = RoundedCornerShape(12.dp), color = DeviceColors.Surface, border = BorderStroke(1.dp, DeviceColors.Border)) {
                Row(Modifier.fillMaxWidth().clickable(enabled = !state.generating, role = Role.Button) { typesOpen = true }
                    .heightIn(min = 48.dp).padding(horizontal = 14.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text(typeLabels[state.type.ordinal], Modifier.weight(1f), fontSize = 14.sp)
                    FigmaIcon(R.drawable.figma_chevron, size = 20.dp, tint = DeviceColors.Secondary)
                }
            }
        }
        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(if (state.invalid) stringResource(R.string.support_description_required) else descriptionLabel,
                color = if (state.invalid) MaterialTheme.colorScheme.error else DeviceColors.Ink, fontSize = 14.sp)
            Surface(shape = RoundedCornerShape(12.dp), color = DeviceColors.Surface,
                border = BorderStroke(1.dp, if (state.invalid) MaterialTheme.colorScheme.error else DeviceColors.Border)) {
                Column(Modifier.fillMaxWidth().heightIn(min = 124.dp).padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    BasicTextField(state.description, { model.edit(description = it) }, enabled = !state.generating,
                        textStyle = MaterialTheme.typography.bodyMedium.copy(color = DeviceColors.Ink),
                        cursorBrush = SolidColor(DeviceColors.Blue), modifier = Modifier.fillMaxWidth().heightIn(min = 62.dp)
                            .semantics { contentDescription = descriptionLabel },
                        decorationBox = { input -> Box { if (state.description.isEmpty()) Text(stringResource(R.string.support_description_hint),
                            fontSize = 13.sp, color = DeviceColors.Secondary); input() } })
                    if (state.invalid) Text(stringResource(R.string.support_required_error), color = MaterialTheme.colorScheme.error, fontSize = 12.sp)
                    Text("${state.description.length} / 1000", Modifier.align(Alignment.End), color = DeviceColors.Secondary, fontSize = 11.sp)
                }
            }
        }
        Surface(shape = RoundedCornerShape(12.dp), color = DeviceColors.Surface, border = BorderStroke(1.dp, DeviceColors.Border)) {
            Row(Modifier.fillMaxWidth().toggleable(state.includeDiagnostics, enabled = !state.generating, role = Role.Switch,
                onValueChange = { model.edit(includeDiagnostics = it) }).padding(horizontal = 16.dp, vertical = 10.dp),
                verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    Text(stringResource(R.string.support_diagnostics), fontSize = 14.sp)
                    Text(stringResource(R.string.support_diagnostics_description), fontSize = 12.sp, color = DeviceColors.Secondary)
                }
                Spacer(Modifier.width(12.dp))
                SettingsSwitchVisual(state.includeDiagnostics, enabled = !state.generating)
            }
        }
        if (state.packageFile != null || state.failed) {
            val failed = state.failed
            Surface(shape = RoundedCornerShape(12.dp), color = if (failed) MaterialTheme.colorScheme.error.copy(alpha = .1f) else DeviceColors.Success.copy(alpha = .1f)) {
                Column(Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        FigmaIcon(if (failed) R.drawable.figma_feedback_failure else R.drawable.figma_feedback_success, size = 22.dp)
                        Text(stringResource(if (failed) R.string.support_failed else R.string.support_ready), fontSize = 14.sp,
                            color = if (failed) MaterialTheme.colorScheme.error else DeviceColors.Success)
                    }
                    Text(if (failed) stringResource(R.string.support_failed_description) else state.packageFile!!.name, fontSize = 12.sp)
                    Text(stringResource(if (failed) R.string.support_failed_retained else if (state.includeDiagnostics)
                        R.string.support_ready_diagnostics else R.string.support_ready_without_diagnostics), color = DeviceColors.Secondary, fontSize = 12.sp)
                }
            }
        }
        Button(onClick = {
            val file = state.packageFile
            if (file == null) model.generate(packageDirectory ?: File(context.cacheDir, "shared/feedback"), "${BuildConfig.VERSION_NAME} (${BuildConfig.VERSION_CODE})", Build.VERSION.SDK_INT) { diagnostics }
            else runCatching {
                val uri = FileProvider.getUriForFile(context, "${context.packageName}.files", file)
                context.startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).apply {
                    type = "application/zip"; putExtra(Intent.EXTRA_STREAM, uri)
                    addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                }, shareTitle))
            }.onFailure { Toast.makeText(context, shareError, Toast.LENGTH_SHORT).show() }
        }, enabled = !state.generating, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp), shape = RoundedCornerShape(10.dp)) {
            if (state.generating) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary)
            Text(stringResource(when { state.generating -> R.string.support_generating; state.packageFile != null -> R.string.support_share
                state.failed -> R.string.support_retry; else -> R.string.support_generate }), Modifier.padding(horizontal = 8.dp), fontWeight = FontWeight.Normal)
        }
        SupportNotice(stringResource(R.string.support_no_upload), stringResource(R.string.support_no_upload_description))
        Text(stringResource(R.string.support_footer), Modifier.align(Alignment.CenterHorizontally).padding(vertical = 12.dp),
            fontSize = 11.sp, color = DeviceColors.Secondary)
    }
    if (typesOpen) SettingsSelectionSheet(stringResource(R.string.support_type), FeedbackType.entries.map { it.name to typeLabels[it.ordinal] },
        state.type.name, dismiss = { typesOpen = false }, select = { model.edit(type = FeedbackType.valueOf(it)) })
}

@Composable
internal fun SupportNotice(title: String, text: String) {
    Surface(shape = RoundedCornerShape(12.dp), color = DeviceColors.Selected) {
        Column(Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            if (title.isNotEmpty()) Text(title, color = DeviceColors.Blue, fontSize = 14.sp)
            Text(text, color = DeviceColors.Secondary, fontSize = 12.sp, lineHeight = 19.sp)
        }
    }
}
