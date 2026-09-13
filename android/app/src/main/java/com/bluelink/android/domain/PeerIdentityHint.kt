package com.bluelink.android.domain

import java.util.Locale

/** Address evidence selects a candidate for explicit verification; it is never a trust credential. */
object PeerIdentityHint {
    fun bluetooth(address: String?): String? {
        val value = address.orEmpty().trim().replace(":", "").replace("-", "").uppercase(Locale.ROOT)
        return value.takeIf { it.matches(Regex("[0-9A-F]{12}")) && it !in setOf("000000000000", "FFFFFFFFFFFF", "020000000000") }?.let { "bt:$it" }
    }
    fun usbHost(address: String?): String? = address?.takeIf { it.startsWith("usb:BlueLink:") }
        ?.removePrefix("usb:BlueLink:")?.trim()?.takeIf { it.matches(Regex("[0-9a-fA-F]{32}")) }
        ?.let { "usb-host:${it.uppercase(Locale.ROOT)}" }
    fun fromAddress(address: String?): String? = bluetooth(address) ?: usbHost(address)
}

data class IdentityCandidate(val peerId: String, val displayName: String, val publicKey: ByteArray?, val hint: String)
