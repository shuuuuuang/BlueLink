package com.bluelink.android

import android.app.Application
import android.net.Uri
import androidx.lifecycle.AndroidViewModel

class MainViewModel(application: Application) : AndroidViewModel(application) {
    private val runtime = (application as BlueLinkApplication).runtime
    val composer = runtime.composer
    val devices = runtime.devices
    val discovery = runtime.bluetooth.discovery
    val connection = runtime.connection
    val messages = runtime.messages
    val historyRevision = runtime.historyRevision
    suspend fun loadSearchHistory(peerId: String) = runtime.loadSearchHistory(peerId)
    suspend fun loadHistoryContext(peerId: String, id: java.util.UUID) = runtime.loadHistoryContext(peerId,id)
    suspend fun loadEarlierHistory(peerId: String) = runtime.loadEarlierHistory(peerId)
    val transfers = runtime.transfers
    val allTransfers = runtime.allTransfers
    val fileBatch = runtime.fileBatch
    val securityRequest = runtime.securityRequest
    fun confirmSecurityRequest(id: java.util.UUID) = runtime.confirmSecurityRequest(id)
    fun dismissSecurityRequest(id: java.util.UUID) = runtime.dismissSecurityRequest(id)
    fun retrySecurityRequest(id: java.util.UUID) = runtime.retrySecurityRequest(id)
    val trustPrompt = runtime.trustPrompt
    val fileConflictRequest = runtime.fileConflictRequest
    fun resolveFileConflict(id: java.util.UUID, choice: com.bluelink.android.files.DuplicateChoice) = runtime.resolveFileConflict(id, choice)
    val incomingFileRequest = runtime.incomingFileRequest
    fun confirmIncomingFile(id: java.util.UUID, accepted: Boolean) = runtime.confirmIncomingFile(id, accepted)
    val diagnostics = runtime.diagnostics
    val drafts = runtime.drafts
    val draftSaveFailed = runtime.draftSaveFailed
    fun editDraft(peerId: String, text: String) = runtime.editDraft(peerId, text)
    fun flushDrafts() = runtime.requestDraftFlush()
    fun savePeerPreference(peer: String,note: String?=null,pinned: Boolean?=null) = runtime.savePeerPreference(peer,note,pinned)
    val conversations = runtime.conversations
    val usbState = runtime.usbState
    fun retryUsb() = runtime.retryUsb()
    val settings = runtime.settings
    val identityFingerprint = runtime.identityFingerprint

    fun discover() = runtime.discover()
    fun connect(device: com.bluelink.android.domain.NearbyDevice) = runtime.connect(device)
    fun sendMessage(text: String) = runtime.sendChat(text, fromDraft = true)
    fun sendFile(uri: Uri, name: String, size: Long) = runtime.sendFile(uri, name, size)
    fun confirmTrust(accepted: Boolean, requestId: java.util.UUID) = runtime.confirmTrust(accepted, requestId)
    fun disconnect() = runtime.disconnect()
    fun disconnectPeer(peerId: String) = runtime.disconnectPeer(peerId)
    fun selectSession(sessionId: java.util.UUID) = runtime.selectSession(sessionId)
    fun selectPeer(peerId: String) = runtime.selectPeer(peerId)
    fun setVisibleMessagePeer(peerId: String?) = runtime.setVisibleMessagePeer(peerId)
    fun clearDiagnostics() = runtime.clearDiagnostics()
    fun saveSettings(value: com.bluelink.android.data.local.AppSettings) = runtime.saveSettings(value)
    val operationFailed = runtime.operationFailed
    fun dismissOperationFailure() = runtime.dismissOperationFailure()
    fun forgetAllPeers() = runtime.forgetAllPeers()
    fun forgetPeer(peerId: String) = runtime.forgetPeer(peerId)
    suspend fun performPrivacyAction(action: com.bluelink.android.domain.PrivacyAction) = runtime.performPrivacyAction(action)
    fun clearChatHistory() = runtime.clearChatHistory()
    fun clearConversation(peerId: String) = runtime.clearConversation(peerId)
    fun deleteMessage(messageId: java.util.UUID) = runtime.deleteMessage(messageId)
    suspend fun deleteSelectedMessages(peerId: String, ids: List<java.util.UUID>) = runtime.deleteSelectedMessages(peerId, ids)
    fun clearTransferHistory() = runtime.clearTransferHistory()
    fun deleteTransfer(transferId: java.util.UUID) = runtime.deleteTransfer(transferId)
    fun retryTransfer(value: com.bluelink.android.domain.TransferItem) = runtime.retryTransfer(value)
    fun switchQueuedToBluetooth(value: com.bluelink.android.domain.TransferItem) = runtime.switchQueuedToBluetooth(value)
    fun retryTransferFrom(value: com.bluelink.android.domain.TransferItem, uri: android.net.Uri) = runtime.retryTransferFrom(value, uri)
    fun pauseTransfer(value: com.bluelink.android.domain.TransferItem) = runtime.pauseTransfer(value)
    fun resumeTransfer(value: com.bluelink.android.domain.TransferItem) = runtime.resumeTransfer(value)
    fun cancelTransfer(value: com.bluelink.android.domain.TransferItem) = runtime.cancelTransfer(value)
}
