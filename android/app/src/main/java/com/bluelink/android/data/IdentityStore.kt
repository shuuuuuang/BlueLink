package com.bluelink.android.data

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import com.bluelink.core.DeviceIdentity
import java.security.MessageDigest
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class IdentityStore(context: Context) {
    private val preferences = context.getSharedPreferences("bluelink_identity", Context.MODE_PRIVATE)
    private val trustRegistry = com.bluelink.android.domain.PeerTrustRegistry(
        { id -> preferences.getString("trust.$id", null)?.let(::decode) },
        { id, key -> editIdentityPreferences { putString("trust.$id", encode(key)) } },
        { id -> editIdentityPreferences { remove("trust.$id") } },
    )

    fun beginTrustVerification(peerId: ByteArray, key: ByteArray, epoch: Long): com.bluelink.android.domain.PeerTrustRegistry.Verification {
        check(!isRetired(peerId.hex())) { "此身份已被替换" }
        return trustRegistry.begin(peerId.hex(), key, epoch)
    }
    fun identityAssociations(): Map<String, String> = preferences.all.entries
        .filter { it.key.startsWith("association.") }.mapNotNull { (key, value) ->
            (value as? String)?.let { key.removePrefix("association.") to it }
        }.toMap()
    fun isRetired(peerId: String): Boolean = preferences.contains("association.${peerId.lowercase(java.util.Locale.ROOT)}")
    fun associateIdentity(verification: com.bluelink.android.domain.PeerTrustRegistry.Verification,
                          candidate: com.bluelink.android.domain.IdentityCandidate) {
        check(!isRetired(candidate.peerId) && !isRetired(verification.peerId)) { "设备身份已被替换" }
        trustRegistry.replace(verification, candidate.peerId, candidate.publicKey) {
            editIdentityPreferences {
                remove("trust.${candidate.peerId.lowercase(java.util.Locale.ROOT)}")
                putString("trust.${verification.peerId}", encode(verification.key))
                putString("association.${candidate.peerId.lowercase(java.util.Locale.ROOT)}", verification.peerId)
            }
        }
    }
    fun captureIdentity(): com.bluelink.android.domain.IdentitySnapshot {
        val snapshot = trustRegistry.snapshot { identity }
        return com.bluelink.android.domain.IdentitySnapshot(snapshot.first, snapshot.second)
    }
    fun completeTrustVerification(verification: com.bluelink.android.domain.PeerTrustRegistry.Verification) =
        trustRegistry.complete(verification)

    private val identityLock = Any()
    @Volatile private var cachedIdentity: DeviceIdentity? = null
    val identity: DeviceIdentity get() = synchronized(identityLock) {
        cachedIdentity ?: loadIdentity().also { cachedIdentity = it }
    }

    private fun loadIdentity(): DeviceIdentity {
        val protectedPrivate = preferences.getString("identity.private.protected", null)
        val privateValue = protectedPrivate?.let(::decrypt)
            ?: preferences.getString("identity.private", null)?.let(::decode)
        val publicValue = preferences.getString("identity.public", null)
        return if (privateValue != null && publicValue != null) {
            DeviceIdentity.restore(privateValue, decode(publicValue)).also {
                if (protectedPrivate == null) preferences.edit()
                    .putString("identity.private.protected", encrypt(privateValue))
                    .remove("identity.private")
                    .apply()
            }
        } else {
            DeviceIdentity.generate().also {
                preferences.edit()
                    .putString("identity.private.protected", encrypt(it.privateKey()))
                    .putString("identity.public", encode(it.publicKey()))
                    .remove("identity.private")
                    .apply()
            }
        }
    }

    /** Persists the replacement and removal of trust in one preference transaction. */
    fun resetIdentity(): DeviceIdentity {
        val next = DeviceIdentity.generate()
        val encrypted = encrypt(next.privateKey())
        return trustRegistry.invalidateAll {
            synchronized(identityLock) {
                editIdentityPreferences {
                    putString("identity.private.protected", encrypted)
                    putString("identity.public", encode(next.publicKey()))
                    remove("identity.private")
                    preferences.all.keys.filter { it.startsWith("trust.") }.forEach(::remove)
                }
                cachedIdentity = next
                next
            }
        }
    }

    fun removeAllTrust() = trustRegistry.invalidateAll {
        editIdentityPreferences { preferences.all.keys.filter { it.startsWith("trust.") }.forEach(::remove) }
    }

    private fun editIdentityPreferences(edit: android.content.SharedPreferences.Editor.() -> Unit) {
        val previous = preferences.all
        if (!preferences.edit().apply(edit).commit()) {
            // SharedPreferences changes memory even when disk persistence fails.
            // Restore the previous in-memory snapshot as well as attempting disk recovery.
            val restored = preferences.edit().clear().apply {
                previous.forEach { (key, value) -> if (value is String) putString(key, value) }
            }.commit()
            throw java.io.IOException("无法保存设备身份或信任更改").apply {
                if (!restored) addSuppressed(java.io.IOException("旧身份的持久化恢复尚未完成"))
            }
        }
    }

    fun trustedKey(peerId: ByteArray): ByteArray? =
        preferences.getString("trust.${peerId.hex()}", null)?.let(::decode)

    fun trustedEntries(): Map<String, ByteArray> = preferences.all
        .filterKeys { it.startsWith("trust.") }
        .mapNotNull { (key, value) ->
            (value as? String)?.let { key.removePrefix("trust.") to decode(it) }
        }
        .toMap()

    fun removeTrust(peerId: String) {
        trustRegistry.revoke(peerId)
    }

    fun matchesTrustedKey(peerId: ByteArray, publicKey: ByteArray): Boolean? =
        trustedKey(peerId)?.let { MessageDigest.isEqual(it, publicKey) }

    private fun encode(value: ByteArray) = Base64.encodeToString(value, Base64.NO_WRAP)
    private fun decode(value: String) = Base64.decode(value, Base64.NO_WRAP)

    private fun encrypt(value: ByteArray): String {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, identityEncryptionKey())
        return "v1:${encode(cipher.iv)}:${encode(cipher.doFinal(value))}"
    }

    private fun decrypt(value: String): ByteArray {
        val fields = value.split(':', limit = 3)
        require(fields.size == 3 && fields[0] == "v1") { "不支持的本机身份密钥格式" }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, identityEncryptionKey(), GCMParameterSpec(128, decode(fields[1])))
        return cipher.doFinal(decode(fields[2]))
    }

    private fun identityEncryptionKey(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").run {
            init(KeyGenParameterSpec.Builder(KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setRandomizedEncryptionRequired(true)
                .build())
            generateKey()
        }
    }

    private companion object { const val KEY_ALIAS = "bluelink.identity.aes.v1" }
}

fun ByteArray.hex(): String = joinToString("") { "%02x".format(it) }
