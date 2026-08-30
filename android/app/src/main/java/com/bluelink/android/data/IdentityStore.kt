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

    val identity: DeviceIdentity by lazy {
        val protectedPrivate = preferences.getString("identity.private.protected", null)
        val privateValue = protectedPrivate?.let(::decrypt)
            ?: preferences.getString("identity.private", null)?.let(::decode)
        val publicValue = preferences.getString("identity.public", null)
        if (privateValue != null && publicValue != null) {
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

    fun trustedKey(peerId: ByteArray): ByteArray? =
        preferences.getString("trust.${peerId.hex()}", null)?.let(::decode)

    fun trust(peerId: ByteArray, publicKey: ByteArray) {
        preferences.edit().putString("trust.${peerId.hex()}", encode(publicKey)).apply()
    }

    fun trustedEntries(): Map<String, ByteArray> = preferences.all
        .filterKeys { it.startsWith("trust.") }
        .mapNotNull { (key, value) ->
            (value as? String)?.let { key.removePrefix("trust.") to decode(it) }
        }
        .toMap()

    fun removeTrust(peerId: String) {
        preferences.edit().remove("trust.$peerId").apply()
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
