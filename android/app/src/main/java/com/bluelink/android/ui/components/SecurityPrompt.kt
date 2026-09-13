package com.bluelink.android.ui.components

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.*
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.*
import com.bluelink.android.R
import com.bluelink.android.domain.*
import com.bluelink.android.ui.devices.*

/** Security family: 374:3, 1004:4538/4661, 1006:4693/4817. */
@Composable
internal fun SecurityPrompt(request: SecurityRequest, confirm: () -> Unit, dismiss: () -> Unit,
                            retry: () -> Unit, manageTrust: () -> Unit) {
    val stage by request.stage.collectAsState()
    if (stage == TrustStage.COMPLETED || stage == TrustStage.CANCELED) return
    val context = LocalContext.current
    val configuration = LocalConfiguration.current
    Dialog(dismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        CompositionLocalProvider(LocalContext provides context, LocalConfiguration provides configuration) {
            val window = (LocalView.current.parent as? DialogWindowProvider)?.window
            SideEffect { window?.setDimAmount(.42f) }
            DeviceScreenTheme {
                Box(Modifier.fillMaxWidth().padding(horizontal = 16.dp), contentAlignment = Alignment.Center) {
                    Surface(color = DeviceColors.Surface, shape = RoundedCornerShape(26.dp),
                        modifier = Modifier.widthIn(max = 380.dp).fillMaxWidth()
                            .heightIn(max = (configuration.screenHeightDp - 48).coerceAtLeast(260).dp)) {
                        Column(horizontalAlignment = Alignment.CenterHorizontally) {
                            Column(Modifier.weight(1f, fill = false).verticalScroll(rememberScrollState())
                                .fillMaxWidth().padding(start = 20.dp, end = 20.dp, top = 10.dp),
                                horizontalAlignment = Alignment.CenterHorizontally) {
                                BlueLinkLogo(Modifier.size(58.dp))
                                Spacer(Modifier.height(4.dp))
                                Text(stringResource(when (stage) {
                                    TrustStage.CONFIRM -> if (request.identityCandidate != null) R.string.security_identity_candidate else R.string.security_confirm
                                    TrustStage.WAITING -> R.string.security_waiting
                                    TrustStage.REJECTED -> R.string.security_rejected
                                    TrustStage.TIMED_OUT -> R.string.security_timeout
                                    TrustStage.IDENTITY_CHANGED -> R.string.security_changed
                                    TrustStage.REVOKED -> R.string.security_revoked
                                    else -> R.string.security_closed
                                }), Modifier.fillMaxWidth(), textAlign = TextAlign.Center,
                                    fontSize = 24.sp, lineHeight = 34.sp, fontWeight = FontWeight.Bold)
                                Row(Modifier.heightIn(min = 30.dp), verticalAlignment = Alignment.CenterVertically,
                                    horizontalArrangement = Arrangement.spacedBy(12.dp, Alignment.CenterHorizontally)) {
                                    FigmaIcon(when (request.platform) {
                                        PeerPlatform.ANDROID -> R.drawable.figma_phone
                                        PeerPlatform.WINDOWS -> R.drawable.figma_desktop
                                        else -> R.drawable.figma_generic
                                    }, size = 22.dp, tint = DeviceColors.Blue)
                                    Text(request.peerName, fontSize = 17.sp, lineHeight = 26.sp,
                                        fontWeight = FontWeight.Medium, maxLines = 2, overflow = TextOverflow.Ellipsis)
                                }
                                Row(Modifier.fillMaxWidth().height(48.dp), verticalAlignment = Alignment.CenterVertically,
                                    horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                                    HorizontalDivider(Modifier.weight(1f), color = DeviceColors.Border)
                                    FigmaIcon(R.drawable.figma_security_shield, size = 48.dp)
                                    HorizontalDivider(Modifier.weight(1f), color = DeviceColors.Border)
                                }
                                val checking = stage == TrustStage.CONFIRM || stage == TrustStage.WAITING
                                Text(stringResource(if (checking) R.string.security_code else R.string.security_status),
                                    fontSize = 14.sp, lineHeight = 20.sp, fontWeight = FontWeight.Medium)
                                val status = if (checking) request.safetyCode else stringResource(when (stage) {
                                    TrustStage.REJECTED -> R.string.security_declined
                                    TrustStage.TIMED_OUT -> R.string.security_expired
                                    TrustStage.IDENTITY_CHANGED, TrustStage.REVOKED -> R.string.security_blocked
                                    else -> R.string.security_not_connected
                                })
                                Text(status, Modifier.fillMaxWidth().heightIn(min = 52.dp), textAlign = TextAlign.Center,
                                    fontSize = if (stage == TrustStage.CONFIRM) 44.sp else 24.sp,
                                    lineHeight = if (stage == TrustStage.CONFIRM) 52.sp else 32.sp,
                                    fontWeight = FontWeight.Bold,
                                    color = if (checking) DeviceColors.Blue else if (stage in setOf(TrustStage.TIMED_OUT,
                                        TrustStage.IDENTITY_CHANGED, TrustStage.REVOKED)) DeviceColors.Error else DeviceColors.Secondary)
                                Text(stringResource(when (stage) {
                                    TrustStage.CONFIRM -> R.string.security_confirm_note
                                    TrustStage.WAITING -> R.string.security_wait_note
                                    TrustStage.REJECTED -> R.string.security_reject_note
                                    TrustStage.TIMED_OUT -> R.string.security_timeout_note
                                    TrustStage.IDENTITY_CHANGED -> R.string.security_changed_note
                                    TrustStage.REMOTE_CLOSED -> R.string.security_closed_note
                                    TrustStage.REVOKED -> R.string.security_revoked_note
                                    else -> R.string.security_failed_note
                                }), Modifier.fillMaxWidth().heightIn(min = 22.dp), textAlign = TextAlign.Center,
                                    fontSize = 13.sp, lineHeight = 19.sp, color = DeviceColors.Secondary)
                                Spacer(Modifier.height(16.dp))
                                if (request.identityCandidate != null && checking) {
                                    Text(stringResource(R.string.security_candidate_detail, request.identityCandidate.displayName),
                                        Modifier.fillMaxWidth().padding(bottom = 12.dp), textAlign = TextAlign.Center,
                                        fontSize = 13.sp, lineHeight = 20.sp, color = DeviceColors.Secondary)
                                }
                                Fingerprints(request, stage == TrustStage.IDENTITY_CHANGED || request.identityCandidate != null)
                                if (stage == TrustStage.IDENTITY_CHANGED) Text(stringResource(R.string.security_changed_help),
                                    Modifier.padding(top = 10.dp), textAlign = TextAlign.Center,
                                    fontSize = 12.sp, lineHeight = 18.sp, color = DeviceColors.Secondary)
                            }
                            Row(Modifier.fillMaxWidth().padding(start = 20.dp, end = 20.dp, top = 30.dp, bottom = 28.dp),
                                horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                                OutlinedButton(onClick = dismiss, shape = RoundedCornerShape(13.dp),
                                    modifier = Modifier.weight(1f).heightIn(min = 52.dp), contentPadding = PaddingValues(8.dp),
                                    border = BorderStroke(1.dp, DeviceColors.Border),
                                    colors = ButtonDefaults.outlinedButtonColors(contentColor = DeviceColors.Blue)) {
                                    Text(stringResource(when (stage) { TrustStage.CONFIRM -> R.string.cancel
                                        TrustStage.WAITING -> R.string.security_cancel_connection
                                        else -> R.string.close }), fontSize = 16.sp, fontWeight = FontWeight.Medium, textAlign = TextAlign.Center)
                                }
                                val enabled = stage != TrustStage.WAITING
                                Button(onClick = { when (stage) { TrustStage.CONFIRM -> confirm()
                                    TrustStage.IDENTITY_CHANGED, TrustStage.REVOKED -> manageTrust()
                                    else -> retry() } }, enabled = enabled, shape = RoundedCornerShape(13.dp),
                                    modifier = Modifier.weight(1.08f).heightIn(min = 52.dp).clip(RoundedCornerShape(13.dp))
                                        .background(Brush.horizontalGradient(listOf(Color(0xFF1677FF), Color(0xFF0050EA))
                                            .map { it.copy(alpha = if (enabled) 1f else .5f) })),
                                    contentPadding = PaddingValues(8.dp), colors = ButtonDefaults.buttonColors(
                                        containerColor = Color.Transparent, contentColor = Color.White,
                                        disabledContainerColor = Color.Transparent, disabledContentColor = Color.White)) {
                                    Text(stringResource(when (stage) { TrustStage.CONFIRM -> if (request.identityCandidate != null) R.string.security_link_identity else R.string.security_trust
                                        TrustStage.WAITING -> R.string.security_confirmed
                                        TrustStage.IDENTITY_CHANGED, TrustStage.REVOKED -> R.string.security_manage_trust
                                        else -> R.string.security_retry }), fontSize = 16.sp, fontWeight = FontWeight.Medium, textAlign = TextAlign.Center)
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun Fingerprints(request: SecurityRequest, changed: Boolean) {
    Surface(Modifier.fillMaxWidth(), color = DeviceColors.Canvas, shape = RoundedCornerShape(14.dp),
        border = BorderStroke(1.dp, DeviceColors.Border)) {
        Column(Modifier.padding(horizontal = 12.dp, vertical = 4.dp)) {
            FingerprintRow(stringResource(if (changed) R.string.security_current_fingerprint else R.string.security_local_fingerprint),
                if (changed) request.remoteFingerprint else request.localFingerprint)
            HorizontalDivider(Modifier.padding(start = 45.dp), color = DeviceColors.Border)
            FingerprintRow(stringResource(if (changed) R.string.security_trusted_fingerprint else R.string.security_remote_fingerprint),
                if (changed) request.trustedFingerprint.orEmpty() else request.remoteFingerprint)
        }
    }
}
@Composable
private fun FingerprintRow(label: String, value: String) {
    BoxWithConstraints(Modifier.fillMaxWidth()) {
        val compact = maxWidth < 316.dp || LocalConfiguration.current.fontScale > 1.1f
        Row(Modifier.heightIn(min = 43.dp).padding(vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(38.dp).background(DeviceColors.Blue.copy(alpha = .06f), CircleShape), contentAlignment = Alignment.Center) {
                FigmaIcon(R.drawable.figma_security_fingerprint, size = 26.dp)
            }
            Spacer(Modifier.width(7.dp))
            if (compact) {
                Column(Modifier.weight(1f)) {
                    Text(label, fontSize = 12.sp, lineHeight = 18.sp, color = DeviceColors.Secondary)
                    Text(value, fontSize = 11.sp, lineHeight = 18.sp, color = DeviceColors.Secondary)
                }
            } else {
                Text(label, Modifier.weight(1f), fontSize = 13.sp, color = DeviceColors.Secondary)
                Text(value, fontSize = 11.sp, color = DeviceColors.Secondary)
            }
        }
    }
}
