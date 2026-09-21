package com.bluelink.android.ui.settings

import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import com.bluelink.android.R
import com.bluelink.android.ui.components.BlueLinkPrompt
import com.bluelink.android.updates.*
import java.util.Locale

@Composable
internal fun GitHubUpdateRow(backend: UpdateBackend? = null) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val operations = backend ?: remember(context) { AndroidUpdateBackend(context) }
    val workflow = remember(operations, scope) { UpdateWorkflow(operations, scope) }
    DisposableEffect(workflow) { onDispose { workflow.close() } }
    val state by workflow.state.collectAsState()
    SettingsValueRow(stringResource(R.string.update_check), "", onClick = workflow::check)
    if (state.visible) BlueLinkPrompt(stringResource(R.string.update_check), dismiss = workflow::close, footer = {
        TextButton(onClick = workflow::close) { Text(stringResource(if (state.busy) R.string.update_cancel else R.string.close)) }
        val action: (() -> Unit)? = when (state.phase) {
            UpdatePhase.AVAILABLE, UpdatePhase.DOWNLOAD_FAILED -> workflow::download
            UpdatePhase.READY, UpdatePhase.INSTALL_FAILED -> workflow::install
            UpdatePhase.PERMISSION -> workflow::requestPermission
            UpdatePhase.CHECK_FAILED -> workflow::check
            else -> null
        }
        if (action != null) TextButton(onClick = action) { Text(stringResource(when (state.phase) {
            UpdatePhase.READY, UpdatePhase.INSTALL_FAILED -> R.string.update_install
            UpdatePhase.PERMISSION -> R.string.update_allow_install
            UpdatePhase.CHECK_FAILED, UpdatePhase.DOWNLOAD_FAILED -> R.string.update_retry
            else -> R.string.update_download
        })) }
    }) {
        val failureText = when (state.failure) {
            UpdateFailure.INTEGRITY -> R.string.update_integrity_failed
            UpdateFailure.SIGNATURE -> R.string.update_signature_failed
            UpdateFailure.PACKAGE -> R.string.update_package_failed
            UpdateFailure.VERSION -> R.string.update_version_failed
            UpdateFailure.INSTALL -> R.string.update_install_failed
            else -> R.string.update_failed
        }
        Text(stringResource(if (state.failure != null) failureText else when (state.phase) {
            UpdatePhase.CHECKING -> R.string.update_checking
            UpdatePhase.CURRENT -> R.string.update_current
            UpdatePhase.DOWNLOADING -> R.string.update_downloading
            UpdatePhase.READY -> R.string.update_ready
            UpdatePhase.VERIFYING -> R.string.update_verifying
            UpdatePhase.PERMISSION -> R.string.update_permission_notice
            UpdatePhase.HANDED_OFF -> R.string.update_handed_off
            else -> R.string.update_available
        }))
        state.release?.let { release ->
            Text(release.tag.removePrefix("v"))
            if (state.phase == UpdatePhase.DOWNLOADING) {
                val fraction = if (state.total > 0) (state.received.toFloat() / state.total).coerceIn(0f, 1f) else 0f
                LinearProgressIndicator(progress = fraction, modifier = Modifier.fillMaxWidth())
                Text(String.format(Locale.ROOT, "%d%% · %.1f / %.1f MiB", (fraction * 100).toInt(), state.received / 1048576.0, state.total / 1048576.0))
            }
            if (state.phase in setOf(UpdatePhase.AVAILABLE, UpdatePhase.CHECK_FAILED)) {
                Text(stringResource(R.string.update_in_app_notice))
                if (release.notes.isNotBlank()) Text(release.notes)
            }
        }
    }
}
