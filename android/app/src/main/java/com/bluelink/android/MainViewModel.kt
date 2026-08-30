package com.bluelink.android

import android.app.Application
import android.net.Uri
import androidx.lifecycle.AndroidViewModel

class MainViewModel(application: Application) : AndroidViewModel(application) {
    private val runtime = (application as BlueLinkApplication).runtime
    val devices = runtime.bluetooth.devices
    val discovery = runtime.bluetooth.discovery
    val connection = runtime.connection
    val messages = runtime.messages
    val transfers = runtime.transfers
    val trustPrompt = runtime.trustPrompt
    val diagnostics = runtime.diagnostics
    val conversations = runtime.conversations
    val settings = runtime.settings
    val identityFingerprint = runtime.identityFingerprint

    fun discover() = runtime.discover()
    fun connect(device: com.bluelink.android.domain.NearbyDevice) = runtime.connect(device)
    fun sendMessage(text: String) = runtime.sendChat(text)
    fun sendFile(uri: Uri, name: String, size: Long) = runtime.sendFile(uri, name, size)
    fun confirmTrust(accepted: Boolean) = runtime.confirmTrust(accepted)
    fun disconnect() = runtime.disconnect()
    fun disconnectPeer(peerId: String) = runtime.disconnectPeer(peerId)
    fun selectSession(sessionId: java.util.UUID) = runtime.selectSession(sessionId)
    fun selectPeer(peerId: String) = runtime.selectPeer(peerId)
    fun clearDiagnostics() = runtime.clearDiagnostics()
    fun saveSettings(value: com.bluelink.android.data.local.AppSettings) = runtime.saveSettings(value)
    fun forgetPeer(peerId: String) = runtime.forgetPeer(peerId)
    fun clearChatHistory() = runtime.clearChatHistory()
    fun clearConversation(peerId: String) = runtime.clearConversation(peerId)
    fun deleteMessage(messageId: java.util.UUID) = runtime.deleteMessage(messageId)
    fun clearTransferHistory() = runtime.clearTransferHistory()
    fun deleteTransfer(transferId: java.util.UUID) = runtime.deleteTransfer(transferId)
    fun retryTransfer(value: com.bluelink.android.domain.TransferItem) = runtime.retryTransfer(value)
    fun pauseTransfer(value: com.bluelink.android.domain.TransferItem) = runtime.pauseTransfer(value)
    fun resumeTransfer(value: com.bluelink.android.domain.TransferItem) = runtime.resumeTransfer(value)
    fun cancelTransfer(value: com.bluelink.android.domain.TransferItem) = runtime.cancelTransfer(value)
}
