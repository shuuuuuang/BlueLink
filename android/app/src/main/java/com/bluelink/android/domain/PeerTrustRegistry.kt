package com.bluelink.android.domain

import java.security.MessageDigest
import java.util.Locale

/** Couples a handshake to the trust revision observed before its confirmation. */
class PeerTrustRegistry(
    private val read: (String) -> ByteArray?,
    private val write: (String, ByteArray) -> Unit,
    private val remove: (String) -> Unit,
) {
    class Verification internal constructor(internal val peerId: String, internal val key: ByteArray,
                                            internal val revision: Long, internal val epoch: Long, val wasTrusted: Boolean, internal val observedRevisions: Map<String, Long>)
    private val revisions = mutableMapOf<String, Long>()
    private var epoch = 0L

    @Synchronized
    fun <T> snapshot(readIdentity: () -> T): Pair<T, Long> = readIdentity() to epoch

    @Synchronized
    fun begin(peerId: String, key: ByteArray, expectedEpoch: Long? = null): Verification {
        check(expectedEpoch == null || expectedEpoch == epoch) { "本机身份或信任关系已变化" }
        val id = peerId.lowercase(Locale.ROOT)
        val existing = read(id)
        if (existing != null && !MessageDigest.isEqual(existing, key))
            throw SecurityException("已信任设备的身份密钥发生变化")
        return Verification(id, key.copyOf(), revisions[id] ?: 0, epoch, existing != null, revisions.toMap())
    }

    /** Call only after the encrypted protocol greeting proves possession of the session key. */
    @Synchronized
    fun complete(verification: Verification) {
        val id = verification.peerId
        check(epoch == verification.epoch && (revisions[id] ?: 0) == verification.revision) { "设备信任已移除，请重新连接并核对安全码" }
        val existing = read(id)
        if (existing != null && !MessageDigest.isEqual(existing, verification.key))
            throw SecurityException("已信任设备的身份密钥发生变化")
        if (existing == null) write(id, verification.key.copyOf())
    }

    /** Persist the replacement and its migration journal together, after verifying both peers. */
    @Synchronized
    fun replace(verification: Verification, previousId: String, previousKey: ByteArray?, persist: () -> Unit) {
        val oldId = previousId.lowercase(Locale.ROOT)
        check(epoch == verification.epoch && (revisions[verification.peerId] ?: 0) == verification.revision) { "设备信任已变化" }
        check((revisions[oldId] ?: 0) == (verification.observedRevisions[oldId] ?: 0)) { "原设备信任已被移除" }
        check(oldId != verification.peerId && read(verification.peerId) == null) { "目标身份已存在" }
        val oldKey = read(oldId)
        check(oldKey == null || previousKey != null && MessageDigest.isEqual(oldKey, previousKey)) { "原设备身份已变化" }
        persist()
        epoch++
        revisions[oldId] = (revisions[oldId] ?: 0) + 1
    }

    /** Invalidates even peers that have not reached their first trust prompt yet. */
    @Synchronized
    fun <T> invalidateAll(change: () -> T): T {
        epoch++
        revisions.clear()
        return change()
    }

    @Synchronized
    fun revoke(peerId: String) {
        val id = peerId.lowercase(Locale.ROOT)
        revisions[id] = (revisions[id] ?: 0) + 1
        remove(id)
    }
}
