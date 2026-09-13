package com.bluelink.android.usb

import android.content.Context
import com.bluelink.android.domain.ManagedSessionState
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

/** Presentation only. WPD readiness belongs to authenticated Bluetooth peers, not to an Accessory intent. */
internal class MtpDirectoryController(private val context: Context, private val sessions: () -> List<ManagedSessionState>,
    private val configureSessions: (Boolean) -> Unit, private val requestProbe: () -> Unit) {
    private val mutable = MutableStateFlow(UsbSnapshot())
    val state = mutable.asStateFlow()
    private var enabled = false
    fun configure(value: Boolean, active: Boolean) { enabled = value && active; configureSessions(enabled); observe() }
    fun refresh(retry: Boolean = false) { if (retry && enabled) requestProbe(); observe() }
    fun observe() {
        val ready = sessions().firstOrNull { it.usbFileReady }
        mutable.value = when {
            !enabled -> UsbSnapshot()
            !MtpSpool.hasGrant(context) -> UsbSnapshot(UsbStage.AUTHORIZATION)
            ready != null -> UsbSnapshot(UsbStage.READY, ready.peerName, ready.peerId, true, true)
            else -> UsbSnapshot(UsbStage.WAITING, permissionGranted = true)
        }
    }
}
