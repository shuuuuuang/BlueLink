package com.bluelink.android.domain

import com.bluelink.core.DeviceIdentity
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.security.MessageDigest
import java.util.UUID

enum class TrustStage { CONFIRM, WAITING, COMPLETED, CANCELED, REJECTED, TIMED_OUT, IDENTITY_CHANGED, REMOTE_CLOSED, REVOKED, FAILED }

/** One immutable identity pair and a single-use decision for one handshake. */
class SecurityRequest(
    val peerName: String,
    val safetyCode: String,
    val localFingerprint: String,
    val remoteFingerprint: String,
    val requiresConfirmation: Boolean,
    val trustedFingerprint: String? = null,
    val sessionId: UUID = UUID.randomUUID(),
    val transportAddress: String = "",
    val platform: PeerPlatform = PeerPlatform.UNKNOWN,
    val peerId: String = "",
    val identityCandidate: IdentityCandidate? = null,
) {
    val id: UUID = UUID.randomUUID()
    private val lock = Any()
    private val mutableStage = MutableStateFlow(if (requiresConfirmation) TrustStage.CONFIRM else TrustStage.WAITING)
    val stage = mutableStage.asStateFlow()
    val decision = CompletableDeferred<Boolean>().also { if (!requiresConfirmation) it.complete(true) }
    val abort = CompletableDeferred<Unit>()

    fun confirm(): Boolean = synchronized(lock) {
        if (mutableStage.value != TrustStage.CONFIRM) return false
        mutableStage.value = TrustStage.WAITING
        decision.complete(true)
    }
    fun cancel() = synchronized(lock) {
        when (mutableStage.value) {
            TrustStage.CONFIRM -> { mutableStage.value = TrustStage.CANCELED; decision.complete(false) }
            TrustStage.WAITING -> { mutableStage.value = TrustStage.CANCELED; abort.complete(Unit) }
            else -> Unit
        }
    }
    fun finish(value: TrustStage) = synchronized(lock) {
        if (mutableStage.value == TrustStage.CONFIRM || mutableStage.value == TrustStage.WAITING) {
            mutableStage.value = value
            decision.complete(false)
        }
    }
    fun complete(commit: () -> Unit): Boolean = synchronized(lock) {
        if (mutableStage.value != TrustStage.WAITING) return false
        commit()
        mutableStage.value = TrustStage.COMPLETED
        true
    }
    companion object {
        fun fingerprint(key: ByteArray): String = MessageDigest.getInstance("SHA-256").digest(key)
            .take(12).joinToString("") { "%02X".format(it) }.chunked(4).joinToString(":")
    }
}

data class IdentitySnapshot(val identity: DeviceIdentity, val trustEpoch: Long)
