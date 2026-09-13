package com.bluelink.android.ui.settings

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.runtime.key
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringArrayResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.bluelink.android.BuildConfig
import com.bluelink.android.R
import com.bluelink.android.ui.components.BlueLinkLogo
import com.bluelink.android.ui.devices.*

@Composable
internal fun AboutSettings(modifier: Modifier, select: (Int) -> Unit, localUpdate: () -> Unit) = DeviceScreenTheme {
    Column(modifier.fillMaxSize()) {
        Column(Modifier.weight(1f).fillMaxWidth().verticalScroll(rememberScrollState()).padding(16.dp, 28.dp),
            verticalArrangement = Arrangement.spacedBy(24.dp)) {
            Surface(shape = RoundedCornerShape(16.dp), color = DeviceColors.Surface, border = BorderStroke(1.dp, DeviceColors.Border)) {
                Column(Modifier.fillMaxWidth().padding(20.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                    BlueLinkLogo(Modifier.size(70.dp))
                    Spacer(Modifier.height(10.dp))
                    Text("蓝联 BlueLink", fontSize = 20.sp, lineHeight = 28.sp)
                    Spacer(Modifier.height(4.dp))
                    Text(stringResource(R.string.support_version, BuildConfig.VERSION_NAME, BuildConfig.VERSION_CODE),
                        fontSize = 12.sp, lineHeight = 18.sp, color = DeviceColors.Secondary)
                }
            }
            SettingsGroup(stringResource(R.string.support_update_support)) {
                SettingsValueRow(stringResource(R.string.support_local_update), "", onClick = localUpdate)
                SettingsDivider()
                SettingsValueRow(stringResource(R.string.support_help_feedback), "") { select(SETTINGS_HELP) }
                SettingsDivider()
                SettingsValueRow(stringResource(R.string.support_licenses), "") { select(SETTINGS_LICENSES) }
            }
            SettingsGroup(stringResource(R.string.support_legal)) {
                SettingsValueRow(stringResource(R.string.support_privacy), "") { select(SETTINGS_PRIVACY_POLICY) }
                SettingsDivider()
                SettingsValueRow(stringResource(R.string.support_agreement), "") { select(SETTINGS_AGREEMENT) }
            }
        }
        Text("© 2026 BlueLink. Bluetooth only.", Modifier.align(Alignment.CenterHorizontally).padding(16.dp, 12.dp),
            fontSize = 12.sp, lineHeight = 17.sp, color = DeviceColors.Secondary)
    }
}

@Composable
internal fun SupportArticle(modifier: Modifier, page: Int, feedback: () -> Unit) = DeviceScreenTheme {
    val article = stringArrayResource(when (page) {
        SETTINGS_HELP_CONNECTION -> R.array.support_article_connection
        SETTINGS_HELP_MESSAGES -> R.array.support_article_messages
        SETTINGS_HELP_FAQ -> R.array.support_article_faq
        SETTINGS_PRIVACY_POLICY -> R.array.support_article_privacy
        else -> R.array.support_article_agreement
    })
    key(page) {
        if (page == SETTINGS_PRIVACY_POLICY || page == SETTINGS_AGREEMENT) LegalArticle(modifier, article)
        else HelpArticle(modifier, article, feedback)
    }
}

/** Both original article families keep their reading frame fixed while the content scrolls. */
@Composable
private fun LegalArticle(modifier: Modifier, article: Array<String>) {
    Surface(modifier.fillMaxSize().padding(16.dp), shape = RoundedCornerShape(16.dp),
        color = DeviceColors.Surface, border = BorderStroke(1.dp, DeviceColors.Border)) {
        Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 20.dp, vertical = 16.dp)) {
            ArticleBody(article)
        }
    }
}

@Composable
private fun HelpArticle(modifier: Modifier, article: Array<String>, feedback: () -> Unit) {
    Surface(modifier.fillMaxSize().padding(16.dp), shape = RoundedCornerShape(16.dp),
        color = DeviceColors.Surface, border = BorderStroke(1.dp, DeviceColors.Border)) {
        BoxWithConstraints {
            val viewportHeight = maxHeight
            Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).heightIn(min = viewportHeight)
                .padding(horizontal = 20.dp, vertical = 16.dp), verticalArrangement = Arrangement.SpaceBetween) {
                Column { ArticleBody(article) }
                Column(Modifier.fillMaxWidth().padding(top = 24.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(stringResource(R.string.support_unresolved), fontSize = 14.sp, lineHeight = 22.sp, fontWeight = FontWeight.Medium)
                    Button(onClick = feedback, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp), shape = RoundedCornerShape(10.dp)) {
                        Text(stringResource(R.string.support_fill_feedback), fontSize = 14.sp, lineHeight = 22.sp, fontWeight = FontWeight.Medium)
                    }
                    Text(stringResource(R.string.support_form_only), fontSize = 12.sp, lineHeight = 17.sp, color = DeviceColors.Secondary)
                }
            }
        }
    }
}

@Composable
private fun ArticleBody(article: Array<String>) {
    Column(Modifier.fillMaxWidth().padding(bottom = 8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(article[0], fontSize = 24.sp, lineHeight = 32.sp, fontWeight = FontWeight.Bold)
        Text(article[1], fontSize = 12.sp, lineHeight = 17.sp, color = DeviceColors.Secondary)
        Surface(shape = RoundedCornerShape(10.dp), color = DeviceColors.Selected) {
            Text(article[2], Modifier.fillMaxWidth().padding(12.dp), fontSize = 14.sp,
                lineHeight = 22.sp, color = DeviceColors.Secondary)
        }
    }
    article.drop(3).chunked(2).forEachIndexed { index, part ->
        Column(Modifier.fillMaxWidth().padding(top = 12.dp, bottom = 8.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(Modifier.fillMaxWidth().heightIn(min = 26.dp), verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("%02d".format(index + 1), Modifier.width(24.dp), color = DeviceColors.Blue,
                    fontSize = 13.sp, lineHeight = 18.sp, fontWeight = FontWeight.Medium,
                    textAlign = androidx.compose.ui.text.style.TextAlign.Center)
                Text(part[0], Modifier.weight(1f), fontSize = 18.sp, lineHeight = 26.sp, fontWeight = FontWeight.Bold)
            }
            Text(part[1], fontSize = 14.sp, lineHeight = 22.sp, color = DeviceColors.Secondary)
            HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant)
        }
    }
}
