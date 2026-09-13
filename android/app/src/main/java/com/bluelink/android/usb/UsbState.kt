package com.bluelink.android.usb

import com.bluelink.android.domain.*

enum class UsbStage { OFF, WAITING, AUTHORIZATION, PERMISSION_DENIED, NEGOTIATING, READY,
    HIGH_SPEED, FULL_SPEED, FALLBACK, UNAVAILABLE, DRIVER_MISSING, POLICY_BLOCKED, UNSUPPORTED }

data class UsbSnapshot(val stage: UsbStage = UsbStage.OFF, val peerName: String = "",
    val peerId: String? = null, val permissionGranted: Boolean = false,
    val bluetoothAvailable: Boolean = false)

internal object UsbStatePolicy {
    fun isReady(enabled: Boolean, peerId: String?, sessions: List<ManagedSessionState>) = enabled && !peerId.isNullOrBlank() &&
        sessions.any { it.phase == ConnectionPhase.CONNECTED && it.usbFileReady && it.peerId.equals(peerId, true) }

    fun noticeStage(enabled: Boolean, peerId: String?, ready: Boolean, snapshot: UsbSnapshot): UsbStage? =
        if (!enabled || ready || peerId.isNullOrBlank() || !snapshot.peerId.equals(peerId, true)) null
        else snapshot.stage.takeIf { it in setOf(UsbStage.AUTHORIZATION, UsbStage.NEGOTIATING, UsbStage.FALLBACK, UsbStage.UNAVAILABLE,
            UsbStage.PERMISSION_DENIED, UsbStage.DRIVER_MISSING, UsbStage.POLICY_BLOCKED, UsbStage.UNSUPPORTED) }
    fun isBlueLink(manufacturer: String?, model: String?) = manufacturer == "BlueLink" && model == "BlueLink"
    fun bluetoothAvailable(sessions: List<ManagedSessionState>, peerId: String?) = !peerId.isNullOrBlank() &&
        sessions.any { it.transport == SessionTransport.BLUETOOTH && it.phase == ConnectionPhase.CONNECTED && it.peerId.equals(peerId, true) }
    fun disconnected(sessions: List<ManagedSessionState>, peerId: String?) =
        if (bluetoothAvailable(sessions, peerId)) UsbStage.FALLBACK else UsbStage.UNAVAILABLE
}
