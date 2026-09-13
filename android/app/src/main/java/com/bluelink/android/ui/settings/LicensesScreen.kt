package com.bluelink.android.ui.settings

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bluelink.android.R
import com.bluelink.android.ui.devices.*
import org.json.JSONArray

private data class LicensedComponent(val name: String, val version: String, val licenses: List<String>)

@Composable
internal fun LicensesScreen(modifier: Modifier) = DeviceScreenTheme {
    val context = LocalContext.current
    val entries = remember {
        val array = JSONArray(context.assets.open("licenses/dependencies.json").bufferedReader().use { it.readText() })
        (0 until array.length()).map { i -> array.getJSONObject(i).let { item ->
            val licenses = item.getJSONArray("licenses")
            LicensedComponent(item.getString("name"), item.getString("version"), (0 until licenses.length()).map { licenses.getString(it) })
        } } + listOf(LicensedComponent("Noto Sans SC", "", listOf("SIL Open Font License 1.1")),
            LicensedComponent("Phosphor Icons / BlueLink file icons", "", listOf("MIT License")))
    }
    var query by rememberSaveable { mutableStateOf("") }
    var selected by remember { mutableStateOf<LicensedComponent?>(null) }
    BackHandler(selected != null) { selected = null }
    Column(modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(20.dp)) {
        SupportNotice(stringResource(R.string.support_licenses_intro) + " · " + stringResource(R.string.support_licenses_count, entries.size),
            stringResource(R.string.support_licenses_description))
        OutlinedTextField(query, { query = it }, Modifier.fillMaxWidth(), singleLine = true, shape = RoundedCornerShape(12.dp),
            placeholder = { Text(stringResource(R.string.support_licenses_search), fontSize = 13.sp) })
        SettingsGroup(stringResource(R.string.support_licenses_components)) {
            val visible = entries.filter { (it.name + " " + it.licenses.joinToString()).contains(query, true) }
            if (visible.isEmpty()) Text(stringResource(R.string.support_licenses_empty), Modifier.padding(16.dp), color = DeviceColors.Secondary)
            visible.forEachIndexed { index, entry ->
                Row(Modifier.fillMaxWidth().clickable { selected = entry }.heightIn(min = 80.dp).padding(16.dp),
                    verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                    Surface(shape = RoundedCornerShape(10.dp), color = DeviceColors.Canvas) {
                        Box(Modifier.size(40.dp), contentAlignment = Alignment.Center) {
                            Text(entry.name.substringAfter(':').take(1).uppercase(), color = DeviceColors.Blue, fontSize = 15.sp)
                        }
                    }
                    Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                        Text(entry.name.substringAfter(':'), fontSize = 15.sp)
                        Text(entry.version + " · " + entry.licenses.joinToString(), color = DeviceColors.Secondary, fontSize = 11.sp, lineHeight = 17.sp)
                    }
                    FigmaIcon(R.drawable.figma_content_chevron, size = 14.dp, tint = DeviceColors.Secondary)
                }
                if (index != visible.lastIndex) SettingsDivider()
            }
        }
        Text(stringResource(R.string.support_licenses_footer), fontSize = 12.sp, color = DeviceColors.Secondary)
    }
    selected?.let { entry ->
        com.bluelink.android.ui.components.BlueLinkPrompt(entry.name.substringAfter(':'), { selected = null }) {
            Column(Modifier.heightIn(max = 500.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text("${entry.name}\n${entry.version}", fontSize = 13.sp)
                entry.licenses.forEach { license ->
                    Text(license, style = MaterialTheme.typography.titleSmall)
                    val asset = when {
                        entry.name == "Phosphor Icons / BlueLink file icons" -> "PhosphorIcons.txt"
                        license.contains("Apache", true) -> "Apache-2.0.txt"
                        license.contains("SIL", true) -> "NotoSansSC-OFL.txt"
                        else -> null
                    }
                    if (asset != null) Text(remember(asset) { context.assets.open("licenses/$asset").bufferedReader().use { it.readText() } }, fontSize = 12.sp)
                }
            }
        }
    }
}
