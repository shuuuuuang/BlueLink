package com.bluelink.android.domain

enum class PrivacyAction { CLEAR_MESSAGES, CLEAR_TRANSFERS, REMOVE_ALL_TRUST, RESET_IDENTITY }

object RecordRetention {
    val choices = setOf("7d", "30d", "90d", "1y", "forever")
    fun cutoff(period: String, now: Long): Long? {
        val days = when (period) { "7d" -> 7L; "30d" -> 30L; "90d" -> 90L; "1y" -> 365L; else -> return null }
        return (now - days * 86_400_000L).coerceAtLeast(0)
    }
}
