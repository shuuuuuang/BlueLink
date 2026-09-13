package com.bluelink.android.ui.devices

import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.LocalTextStyle
import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp
import com.bluelink.android.R

/** Android Foundations 414:4–414:12; scoped while remaining screens are migrated. */
internal object DeviceColors {
    val Blue @Composable get() = MaterialTheme.colorScheme.primary
    val Canvas @Composable get() = MaterialTheme.colorScheme.background
    val Ink @Composable get() = MaterialTheme.colorScheme.onSurface
    val Secondary @Composable get() = MaterialTheme.colorScheme.onSurfaceVariant
    val Border @Composable get() = MaterialTheme.colorScheme.outline
    val Selected @Composable get() = MaterialTheme.colorScheme.primaryContainer
    val Surface @Composable get() = MaterialTheme.colorScheme.surface
    val SwitchOff @Composable get() = if (MaterialTheme.colorScheme.surface == Color.White) Color(0xFFCBD5E1) else Color(0xFF35445A)
    val Success @Composable get() = if (MaterialTheme.colorScheme.surface == Color.White) Color(0xFF16A05D) else Color(0xFF62D9A5)
    val Warning @Composable get() = if (MaterialTheme.colorScheme.surface == Color.White) Color(0xFFF0A51A) else Color(0xFFFFD58B)
    val Error @Composable get() = MaterialTheme.colorScheme.error
}

// Static instances of the original Noto Sans SC variable font keep weight
// consistent on device font loaders that ignore the variation axis.
internal val DeviceFont = FontFamily(
    Font(R.font.noto_sans_sc_regular, weight = FontWeight.Normal),
    Font(R.font.noto_sans_sc_medium, weight = FontWeight.Medium),
    Font(R.font.noto_sans_sc_bold, weight = FontWeight.Bold),
)

@Composable
internal fun DeviceScreenTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = MaterialTheme.colorScheme.copy(
        primary = DeviceColors.Blue, onPrimary = MaterialTheme.colorScheme.onPrimary, surface = DeviceColors.Surface,
        onSurface = DeviceColors.Ink, outline = DeviceColors.Border,
    )) {
        CompositionLocalProvider(
            LocalContentColor provides DeviceColors.Ink,
            LocalTextStyle provides TextStyle(fontFamily = DeviceFont, fontWeight = FontWeight.Normal,
                fontSize = 14.sp, lineHeight = 22.sp),
            content = content,
        )
    }
}
