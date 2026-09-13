package com.bluelink.android.bluetooth

import android.Manifest
import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothManager
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import com.bluelink.android.domain.BluetoothAccessState

val nearbyPermissions = arrayOf(
    Manifest.permission.BLUETOOTH_SCAN,
    Manifest.permission.BLUETOOTH_CONNECT,
    Manifest.permission.BLUETOOTH_ADVERTISE,
)

fun markBluetoothPermissionRequested(context: Context) {
    context.getSharedPreferences("bluelink.permission-ui", Context.MODE_PRIVATE)
        .edit().putBoolean("bluetooth-requested", true).apply()
}

@SuppressLint("MissingPermission")
fun readBluetoothAccess(context: Context): BluetoothAccessState {
    val granted = nearbyPermissions.all { context.checkSelfPermission(it) == PackageManager.PERMISSION_GRANTED }
    val enabled = granted && runCatching {
        context.getSystemService(BluetoothManager::class.java)?.adapter?.isEnabled == true
    }.getOrDefault(false)
    val requested = context.getSharedPreferences("bluelink.permission-ui", Context.MODE_PRIVATE)
        .getBoolean("bluetooth-requested", false)
    return BluetoothAccessState.resolve(granted, enabled, requested)
}

/** Re-read on return from Settings and on adapter broadcasts; never cache a grant across a resume. */
@Composable
fun rememberBluetoothAccess(refreshKey: Int): BluetoothAccessState {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    var access by remember(context) { mutableStateOf(readBluetoothAccess(context)) }
    LaunchedEffect(refreshKey) { access = readBluetoothAccess(context) }
    DisposableEffect(context, lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) access = readBluetoothAccess(context)
        }
        val receiver = object : BroadcastReceiver() {
            override fun onReceive(context: Context, intent: Intent) {
                if (intent.action == BluetoothAdapter.ACTION_STATE_CHANGED) access = readBluetoothAccess(context)
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        ContextCompat.registerReceiver(context, receiver, IntentFilter(BluetoothAdapter.ACTION_STATE_CHANGED),
            ContextCompat.RECEIVER_EXPORTED)
        onDispose {
            lifecycleOwner.lifecycle.removeObserver(observer)
            context.unregisterReceiver(receiver)
        }
    }
    return access
}
