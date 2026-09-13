package com.bluelink.android.ui.files

import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import com.bluelink.android.R
import com.bluelink.android.files.DuplicateChoice
import com.bluelink.android.ui.devices.DeviceScreenTheme
import com.bluelink.android.ui.settings.SettingsSelectionSheet

@Composable
internal fun FileConflictSheet(fileName: String, decide: (DuplicateChoice) -> Unit) = DeviceScreenTheme {
    SettingsSelectionSheet(stringResource(R.string.storage_conflict_title) + " · " + fileName,
        listOf("rename" to stringResource(R.string.storage_rename), "overwrite" to stringResource(R.string.storage_overwrite)), "",
        dismiss = { decide(DuplicateChoice.CANCEL) },
        select = { decide(if (it == "overwrite") DuplicateChoice.REPLACE else DuplicateChoice.RENAME) })
}
