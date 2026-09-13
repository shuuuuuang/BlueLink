package com.bluelink.android.session

import com.bluelink.android.domain.*
import com.bluelink.core.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.Test
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger

class SecureConnectionHandshakeTest {
    private class Peer {
        val identity = DeviceIdentity.generate()
        val trusted = ConcurrentHashMap<String, ByteArray>()
        val registry = PeerTrustRegistry({ trusted[it] }, { id, key -> trusted[id] = key }, { trusted.remove(it); Unit })
        val prompts = mutableListOf<SecurityRequest>()
        var action: (SecurityRequest) -> Unit = { it.confirm(); Unit }
        var expected = emptyList<ByteArray>()
        var candidate: IdentityCandidate? = null
        var applied = 0
        var timeout = 3000L
        fun engine(socket: Socket, listener: Boolean): SecureConnectionHandshake = SecureConnectionHandshake(
            socket.getInputStream(), socket.getOutputStream(), { socket.close() }, listener,
            { val snapshot = registry.snapshot { identity }; IdentitySnapshot(snapshot.first, snapshot.second) },
            { trusted[it.joinToString("") { b -> "%02x".format(b) }] }, { expected },
            { id, key, epoch -> registry.begin(id.joinToString("") { "%02x".format(it) }, key, epoch) }, registry::complete,
            "Test peer", UUID.randomUUID(), "test-transport", PeerPlatform.ANDROID,
            { synchronized(prompts) { prompts.add(it) }; action(it) }, {}, {}, timeout,
            findCandidate = { candidate },
            commitAssociation = { verification, old -> registry.replace(verification, old.peerId, old.publicKey) {
                trusted.remove(old.peerId); trusted[verification.peerId] = verification.key
            } }, applyAssociation = { applied++ })
    }
    private fun pair(a: Peer, b: Peer): Pair<Result<SecureHandshakeResult>, Result<SecureHandshakeResult>> = runBlocking {
        ServerSocket(0, 1, InetAddress.getLoopbackAddress()).use { server ->
            Socket(InetAddress.getLoopbackAddress(), server.localPort).use { left ->
                server.accept().use { right ->
                    left.soTimeout = 5000; right.soTimeout = 5000
                    supervisorScope {
                        val first = async { runCatching { a.engine(left, false).run() } }
                        val second = async { runCatching { b.engine(right, true).run() } }
                        first.await() to second.await()
                    }
                }
            }
        }
    }
    private fun previousPeer(a: Peer): String {
        val old = DeviceIdentity.generate()
        val id = old.peerId().joinToString("") { "%02x".format(it) }
        a.trusted[id] = old.publicKey()
        a.expected = listOf(old.publicKey())
        a.candidate = IdentityCandidate(id, "原设备", old.publicKey(), "bt:001122334455")
        return id
    }
    @Test fun identityChangeCanAssociateOnlyAfterFreshConfirmationAndRemoteGreeting() {
        val a = Peer(); val b = Peer(); val old = previousPeer(a)
        a.action = { request ->
            assertEquals(old, request.identityCandidate?.peerId)
            assertEquals(1, a.trusted.size); assertTrue(a.trusted.containsKey(old)); assertEquals(0, a.applied)
            request.confirm()
        }
        val (left, right) = pair(a, b)
        assertTrue(left.exceptionOrNull().toString(), left.isSuccess)
        assertTrue(right.isSuccess)
        assertFalse(a.trusted.containsKey(old)); assertEquals(1, a.trusted.size); assertEquals(1, a.applied)
        assertEquals(a.prompts.single().safetyCode, b.prompts.single().safetyCode)
    }
    @Test fun declinedAssociationLeavesOldIdentityAndHistoryUntouched() {
        for (remote in listOf(false, true)) {
            val a = Peer(); val b = Peer(); val old = previousPeer(a)
            if (remote) { a.action = {}; b.action = { it.cancel() } } else a.action = { it.cancel() }
            assertTrue(pair(a, b).first.isFailure)
            assertTrue(a.trusted.containsKey(old)); assertEquals(1, a.trusted.size); assertEquals(0, a.applied)
        }
    }
    @Test fun revokingOldIdentityWhileConfirmingPreventsAssociation() {
        val a = Peer(); val b = Peer(); val old = previousPeer(a)
        a.action = { a.registry.revoke(old); it.confirm() }
        assertTrue(pair(a, b).first.isFailure)
        assertTrue(a.trusted.isEmpty()); assertEquals(0, a.applied)
    }
    @Test fun expiredAssociationDoesNotChangeOldIdentity() {
        val a = Peer(); val b = Peer(); val old = previousPeer(a)
        a.action = {}; a.timeout = 300
        assertTrue(pair(a, b).first.isFailure)
        assertTrue(a.trusted.containsKey(old)); assertEquals(0, a.applied)
    }

