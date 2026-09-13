package com.bluelink.android.session

import com.bluelink.android.domain.*
import com.bluelink.core.*
import kotlinx.coroutines.*
import kotlinx.coroutines.selects.select
import java.io.*
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference

internal class TrustHandshakeException(val stage: TrustStage, cause: Throwable? = null) :
    IOException("Secure connection: ${stage.name}", cause)

internal data class SecureHandshakeResult(val keys: SessionKeys,
    val negotiation: ProtocolGreeting.Negotiation, val replay: ReplayGuard, val remoteDeviceName: String = "")

/** The existing BTX encrypted greeting proves remote confirmation before trust is persisted. */
internal class SecureConnectionHandshake(
    private val input: InputStream,
    private val output: OutputStream,
    private val closeTransport: () -> Unit,
    private val listenerRole: Boolean,
    private val captureIdentity: () -> IdentitySnapshot,
    private val trustedKey: (ByteArray) -> ByteArray?,
    private val expectedTrustedKeys: suspend () -> List<ByteArray>,
    private val beginTrust: (ByteArray, ByteArray, Long) -> PeerTrustRegistry.Verification,
    private val commitTrust: (PeerTrustRegistry.Verification) -> Unit,
    private val peerName: String,
    private val sessionId: UUID,
    private val transportAddress: String,
    private val platform: PeerPlatform,
    private val present: (SecurityRequest) -> Unit,
    private val finished: (SecurityRequest) -> Unit,
    private val identified: (String) -> Unit,
    private val timeoutMs: Long = 90_000,
    private val localDeviceName: String = "",
    private val findCandidate: suspend (String) -> IdentityCandidate? = { null },
    private val commitAssociation: ((PeerTrustRegistry.Verification, IdentityCandidate) -> Unit)? = null,
    private val applyAssociation: suspend () -> Unit = {},
) {
    suspend fun run(): SecureHandshakeResult = supervisorScope {
        val requestRef = AtomicReference<SecurityRequest?>()
        val expired = AtomicBoolean()
        val shown = AtomicBoolean()
        val succeeded = AtomicBoolean()
        var derivedKeys: SessionKeys? = null
        val operation = async(Dispatchers.IO) {
            val snapshot = captureIdentity()
            val expected = expectedTrustedKeys()
            val local = HandshakeHello.create(snapshot.identity)
            val remote = if (listenerRole) readHello().also { writeHello(local) }
                else { writeHello(local); readHello() }
            val keys = local.derive(remote).also { derivedKeys = it }
            val remoteKey = keys.remoteIdentityPublicKey()
            val known = trustedKey(keys.remotePeerId())
            val remoteId = keys.remotePeerId().joinToString("") { "%02x".format(it) }
            val candidate = if (known == null && commitAssociation != null) findCandidate(remoteId) else null
            val changedKey = if (known != null && !MessageDigest.isEqual(known, remoteKey)) known
                else expected.firstOrNull()?.takeIf { expected.none { MessageDigest.isEqual(it, remoteKey) } }
            val request = SecurityRequest(peerName, keys.formattedSafetyCode(),
                SecurityRequest.fingerprint(snapshot.identity.publicKey()), SecurityRequest.fingerprint(remoteKey),
                known == null || changedKey != null, (candidate?.publicKey ?: changedKey)?.let(SecurityRequest::fingerprint),
                sessionId, transportAddress, platform, remoteId, candidate).also(requestRef::set)
            identified(keys.remotePeerId().joinToString("") { "%02x".format(it) })
            if (changedKey != null && candidate == null) throw TrustHandshakeException(TrustStage.IDENTITY_CHANGED)
            val verification = try { beginTrust(keys.remotePeerId(), remoteKey, snapshot.trustEpoch) }
                catch (failure: IllegalStateException) { throw TrustHandshakeException(TrustStage.REVOKED, failure) }
            supervisorScope {
                // Exactly one reader owns this stream. readLoop receives the same replay guard afterwards.
                val replay = ReplayGuard(0)
                val greeting = async(Dispatchers.IO) {
                    BtxRecordCodec.read(input, keys.receiveKey(), keys.receiveNoncePrefix(), replay).also(::validateGreeting)
                }
                val decision = async decisionBlock@ {
                    if (request.requiresConfirmation) {
                        present(request); shown.set(true)
                    }
                    request.decision.await()
                }
                val abort = launch { request.abort.await(); runCatching(closeTransport); greeting.cancel() }
                try {
                    select<Unit> {
                        greeting.onAwait { }
                        decision.onAwait { }
                    }
                    if (!decision.await()) {
                        request.cancel()
                        BtxRecordCodec.write(output, BtxFrame(WireMessageType.GOAWAY, 0, 0, 0,
                            "TRUST_REJECTED".toByteArray(Charsets.UTF_8)), keys.sendKey(), keys.sendNoncePrefix())
                        throw TrustHandshakeException(TrustStage.CANCELED)
                    }
                    currentCoroutineContext().ensureActive()
                    BtxRecordCodec.write(output, BtxFrame(WireMessageType.PROTOCOL_HELLO, 0, 0, 0,
                        DeviceNameGreeting.encode(localDeviceName)), keys.sendKey(), keys.sendNoncePrefix())
                    val frame = greeting.await()
                    val negotiation = ProtocolGreeting.current().negotiate(ProtocolGreeting.decode(frame.payload()))
                    currentCoroutineContext().ensureActive()
                    val committed = request.complete {
                        try {
                            if (candidate == null) commitTrust(verification) else commitAssociation!!.invoke(verification, candidate)
                        }
                        catch (failure: IllegalStateException) { throw TrustHandshakeException(TrustStage.REVOKED, failure) }
                    }
                    if (!committed) throw TrustHandshakeException(TrustStage.CANCELED)
                    if (candidate != null) applyAssociation()
                    succeeded.set(true)
                    SecureHandshakeResult(keys, negotiation, replay, DeviceNameGreeting.decode(frame.payload()))
                } finally {
                    if (!succeeded.get()) runCatching(closeTransport)
                    abort.cancel(); decision.cancel(); greeting.cancel()
                }
            }
        }
        val deadline = launch {
            delay(timeoutMs)
            expired.set(true)
            requestRef.get()?.finish(TrustStage.TIMED_OUT)
            runCatching(closeTransport)
            operation.cancel()
        }
        try {
            operation.await()
        } catch (failure: Throwable) {
            val request = requestRef.get()
            val stage = when {
                expired.get() -> TrustStage.TIMED_OUT
                request?.stage?.value == TrustStage.CANCELED || failure is CancellationException -> TrustStage.CANCELED
                failure is TrustHandshakeException -> failure.stage
                failure is EOFException -> TrustStage.REMOTE_CLOSED
                else -> TrustStage.FAILED
            }
            request?.finish(stage)
            if (request != null && stage != TrustStage.CANCELED && !shown.get()) present(request)
            if (failure is CancellationException && !expired.get()) throw failure
            throw TrustHandshakeException(stage, failure)
        } finally {
            deadline.cancel()
            if (!succeeded.get()) runCatching(closeTransport)
            operation.cancel()
            withContext(NonCancellable) { operation.join() }
            if (!succeeded.get()) derivedKeys?.let { it.sendKey().fill(0); it.receiveKey().fill(0) }
            requestRef.get()?.let(finished)
        }
    }
    private fun writeHello(hello: HandshakeHello) {
        val bytes = hello.encode(); require(bytes.size in 1..4096)
        DataOutputStream(output).apply { writeInt(bytes.size); write(bytes); flush() }
    }
    private fun readHello(): HandshakeHello {
        val data = DataInputStream(input); val length = data.readInt()
        if (length !in 1..4096) throw IOException("Invalid handshake length")
        val bytes = data.readNBytes(length)
        if (bytes.size != length) throw EOFException("Truncated handshake")
        return HandshakeHello.decode(bytes)
    }
    private fun validateGreeting(frame: BtxFrame) {
        if (frame.type() == WireMessageType.GOAWAY) throw TrustHandshakeException(
            if (frame.payload().contentEquals("TRUST_REJECTED".toByteArray(Charsets.UTF_8))) TrustStage.REJECTED else TrustStage.REMOTE_CLOSED)
        if (frame.type() != WireMessageType.PROTOCOL_HELLO) throw IOException("Expected PROTOCOL_HELLO")
        ProtocolGreeting.current().negotiate(ProtocolGreeting.decode(frame.payload()))
    }
}
