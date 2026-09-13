package com.bluelink.android.domain

/** Persist bytes; edit whole MiB without floating-point rounding or multiplication overflow. */
internal object ReceiveLimitPolicy {
    const val MIB = 1024L * 1024L
    const val DEFAULT_BYTES = 500L * MIB
    fun parseMiB(text: String): Long? {
        val clean = text.trim()
        if (clean.isEmpty() || clean.any { it !in '0'..'9' }) return null
        val amount = clean.toLongOrNull() ?: return null
        if (amount <= 0 || amount > Long.MAX_VALUE / MIB) return null
        return amount * MIB
    }
    fun initialMiB(bytes: Long): String = ((bytes.coerceAtLeast(1) - 1) / MIB + 1).toString()
}
