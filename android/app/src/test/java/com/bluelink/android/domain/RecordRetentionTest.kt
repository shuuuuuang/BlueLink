package com.bluelink.android.domain

import org.junit.Assert.*
import org.junit.Test

class RecordRetentionTest {
    @Test fun retentionUsesExactDayBoundariesIncludingSevenDays() {
        val now = 400L * 86_400_000
        listOf("7d" to 7, "30d" to 30, "90d" to 90, "1y" to 365).forEach { (period, days) ->
            assertEquals(now - days * 86_400_000L, RecordRetention.cutoff(period, now))
        }
    }
    @Test fun foreverOrUnknownValuesNeverRequestDeletion() {
        assertNull(RecordRetention.cutoff("forever", Long.MAX_VALUE))
        assertNull(RecordRetention.cutoff("bad-setting", Long.MAX_VALUE))
    }
}