    @Test fun bothConfirmThenCommitAndReplayContinuesAfterGreeting() {
        val a = Peer(); val b = Peer()
        val (left, right) = pair(a, b)
        assertTrue(left.exceptionOrNull().toString(), left.isSuccess)
        assertTrue(right.exceptionOrNull().toString(), right.isSuccess)
        assertEquals(1, a.trusted.size); assertEquals(1, b.trusted.size)
        val bytes = java.io.ByteArrayOutputStream()
        val sender = right.getOrThrow().keys
        BtxRecordCodec.write(bytes, BtxFrame(WireMessageType.PING, 0, 0, 1, byteArrayOf(7)), sender.sendKey(), sender.sendNoncePrefix())
        val receiver = left.getOrThrow()
        val frame = BtxRecordCodec.read(java.io.ByteArrayInputStream(bytes.toByteArray()), receiver.keys.receiveKey(), receiver.keys.receiveNoncePrefix(), receiver.replay)
        assertEquals(1L, frame.sequence()); assertArrayEquals(byteArrayOf(7), frame.payload())
        assertEquals(TrustStage.COMPLETED, a.prompts.single().stage.value)
        assertArrayEquals(left.getOrThrow().keys.sendKey(), right.getOrThrow().keys.receiveKey())
    }
    @Test fun authenticatedRejectionInterruptsAnUnansweredLocalPrompt() {
        val a = Peer().apply { action = {} }
        val b = Peer().apply { action = { it.cancel() } }
        val (left, right) = pair(a, b)
        assertEquals(TrustStage.REJECTED, (left.exceptionOrNull() as TrustHandshakeException).stage)
        assertEquals(TrustStage.CANCELED, (right.exceptionOrNull() as TrustHandshakeException).stage)
        assertTrue(a.trusted.isEmpty()); assertTrue(b.trusted.isEmpty())
        assertFalse(a.prompts.single().confirm())
    }
    @Test fun timeoutClosesBlockedReaderAndInvalidatesCodeWithoutTrust() {
        val a = Peer().apply { action = {}; timeout = 400 }
        val b = Peer()
        val (left, right) = pair(a, b)
        assertEquals(TrustStage.TIMED_OUT, (left.exceptionOrNull() as TrustHandshakeException).stage)
        assertTrue(right.isFailure); assertTrue(a.trusted.isEmpty()); assertTrue(b.trusted.isEmpty())
        assertFalse(a.prompts.single().confirm())
    }
    @Test fun knownTransportWithDifferentIdentityIsBlockedEvenWhenPeerIdChanges() {
        val a = Peer().apply { expected = listOf(DeviceIdentity.generate().publicKey()) }
        val b = Peer()
        val (left, _) = pair(a, b)
        assertEquals(TrustStage.IDENTITY_CHANGED, (left.exceptionOrNull() as TrustHandshakeException).stage)
        assertNotNull(a.prompts.single().trustedFingerprint)
        assertTrue(a.trusted.isEmpty()); assertTrue(b.trusted.isEmpty())
    }
    @Test fun firstConnectionAlwaysRequiresExactlyOneSafetyCodeConfirmation() {
        val a = Peer(); val b = Peer()
        a.action = { assertEquals(TrustStage.CONFIRM, it.stage.value); it.confirm() }
        val (left, _) = pair(a, b)
        assertTrue(left.isSuccess); assertEquals(1, a.prompts.size); assertEquals(1, b.prompts.size)
    }
    @Test fun cancelWhileWaitingNeverCommits() {
        val a = Peer().apply { action = { it.confirm(); it.cancel() } }
        val b = Peer().apply { action = {} }
        val (left, _) = pair(a, b)
        assertEquals(TrustStage.CANCELED, (left.exceptionOrNull() as TrustHandshakeException).stage)
        assertTrue(a.trusted.isEmpty()); assertTrue(b.trusted.isEmpty())
    }
}
