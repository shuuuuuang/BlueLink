package com.bluelink.android.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import android.app.Activity
import android.content.res.Configuration
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.graphics.toArgb
import androidx.core.view.WindowInsetsControllerCompat
import com.bluelink.android.ui.devices.DeviceFont
import java.util.Locale
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

val BrandBlue = Color(0xFF176BFF)
val BrandCyan = Color(0xFF12C8E8)
val Ink = Color(0xFF162033)
val Muted = Color(0xFF687386)
val Canvas = Color(0xFFF5F7FB)
val Surface = Color(0xFFFFFFFF)
val Lavender = Color(0xFFF0ECFF)
val Success = Color(0xFF13A663)
val Warning = Color(0xFFB06A00)
val Danger = Color(0xFFD92D20)
val Border = Color(0xFFDCE3EF)
val SoftBlue = Color(0xFFEAF1FF)
val SoftGreen = Color(0xFFE7F8EF)
val SoftWarning = Color(0xFFFFF4E5)
val SoftDanger = Color(0xFFFDECEC)

private val LightColors = lightColorScheme(
    primary = Color(0xFF1677FF), onPrimary = Color.White,
    primaryContainer = Color(0xFFEAF2FF), onPrimaryContainer = Color(0xFF17233C),
    background = Color(0xFFF6F8FC), onBackground = Color(0xFF17233C),
    surface = Color.White, onSurface = Color(0xFF17233C),
    surfaceVariant = Color(0xFFF1F5F9), onSurfaceVariant = Color(0xFF70819B),
    outline = Color(0xFFDCE4EF), outlineVariant = Color(0xFFEEF2F7), error = Color(0xFFDC3545),
)

// The Android Figma foundations currently supply a light mode. Dark surfaces
// use BlueLink's existing shared dark palette, preserving the same hierarchy.
private val DarkColors = darkColorScheme(
    primary = Color(0xFF70AAFF), onPrimary = Color(0xFF10213B),
    primaryContainer = Color(0xFF203858), onPrimaryContainer = Color(0xFFEEF2F9),
    background = Color(0xFF10151F), onBackground = Color(0xFFEEF2F9),
    surface = Color(0xFF1E2633), onSurface = Color(0xFFEEF2F9),
    surfaceVariant = Color(0xFF293443), onSurfaceVariant = Color(0xFFB1BED2),
    outline = Color(0xFF35445A), outlineVariant = Color(0xFF2B3748), error = Color(0xFFFF8A80),
)

private val BlueLinkTypography = Typography(
    headlineLarge = TextStyle(fontFamily = DeviceFont, fontWeight = FontWeight.Bold, fontSize = 30.sp, lineHeight = 38.sp),
    headlineMedium = TextStyle(fontFamily = DeviceFont, fontWeight = FontWeight.Bold, fontSize = 25.sp, lineHeight = 32.sp),
    headlineSmall = TextStyle(fontFamily = DeviceFont, fontWeight = FontWeight.Bold, fontSize = 22.sp, lineHeight = 29.sp),
    titleLarge = TextStyle(fontFamily = DeviceFont, fontWeight = FontWeight.Bold, fontSize = 20.sp, lineHeight = 27.sp),
    titleMedium = TextStyle(fontFamily = DeviceFont, fontWeight = FontWeight.SemiBold, fontSize = 17.sp, lineHeight = 24.sp),
    titleSmall = TextStyle(fontFamily = DeviceFont, fontWeight = FontWeight.SemiBold, fontSize = 15.sp, lineHeight = 21.sp),
    bodyLarge = TextStyle(fontFamily = DeviceFont, fontWeight = FontWeight.Normal, fontSize = 16.sp, lineHeight = 23.sp),
    bodyMedium = TextStyle(fontFamily = DeviceFont, fontWeight = FontWeight.Normal, fontSize = 14.sp, lineHeight = 21.sp),
    bodySmall = TextStyle(fontFamily = DeviceFont, fontWeight = FontWeight.Normal, fontSize = 12.sp, lineHeight = 18.sp),
    labelLarge = TextStyle(fontFamily = DeviceFont, fontWeight = FontWeight.SemiBold, fontSize = 14.sp, lineHeight = 19.sp),
    labelMedium = TextStyle(fontFamily = DeviceFont, fontWeight = FontWeight.Medium, fontSize = 12.sp, lineHeight = 17.sp),
    labelSmall = TextStyle(fontFamily = DeviceFont, fontWeight = FontWeight.Medium, fontSize = 10.sp, lineHeight = 14.sp),
)

@Composable
fun BlueLinkTheme(theme: String = "system", language: String = "zh-CN", content: @Composable () -> Unit) {
    val dark = when (theme) { "dark" -> true; "light" -> false; else -> isSystemInDarkTheme() }
    val colors = if (dark) DarkColors else LightColors
    val context = LocalContext.current
    val currentConfiguration = LocalConfiguration.current
    val configuration = remember(currentConfiguration, language) {
        Configuration(currentConfiguration).apply {
            if (language != "system") setLocale(Locale.forLanguageTag(language))
        }
    }
    // Preserve the Activity in the ContextWrapper chain. A plain configuration
    // Context hides ActivityResultRegistryOwner from image/file launchers.
    val localizedContext = remember(context, configuration) {
        android.view.ContextThemeWrapper(context, 0).apply { applyOverrideConfiguration(configuration) }
    }
    val view = LocalView.current
    SideEffect {
        (view.context as? Activity)?.window?.let { window ->
            window.statusBarColor = colors.background.toArgb()
            window.navigationBarColor = colors.surface.toArgb()
            WindowInsetsControllerCompat(window, view).apply {
                isAppearanceLightStatusBars = !dark
                isAppearanceLightNavigationBars = !dark
            }
        }
    }
    CompositionLocalProvider(LocalContext provides localizedContext, LocalConfiguration provides configuration) {
        MaterialTheme(colorScheme = colors, typography = BlueLinkTypography, content = content)
    }
}
