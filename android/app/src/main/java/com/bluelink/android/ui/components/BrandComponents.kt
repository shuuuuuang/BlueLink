package com.bluelink.android.ui.components

import android.graphics.BitmapFactory
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.rounded.Devices
import androidx.compose.material.icons.rounded.Forum
import androidx.compose.material.icons.rounded.Menu
import androidx.compose.material.icons.rounded.Settings
import androidx.compose.material.icons.rounded.SwapVert
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.bluelink.android.domain.ConnectionPhase
import com.bluelink.android.domain.ConnectionState
import com.bluelink.android.ui.theme.BrandBlue
import com.bluelink.android.ui.theme.Canvas
import com.bluelink.android.ui.theme.Ink
import com.bluelink.android.ui.theme.Lavender
import com.bluelink.android.ui.theme.Muted
import com.bluelink.android.ui.theme.SoftGreen
import com.bluelink.android.ui.theme.Success

@Composable
fun BlueLinkLogo(modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val bitmap = remember {
        context.assets.open("bluelink-final-logo.png").use(BitmapFactory::decodeStream)
    }
    androidx.compose.foundation.Image(
        bitmap = bitmap.asImageBitmap(),
        contentDescription = "蓝联",
        modifier = modifier,
        contentScale = ContentScale.Fit,
    )
}

@Composable
fun BlueLinkAppHeader(
    state: ConnectionState,
    settings: Boolean = false,
    onBack: (() -> Unit)? = null,
    onSettings: (() -> Unit)? = null,
) {
    Surface(color = Canvas) {
        Row(
            Modifier.fillMaxWidth().height(100.dp).padding(horizontal = 20.dp, vertical = 13.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            if (onBack != null) {
                IconButton(onClick = onBack, modifier = Modifier.size(46.dp)) {
                    Icon(Icons.AutoMirrored.Rounded.ArrowBack, "返回", tint = Ink)
                }
                Spacer(Modifier.width(4.dp))
            }
            BlueLinkLogo(Modifier.size(if (settings) 48.dp else 58.dp))
            Spacer(Modifier.width(11.dp))
            Column(Modifier.weight(1f)) {
                Text(if (settings) "设置" else "蓝联", color = Ink,
                    style = if (settings) MaterialTheme.typography.headlineSmall else MaterialTheme.typography.headlineMedium,
                    fontWeight = FontWeight.Bold)
                Text(if (settings) "连接、文件与本地数据" else "BLUETOOTH ONLY", color = BrandBlue,
                    style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.Bold)
            }
            ConnectionPill(state)
            if (onSettings != null) {
                Spacer(Modifier.width(5.dp))
                IconButton(onClick = onSettings, modifier = Modifier.size(46.dp)) {
                    Icon(Icons.Rounded.Settings, "设置", tint = Ink)
                }
            }
        }
    }
}

@Composable
fun ConnectionPill(state: ConnectionState) {
    val connected = state.phase == ConnectionPhase.CONNECTED
    Surface(color = if (connected) SoftGreen else Color(0xFFE9EEF7), shape = RoundedCornerShape(50)) {
        Row(Modifier.padding(horizontal = 12.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(8.dp).background(if (connected) Success else Muted, CircleShape))
            Spacer(Modifier.width(7.dp))
            Text(if (connected) "已加密" else "蓝牙", color = if (connected) Color(0xFF24593E) else Ink,
                style = MaterialTheme.typography.labelLarge)
        }
    }
}

private val destinations = listOf(
    Triple("设备", Icons.Rounded.Devices, "设备与会话"),
    Triple("聊天", Icons.Rounded.Forum, "聊天"),
    Triple("传输", Icons.Rounded.SwapVert, "文件传输"),
    Triple("诊断", Icons.Rounded.Menu, "连接诊断"),
)

@Composable
fun BlueLinkBottomNavigation(selected: Int, onSelected: (Int) -> Unit) {
    Surface(color = Color.White, shadowElevation = 12.dp) {
        Row(
            Modifier.fillMaxWidth().height(84.dp).padding(horizontal = 10.dp, vertical = 8.dp),
            horizontalArrangement = Arrangement.SpaceEvenly,
        ) {
            destinations.forEachIndexed { index, destination ->
                Column(
                    Modifier.weight(1f).clip(RoundedCornerShape(20.dp)).padding(vertical = 3.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    Surface(
                        onClick = { onSelected(index) },
                        color = if (selected == index) Lavender else Color.Transparent,
                        shape = RoundedCornerShape(22.dp),
                        modifier = Modifier.size(width = 66.dp, height = 42.dp),
                    ) {
                        Box(contentAlignment = Alignment.Center) {
                            Icon(destination.second, destination.third,
                                tint = if (selected == index) BrandBlue else Ink, modifier = Modifier.size(23.dp))
                        }
                    }
                    Text(destination.first, color = if (selected == index) Ink else Muted,
                        style = MaterialTheme.typography.labelMedium,
                        fontWeight = if (selected == index) FontWeight.Bold else FontWeight.Medium)
                }
            }
        }
    }
}

@Composable
fun DevicePlatformIcon(android: Boolean, modifier: Modifier = Modifier) {
    Surface(color = Color(0xFFEAF1FF), shape = RoundedCornerShape(15.dp), modifier = modifier) {
        Box(contentAlignment = Alignment.Center) {
            Icon(Icons.Rounded.Devices, if (android) "Android" else "Windows", tint = BrandBlue,
                modifier = Modifier.size(23.dp))
        }
    }
}
